using ModelContextProtocol.Authentication;
using System.Text.Json;

internal sealed record McpBootstrapGrant(string Endpoint, string Issuer, string TokenEndpoint, string ClientId,
    string? ClientSecret, string ClientAuthMethod, string? Resource, string AccessToken,
    string? RefreshToken, DateTimeOffset? ExpiresAt, string? Scope);

// Used only by operator bootstrap; the harness protects the exported grant at rest.
internal sealed class BootstrapTokenCache : ITokenCache
{
    private TokenContainer? tokens;
    internal string? TokenEndpoint { get; set; }
    internal string? ClientAuthMethod { get; set; }
    internal string? Resource { get; set; }
    internal string? RequestedScope { get; set; }
    public ValueTask StoreTokensAsync(TokenContainer value, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref tokens, value);
        return ValueTask.CompletedTask;
    }
    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Volatile.Read(ref tokens));

    internal McpBootstrapGrant Export(string endpoint)
    {
        var grant = Volatile.Read(ref tokens) ?? throw new InvalidOperationException("No OAuth grant was issued.");
        if (string.IsNullOrWhiteSpace(TokenEndpoint) || string.IsNullOrWhiteSpace(ClientAuthMethod) ||
            string.IsNullOrWhiteSpace(grant.AuthorizationServer) || string.IsNullOrWhiteSpace(grant.ClientId) ||
            string.IsNullOrWhiteSpace(grant.AccessToken) || !string.Equals(grant.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("OAuth bootstrap did not capture the negotiated token endpoint and issuer. Reauthorize with the updated client.");
        return new(endpoint, grant.AuthorizationServer, TokenEndpoint, grant.ClientId, grant.ClientSecret,
            ClientAuthMethod, Resource, grant.AccessToken, grant.RefreshToken,
            grant.ExpiresIn is { } seconds ? grant.ObtainedAt.AddSeconds(seconds) : null, grant.Scope ?? RequestedScope);
    }
}

// Capture only negotiated routing metadata, never token response bodies or authorization codes.
internal sealed class TokenRequestCaptureHandler(BootstrapTokenCache cache) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method == HttpMethod.Post && request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
        {
            if (request.Content.Headers.ContentLength > 64 * 1024) throw new InvalidOperationException("OAuth request exceeded its limit.");
            var text = await request.Content.ReadAsStringAsync(token);
            var fields = text.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .ToDictionary(p => Decode(p[0]), p => p.Length == 2 ? Decode(p[1]) : "", StringComparer.Ordinal);
            if (fields.GetValueOrDefault("grant_type") is "authorization_code" or "refresh_token")
            {
                cache.TokenEndpoint = request.RequestUri!.AbsoluteUri;
                cache.Resource = fields.GetValueOrDefault("resource");
                cache.ClientAuthMethod = request.Headers.Authorization?.Scheme == "Basic" ? "client_secret_basic" :
                    fields.ContainsKey("client_secret") ? "client_secret_post" : "none";
            }
        }
        return await base.SendAsync(request, token);
    }
    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
