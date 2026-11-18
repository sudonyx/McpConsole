using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed record HarnessBinding(Guid ProviderId, Guid ApiDescriptionVersionId, string OperationKey, Guid CredentialBindingId);

internal static class HarnessClient
{
    private static HttpClient Create()
    {
        var address = Environment.GetEnvironmentVariable("HARNESS_BASE_URL");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Set HARNESS_BASE_URL to your trusted HTTPS or local loopback harness URL.");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri };
        var key = Environment.GetEnvironmentVariable("HARNESS_API_KEY");
        if (!string.IsNullOrEmpty(key)) client.DefaultRequestHeaders.Add("X-Harness-Api-Key", key);
        return client;
    }

    internal static async Task<HarnessBinding> RegisterAsync(ReadCapability capability, ServerSettings server,
        JsonElement toolDefinition, string accessToken, CancellationToken token)
    {
        using var client = Create();
        var providerId = capability.Harness?.ProviderId ?? Guid.NewGuid();
        Guid bindingId;
        if (capability.Harness is { } existing)
        {
            bindingId = existing.CredentialBindingId;
            using var refreshed = await client.PutAsJsonAsync($"/providers/{providerId}/credential-bindings/{bindingId}/secret",
                new { secret = accessToken }, token);
            refreshed.EnsureSuccessStatusCode();
        }
        else
        {
            using var created = await client.PostAsJsonAsync($"/providers/{providerId}/credential-bindings", new
            {
                name = "mcp-" + capability.Id, scheme = 1, headerName = "Authorization",
                secretReference = "mcp/" + Guid.NewGuid().ToString("N"), secret = accessToken
            }, token);
            created.EnsureSuccessStatusCode();
            var binding = await created.Content.ReadFromJsonAsync<JsonElement>(token);
            bindingId = binding.GetProperty("id").GetGuid();
        }
        using var registered = await client.PostAsJsonAsync($"/providers/{providerId}/mcp-descriptions", new
        {
            providerName = server.Id, endpoint = server.Endpoint, toolDefinition, arguments = capability.Arguments,
            credentialBindingId = bindingId, approvedReadOnly = capability.ApprovedReadOnly
        }, token);
        registered.EnsureSuccessStatusCode();
        return await registered.Content.ReadFromJsonAsync<HarnessBinding>(token)
            ?? throw new InvalidOperationException("Missing harness registration result.");
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
