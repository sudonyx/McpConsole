using System.Text.Json;
using System.Text.Json.Nodes;
using DurableStateMachine.McpClient;

internal sealed class WorkflowReadException(string message) : InvalidOperationException(message);

// One instance per chat question. The fixed workflow plan is outside model control.
internal sealed class WorkflowReads(ChatSettings settings, string directory, Guid requestId,
    Action<Guid?, string, object> audit,
    Func<string, McpRun, Action<ReadStep, JsonElement>, CancellationToken, Task<string>> runWorkflow)
{
    private readonly Dictionary<string, JsonElement> results = [];
    private bool started;

    internal static void Validate(ChatSettings settings)
    {
        if (settings.Capabilities.Any(c => c.ApprovedReadOnly && !McpRun.Capabilities.Contains(c.Id)) ||
            McpRun.Capabilities.Any(id => settings.Capabilities.Count(c => c.Id == id && c.ApprovedReadOnly && c.Harness is not null) != 1))
            throw new ArgumentException("Workflow chat requires exactly the three approved, harness-bound demo reads.");
    }

    internal async Task<JsonElement> ReadAsync(ReadCapability capability, CancellationToken token)
    {
        Validate(settings);
        if (!McpRun.Capabilities.Contains(capability.Id)) throw new InvalidOperationException("Capability is outside the workflow plan.");
        if (!started)
        {
            started = true;
            var workflowUrl = RuntimeSettings.Get("DURABLE_STATE_MACHINE_API_URL")
                ?? throw new ArgumentException("Set DURABLE_STATE_MACHINE_API_URL for workflow chat.");
            var harnessUrl = RuntimeSettings.Get("HARNESS_BASE_URL")
                ?? throw new ArgumentException("Set HARNESS_BASE_URL for workflow chat.");
            var run = McpRun.Create(JsonSerializer.Serialize(settings, ChatApp.Json), workflowUrl, harnessUrl);
            Directory.CreateDirectory(directory);
            var path = Path.GetFullPath(Path.Combine(directory, $"request-{requestId:N}.local.json"));
            using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            McpRun.Save(path, run, createNew: true);
            audit(requestId, "WorkflowStarted", new { run.InstanceId, path, run.WorkflowUrl, run.HarnessUrl });
            Console.WriteLine($"Workflow {run.InstanceId} | journal {path}");
            string outcome;
            var staged = new Dictionary<string, JsonElement>();
            try
            {
                outcome = await runWorkflow(path, run, (step, body) =>
                {
                    var annotated = JsonNode.Parse(body.GetRawText())!.AsObject();
                    annotated["_harnessExecutionId"] = step.ExecutionId;
                    annotated["_workflowInstanceId"] = run.InstanceId;
                    staged.Add(step.Binding.Capability, JsonSerializer.SerializeToElement(annotated, ChatApp.Json));
                }, token);
            }
            catch
            {
                audit(requestId, "WorkflowInterrupted", new { run.InstanceId, path });
                Console.WriteLine("Workflow interrupted. Preserve its journal for inspection; no verified chat summary was released.");
                throw;
            }
            audit(requestId, "WorkflowOutcome", new { run.InstanceId, path, outcome,
                executions = run.Steps.Select(s => new { s.Binding.Capability, s.ExecutionId }) });
            Console.WriteLine("Workflow outcome: " + outcome);
            if (outcome != "Completed")
                throw new WorkflowReadException($"Workflow {outcome}. No combined answer was produced; inspect the saved workflow journal.");
            if (McpRun.Capabilities.Any(id => !staged.ContainsKey(id)))
                throw new WorkflowReadException("Workflow completed, but some original read results are unavailable. No replacement reads or combined answer were produced.");
            foreach (var item in staged)
            {
                // Retain the existing chat evidence contract, including all reads triggered by the fixed plan.
                audit(requestId, "WorkflowReadResult", new { run.InstanceId, capabilityId = item.Key,
                    resultHash = ChatApp.Hash(item.Value), result = item.Value });
                results.Add(item.Key, item.Value);
            }
        }
        if (!results.TryGetValue(capability.Id, out var result))
            throw new WorkflowReadException("This workflow did not produce a verified result. Inspect its saved journal.");
        return result;
    }

    internal static async Task<string> ExecuteAsync(string path, McpRun run,
        Action<ReadStep, JsonElement> receive, CancellationToken token)
    {
        var key = RuntimeSettings.Get("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Set DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY for workflow chat.");
        using var workflow = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri(run.WorkflowUrl), Timeout = TimeSpan.FromSeconds(60) };
        using var harness = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = new Uri(run.HarnessUrl), Timeout = TimeSpan.FromSeconds(60) };
        workflow.DefaultRequestHeaders.Add("X-State-Machine-Trusted-Ingestion-Key", key);
        var harnessKey = RuntimeSettings.Get("HARNESS_API_KEY");
        if (!string.IsNullOrWhiteSpace(harnessKey)) harness.DefaultRequestHeaders.Add("X-Harness-Api-Key", harnessKey);
        return await new McpCoordinator(workflow, harness).ResumeAsync(path, run, token, receive);
    }
}
