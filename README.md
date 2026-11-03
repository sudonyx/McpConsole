# Hosted MCP console client

A standalone .NET 10 CLI. It uses the official MCP C# SDK Core package (2.3.1) for Streamable HTTP, OAuth discovery, authorization-code exchange with S256 PKCE, and MCP messages. Each command opens its own session. The SDK keeps OAuth tokens in memory for the lifetime of the process.

## HubSpot setup on Windows

1. Install the .NET 10 SDK and open PowerShell in this directory.
2. In your existing HubSpot MCP auth app, add **http://localhost:8765/oauth/callback/** as a redirect URL. Keep the Inspector redirect registered if you still use it. The hostname, port, path, and trailing slash must match the value below.
3. Restore and build, then enter your existing app credentials in the same PowerShell window. The secret prompt masks input and keeps the secret out of shell command history. The client receives the secret as a process environment variable.

```powershell
dotnet restore
dotnet build --no-restore

$env:MCP_ENDPOINT = 'https://mcp.hubspot.com/'
$env:MCP_REDIRECT_URI = 'http://localhost:8765/oauth/callback/'
$env:MCP_CLIENT_ID = Read-Host 'HubSpot MCP client ID'
$env:MCP_CLIENT_SECRET = [System.Net.NetworkCredential]::new(
    '', (Read-Host 'HubSpot MCP client secret' -AsSecureString)
).Password

try {
    dotnet run --no-build -- list
    # After reviewing the advertised schema, explicitly invoke the read-only tool.
    dotnet run --no-build -- call get_user_details '{}'
} finally {
    Remove-Item Env:MCP_CLIENT_SECRET, Env:MCP_CLIENT_ID
}
```

Complete account selection and consent in the browser when prompted. The console opens a listener before launching the browser and checks the callback path and state. The SDK also validates state and checks the issuer when supplied. Supplying the registered client ID bypasses dynamic client registration. Grant only the HubSpot permissions needed for the tools you plan to test.

Use a trusted endpoint with its corresponding credentials. Keep secrets out of source files, command arguments, and logs. Successful output and tool error results can contain CRM data. Use non-sensitive arguments for command-line tests.

## Commands and checks

- `list` returns a JSON array with each tool's name, description, and input schema.
- `call TOOL [JSON_OBJECT]` checks that the tool is advertised in the current session and invokes it once. The default argument object is `{}`. For example, pass nested JSON as one single-quoted argument in PowerShell 7.3 or later. Review the advertised schema and tool behavior before calling.
- Results include the server's content, structured content when present, and error flag. An MCP tool error exits 3. Local, OAuth, transport, and protocol failures exit 1 with a stage, exception type, and sanitized message on stderr. Ctrl+C cancels with exit 130.
- Offline checks after building: `dotnet run --no-build -- --help` exits 0. `dotnet run --no-build -- call example '[]'` and `dotnet run --no-build -- call example '{'` exit 1 at input validation before accessing credentials or the network. With no endpoint set, `dotnet run --no-build -- list` exits 1 at configuration.
- Live smoke check: run the two HubSpot commands above. Confirm that `list` advertises `get_user_details`, then confirm the call returns account/user details and exits 0.

If the callback cannot bind, check whether another process is using port 8765 and whether Windows permits your user to bind the exact HTTP URL. If you change the port, register the updated URL in HubSpot and set the same value in PowerShell. A metadata or PKCE failure happens before code exchange. Check that the provider advertises S256 support and that the URL is the hosted MCP endpoint.

## Another provider

Set `MCP_ENDPOINT` to the provider's Streamable HTTP endpoint. Set `MCP_CLIENT_ID` to that provider's registered OAuth client ID and supply `MCP_CLIENT_SECRET` if required. Register the loopback redirect with that provider. `MCP_REDIRECT_URI` accepts an HTTP URL on localhost or 127.0.0.1 with an explicit port and a callback path ending in a slash. If required by the provider, set `MCP_SCOPES` to a space-separated scope list. Run `list` and use the new provider's tool names and schemas. The same code applies to Salesforce if its chosen endpoint supports this transport, OAuth discovery, S256 PKCE, and the registered loopback flow.

[HubSpot hosted MCP setup](https://developers.hubspot.com/docs/apps/developer-platform/build-apps/integrate-with-the-remote-hubspot-mcp-server) · [MCP C# SDK transport and authentication](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html) · [Pinned SDK package](https://www.nuget.org/packages/ModelContextProtocol.Core/2.3.1)

### JSON arguments from a file

Use a file to avoid shell quote handling for JSON objects.

```powershell
'{"objectType":"CONTACT","objectIds":[123],"properties":["firstname"]}' |
    Set-Content -LiteralPath args.json -Encoding utf8
dotnet run --no-build -- call get_crm_objects --json-file args.json
```
