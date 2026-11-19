using System.Text.Json;
using DurableStateMachine.McpClient;

internal static class WorkflowReadTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chat-workflow-check-" + Guid.NewGuid().ToString("N"));
        var previousWorkflow = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL");
        var previousHarness = Environment.GetEnvironmentVariable("HARNESS_BASE_URL");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL", "http://localhost:5271/");
        Environment.SetEnvironmentVariable("HARNESS_BASE_URL", "http://localhost:5247/");
        var settings = new ChatSettings([new("demo", "DEMO", "https://mcp.example.com/")],
            McpRun.Capabilities.Select(id => new ReadCapability(id, "demo", "read", new string('A', 64),
                "Read demo", true, JsonSerializer.SerializeToElement(new { }),
                new HarnessBinding(Guid.NewGuid(), Guid.NewGuid(), "MCP:read", Guid.NewGuid()))).ToArray());
        try
        {
            var calls = 0;
            var events = new List<string>();
            var reads = new WorkflowReads(settings, directory, Guid.NewGuid(), (_, kind, _) => events.Add(kind),
                (path, run, receive, _) =>
                {
                    if (!File.Exists(path)) throw new Exception("Workflow dispatched without a saved plan.");
                    calls++;
                    foreach (var step in run.Steps)
                    {
                        step.ExecutionId = Guid.NewGuid().ToString();
                        receive(step, JsonSerializer.SerializeToElement(new { marker = "transient-only" }));
                    }
                    return Task.FromResult("Completed");
                });
            foreach (var cap in settings.Capabilities.Reverse().Concat(settings.Capabilities))
            {
                var result = await reads.ReadAsync(cap, default);
                if (!result.TryGetProperty("_workflowInstanceId", out _) || !result.TryGetProperty("_harnessExecutionId", out _))
                    throw new Exception("Workflow result lost execution correlation.");
            }
            if (calls != 1 || events.Count(e => e == "WorkflowReadResult") != 3)
                throw new Exception("Repeated capability proposals caused duplicate workflow dispatch.");
            if (Directory.GetFiles(directory, "*.local.json").Any(p => File.ReadAllText(p).Contains("transient-only")))
                throw new Exception("Workflow journal persisted a transient CRM body.");

            foreach (var outcome in new[] { "Blocked", "Failed", "WaitingForReconciliation", "Completed" })
            {
                var stoppedCalls = 0;
                var incomplete = new WorkflowReads(settings, directory, Guid.NewGuid(), (_, _, _) => { },
                    (_, run, receive, _) =>
                    {
                        stoppedCalls++;
                        receive(run.Steps[0], JsonSerializer.SerializeToElement(new { marker = "partial" }));
                        return Task.FromResult(outcome);
                    });
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    try { await incomplete.ReadAsync(settings.Capabilities[0], default); throw new Exception("Incomplete workflow released CRM data."); }
                    catch (WorkflowReadException) { }
                }
                if (stoppedCalls != 1) throw new Exception("Stopped workflow automatically dispatched a replacement.");
            }

            var failedAudit = new WorkflowReads(settings, directory, Guid.NewGuid(), (_, _, _) => throw new IOException("Audit unavailable"),
                (_, _, _, _) => throw new Exception("Workflow ran without a persisted audit intent."));
            try { await failedAudit.ReadAsync(settings.Capabilities[0], default); throw new Exception("Audit failure ignored."); }
            catch (IOException) { }
            foreach (var invalid in new[]
            {
                settings with { Capabilities = settings.Capabilities.Skip(1).ToArray() },
                settings with { Capabilities = settings.Capabilities.Select(c => c with { Harness = null }).ToArray() },
                settings with { Capabilities = settings.Capabilities.Select(c => c with { ApprovedReadOnly = false }).ToArray() }
            })
            {
                try { WorkflowReads.Validate(invalid); throw new Exception("Incomplete/unapproved workflow accepted."); }
                catch (ArgumentException) { }
            }
            Console.WriteLine("Passed chat workflow gating, correlation, cached reads, no-data recovery and audit-failure checks.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL", previousWorkflow);
            Environment.SetEnvironmentVariable("HARNESS_BASE_URL", previousHarness);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
