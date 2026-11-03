using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

const string usage = "Usage: dotnet run -- list | call TOOL [JSON_OBJECT | --json-file PATH]";
if (args is [] or ["--help"])
{
    Console.WriteLine(usage);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var stage = "input";
var json = new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true };
try
{
    bool list = args is ["list"];
    if (!list && (args[0] != "call" || args.Length < 2 || (args.Length >= 3 && args[2] == "--json-file" ? args.Length != 4 : args.Length > 3) ||
        string.IsNullOrWhiteSpace(args[1])))
        throw new ArgumentException(usage);

    Dictionary<string, object?>? arguments = null;
    if (!list)
    {
        var input = args.Length switch
        {
            4 => File.ReadAllText(args[3]),
            3 => args[2],
            _ => "{}"
        };
        using var document = JsonDocument.Parse(input);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool arguments must be a JSON object.");
        arguments = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name, property => (object?)property.Value.Clone());
    }

    stage = "configuration";
    if (!Uri.TryCreate(Required("MCP_ENDPOINT"), UriKind.Absolute, out var endpoint) ||
        endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
        throw new ArgumentException("MCP_ENDPOINT must be an HTTPS endpoint without user info or a fragment.");

    var redirectValue = Environment.GetEnvironmentVariable("MCP_REDIRECT_URI")
        ?? "http://localhost:8765/oauth/callback/";
    if (!Uri.TryCreate(redirectValue, UriKind.Absolute, out var redirect) ||
        redirect.Scheme != "http" || redirect.Host is not ("localhost" or "127.0.0.1") ||
        redirect.IsDefaultPort || redirect.AbsolutePath == "/" ||
        !redirect.AbsolutePath.EndsWith('/') || redirect.Query.Length != 0 ||
        redirect.Fragment.Length != 0 || redirect.UserInfo.Length != 0 ||
        redirect.AbsoluteUri != redirectValue)
        throw new ArgumentException("MCP_REDIRECT_URI must be a canonical HTTP localhost or 127.0.0.1 URL with an explicit port and a callback path ending in /.");

    var oauth = new ClientOAuthOptions
    {
        ClientId = Required("MCP_CLIENT_ID"),
        ClientSecret = Environment.GetEnvironmentVariable("MCP_CLIENT_SECRET"),
        RedirectUri = redirect,
        Scopes = Environment.GetEnvironmentVariable("MCP_SCOPES")
            ?.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        AuthorizationCallbackHandler = (context, token) =>
            ReceiveCallbackAsync(context.AuthorizationUri, redirect, token)
    };

    stage = "connect";
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = endpoint,
        TransportMode = HttpTransportMode.StreamableHttp,
        OAuth = oauth,
        ConnectionTimeout = TimeSpan.FromMinutes(5)
    });
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellation.Token);

    stage = "list";
    var tools = await client.ListToolsAsync(cancellationToken: cancellation.Token);
    if (list)
    {
        Console.WriteLine(JsonSerializer.Serialize(tools.Select(tool => new
        {
            tool.Name, tool.Description, InputSchema = tool.JsonSchema
        }), json));
        return 0;
    }

    stage = "call";
    if (!tools.Any(tool => tool.Name == args[1]))
        throw new ArgumentException("Tool was not advertised by this server. Run list to see available tools.");

    var result = await client.CallToolAsync(args[1], arguments, cancellationToken: cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return result.IsError == true ? 3 : 0;
}
catch (Exception ex)
{
    // SDK exceptions may contain OAuth URLs or HTTP response bodies.
    var message = ex switch
    {
        OperationCanceledException => "Operation cancelled or timed out.",
        JsonException when stage == "input" => "Invalid JSON arguments.",
        IOException or UnauthorizedAccessException when stage == "input" => "Could not read the JSON argument file.",
        ArgumentException when stage is "input" or "configuration" => ex.Message,
        ArgumentException when stage == "call"
            => "Invalid tool request. Check the advertised tool name and schema.",
        _ => "Request failed. Check the endpoint, OAuth registration, and tool permissions."
    };
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        stage, error = ex.GetType().Name, message
    }, json));
    return cancellation.IsCancellationRequested ? 130 : 1;
}

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"Set {name} before running the client.");

static async Task<AuthorizationResult?> ReceiveCallbackAsync(
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

