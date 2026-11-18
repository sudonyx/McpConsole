using ModelContextProtocol.Authentication;

// Used only by the explicit operator bootstrap command; nothing is persisted to disk.
internal sealed class BootstrapTokenCache : ITokenCache
{
    private TokenContainer? tokens;
    public ValueTask StoreTokensAsync(TokenContainer value, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref tokens, value);
        return ValueTask.CompletedTask;
    }
    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Volatile.Read(ref tokens));
}
