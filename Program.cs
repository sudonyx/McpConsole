using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

const string usage = "Usage: dotnet run -- list | call TOOL [JSON_OBJECT | --json-file PATH] | discover CONFIG OUTPUT | chat CONFIG | harness-register CONFIG CAPABILITY | harness-status CONFIG | harness-disconnect CONFIG CAPABILITY";
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
    if (args is ["self-test"]) return ChatApp.SelfTest();
    if (args is ["harness-status", var statusConfig]) return await ChatApp.ConnectionCommandAsync(statusConfig, null, cancellation.Token);
    if (args is ["harness-disconnect", var disconnectConfig, var disconnectId]) return await ChatApp.ConnectionCommandAsync(disconnectConfig, disconnectId, cancellation.Token);
    if (args is ["harness-register", var harnessConfig, var capabilityId]) return await ChatApp.RegisterHarnessAsync(harnessConfig, capabilityId, cancellation.Token);
    if (args is ["demo-summary", var capturePath]) return ChatApp.ReplayDemo(capturePath);
    if (args is ["discover", var configPath, var outputPath]) return await ChatApp.DiscoverAsync(configPath, outputPath, cancellation.Token);
    if (args is ["chat", var chatConfig]) return await ChatApp.RunAsync(chatConfig, cancellation.Token);
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
            OAuthCallback.ReceiveAsync(context.AuthorizationUri, redirect, token)
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
