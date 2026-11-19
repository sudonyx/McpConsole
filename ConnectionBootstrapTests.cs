using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol.Authentication;

internal static class ConnectionBootstrapTests
{
    internal static async Task RunAsync()
    {
        foreach (var method in new[] { "client_secret_post", "client_secret_basic", "none" })
        {
            var cache = new BootstrapTokenCache { RequestedScope = "crm.read" };
            using var client = new HttpClient(new TokenRequestCaptureHandler(cache) { InnerHandler = new TokenReply() });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://issuer.example.com/token");
            var fields = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = "private-one-time-code",
                ["client_id"] = "client", ["resource"] = "https://mcp.example.com/" };
            if (method == "client_secret_post") fields["client_secret"] = "private-client-secret";
            if (method == "client_secret_basic") request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "test-header");
            request.Content = new FormUrlEncodedContent(fields);
            using var response = await client.SendAsync(request);
            await cache.StoreTokensAsync(new TokenContainer
            {
                AccessToken = "private-access", RefreshToken = "private-refresh", ClientId = "client", ClientSecret = "private-client-secret",
                TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow, ExpiresIn = 3600, AuthorizationServer = "https://issuer.example.com/"
            });
            var grant = cache.Export("https://mcp.example.com/");
            if (grant.ClientAuthMethod != method || grant.TokenEndpoint != "https://issuer.example.com/token" ||
                grant.Resource != "https://mcp.example.com/" || grant.Scope != "crm.read" || grant.RefreshToken != "private-refresh" ||
                grant.ExpiresAt is null || JsonSerializer.Serialize(grant).Contains("private-one-time-code"))
                throw new Exception("OAuth bootstrap did not preserve negotiated connection metadata.");
        }
        Console.WriteLine("Passed OAuth bootstrap endpoint, client authentication, expiry and scope capture checks.");
    }
    private sealed class TokenReply : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
