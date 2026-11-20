using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed record HarnessBinding(Guid ProviderId, Guid ApiDescriptionVersionId, string OperationKey, Guid CredentialBindingId);

internal static class HarnessClient
{
    private static HttpClient Create()
    {
        var address = RuntimeSettings.Get("HARNESS_BASE_URL");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Set HARNESS_BASE_URL to your trusted HTTPS or local loopback harness URL.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri };
        var key = RuntimeSettings.Get("HARNESS_API_KEY");
        if (!string.IsNullOrEmpty(key)) client.DefaultRequestHeaders.Add("X-Harness-Api-Key", key);
        return client;
    }

    internal static async Task<HarnessBinding> RegisterAsync(ReadCapability capability, ServerSettings server,
        JsonElement toolDefinition, McpBootstrapGrant grant, CancellationToken token)
    {
        using var client = Create();
        var providerId = capability.Harness?.ProviderId ?? Guid.NewGuid();
        Guid bindingId;
        if (capability.Harness is { } existing)
        {
            bindingId = existing.CredentialBindingId;
        }
        else
        {
            using var created = await client.PostAsJsonAsync($"/providers/{providerId}/credential-bindings", new
            {
                name = "mcp-" + capability.Id, scheme = 1, headerName = "Authorization",
                secretReference = "mcp/" + Guid.NewGuid().ToString("N")
            }, token);
            created.EnsureSuccessStatusCode();
            var binding = await created.Content.ReadFromJsonAsync<JsonElement>(token);
            bindingId = binding.GetProperty("id").GetGuid();
        }
        using var connection = await client.PutAsJsonAsync($"/providers/{providerId}/credential-bindings/{bindingId}/mcp-connection", grant, token);
        connection.EnsureSuccessStatusCode();
        using var registered = await client.PostAsJsonAsync($"/providers/{providerId}/mcp-descriptions", new
        {
            providerName = server.Id, endpoint = server.Endpoint, toolDefinition, arguments = capability.Arguments,
            credentialBindingId = bindingId, approvedReadOnly = capability.ApprovedReadOnly
        }, token);
        registered.EnsureSuccessStatusCode();
        return await registered.Content.ReadFromJsonAsync<HarnessBinding>(token)
            ?? throw new InvalidOperationException("Missing harness registration result.");
    }

    internal static async Task EnsureConnectionStoreAsync(CancellationToken token)
    {
        using var client = Create();
        using var response = await client.GetAsync("/mcp-connections/storage", token);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new ArgumentException("Set HARNESS_API_KEY to match the configured harness API key.");
        if (!response.IsSuccessStatusCode) throw new ArgumentException("Update and restart the harness with Credentials__Store=ProtectedFiles before OAuth registration.");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        if (!body.GetProperty("persistent").GetBoolean())
            throw new ArgumentException("Restart the harness with Credentials__Store=ProtectedFiles before OAuth registration.");
        if (!body.GetProperty("managementProtected").GetBoolean())
            throw new ArgumentException("Configure Harness__Boundary__ApiKeys__0 on the harness and matching HARNESS_API_KEY here before OAuth registration.");
    }

    internal static async Task<string> ConnectionAsync(ReadCapability capability, bool disconnect, CancellationToken token)
    {
        using var client = Create();
        var binding = capability.Harness!;
        var path = $"/providers/{binding.ProviderId}/credential-bindings/{binding.CredentialBindingId}/mcp-connection";
        if (disconnect)
        {
            using var deleted = await client.DeleteAsync(path, token);
            deleted.EnsureSuccessStatusCode();
            return "Disconnected locally; provider consent is unchanged.";
        }
        using var response = await client.GetAsync(path, token);
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return status.GetRawText();
    }

    internal static async Task<JsonElement> ExecuteAsync(ReadCapability capability, CancellationToken token)
    {
        var binding = capability.Harness ?? throw new InvalidOperationException("Missing harness binding.");
        using var client = Create();
        using var response = await client.PostAsJsonAsync("/executions", new
        {
            binding.ProviderId, binding.ApiDescriptionVersionId, binding.OperationKey, binding.CredentialBindingId,
            idempotencyKey = Guid.NewGuid().ToString("N"), maxAttempts = 1
        }, token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, token);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        var execution = result.GetProperty("execution");
        var id = execution.GetProperty("id").GetGuid();
        Console.WriteLine($"Harness execution {id}");
        if (result.GetProperty("providerResponse").ValueKind != JsonValueKind.Object)
            return JsonSerializer.SerializeToElement(new
            {
                isError = true,
                content = new[] { new { type = "text", text = "Harness execution returned no validated output. Inspect the recorded harness execution." } },
                _harnessExecutionId = id,
                _harnessExecutionState = execution.GetProperty("state").GetInt32()
            });
        var body = result.GetProperty("providerResponse").GetProperty("body");
        var node = JsonNode.Parse(body.GetRawText())!.AsObject();
        node["_harnessExecutionId"] = id.ToString();
        return JsonSerializer.SerializeToElement(node);
    }
}
