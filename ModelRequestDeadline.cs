internal sealed class ModelRequestTimeoutException(TimeSpan timeout)
    : TimeoutException("The model request timed out.")
{
    internal double TimeoutSeconds => timeout.TotalSeconds;
}

internal static class ModelRequestDeadline
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> request,
        CancellationToken sessionToken, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        deadline.CancelAfter(limit);
        try { return await request(deadline.Token); }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            // Keep transport timeout details (which may contain remote data) out of output/audit.
            // User/session cancellation must retain its existing shutdown behavior.
            throw new ModelRequestTimeoutException(limit);
        }
    }
}
