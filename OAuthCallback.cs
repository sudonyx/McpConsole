using System.Diagnostics;
using System.Net;
using ModelContextProtocol.Authentication;

internal static class OAuthCallback
{
public static async Task<AuthorizationResult?> ReceiveAsync(
    Uri authorizationUri, Uri redirect, CancellationToken token)
{
    using var listener = new HttpListener();
    listener.Prefixes.Add(redirect.AbsoluteUri);
    try
    {
        listener.Start();
    }
    catch (HttpListenerException)
    {
        Console.Error.WriteLine("Could not bind the OAuth callback. Check the port and Windows HTTP URL permissions.");
        throw;
    }

    var expectedState = ParseQuery(authorizationUri.Query)["state"];
    if (string.IsNullOrEmpty(expectedState))
        throw new InvalidOperationException("The authorization request is missing state.");

    Console.Error.WriteLine("Complete authorization in your browser.");
    Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true })?.Dispose();

    while (true)
    {
        var callback = await listener.GetContextAsync().WaitAsync(token);
        var query = callback.Request.QueryString;
        bool valid = callback.Request.HttpMethod == "GET"
            && callback.Request.Url?.AbsolutePath == redirect.AbsolutePath
            && query.GetValues("state") is { Length: 1 } states && states[0] == expectedState;
        bool authorized = valid && query["error"] is null
            && query.GetValues("code") is { Length: 1 } codes && !string.IsNullOrEmpty(codes[0])
            && (query.GetValues("iss")?.Length ?? 0) <= 1;

        using var response = callback.Response;
        response.StatusCode = authorized ? 200 : 400;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        var body = System.Text.Encoding.UTF8.GetBytes(authorized
            ? "Callback received. Return to the console for the result."
            : "Authorization callback rejected. Check the console.");
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, token);
        if (authorized)
            return new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
        if (valid)
        {
            Console.Error.WriteLine("The authorization server denied access or returned an incomplete callback.");
            return null;
        }
    }
}

static System.Collections.Specialized.NameValueCollection ParseQuery(string query) =>
    System.Web.HttpUtility.ParseQueryString(query);


}
