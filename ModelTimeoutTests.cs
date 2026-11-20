using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

internal static class ModelTimeoutTests
{
    internal static async Task RunAsync()
    {
        // A timeout must be local to the model boundary; session cancellation stays cancellation.
        try
        {
            await ModelRequestDeadline.RunAsync<int>(async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; },
                default, TimeSpan.FromMilliseconds(20));
            throw new Exception("Model deadline was ignored.");
        }
        catch (ModelRequestTimeoutException) { }
        using (var http = new HttpClient(new SlowBodyModel()) { Timeout = Timeout.InfiniteTimeSpan })
        {
            try
            {
                await ModelRequestDeadline.RunAsync(async ct =>
                {
                    using var response = await http.GetAsync("https://model.example.com/chat", HttpCompletionOption.ResponseHeadersRead, ct);
                    await response.Content.LoadIntoBufferAsync(256000, ct);
                    return 0;
                }, default, TimeSpan.FromMilliseconds(30));
                throw new Exception("A stalled model response body escaped the deadline.");
            }
            catch (ModelRequestTimeoutException) { }
        }
        using (var session = new CancellationTokenSource())
        {
            session.Cancel();
            try
            {
                await ModelRequestDeadline.RunAsync<int>(ct => Task.FromCanceled<int>(ct), session.Token);
                throw new Exception("Session cancellation was ignored.");
            }
            catch (OperationCanceledException) { }
        }
        await VerifyChatLoopAsync(cancelSession: false);
        await VerifyChatLoopAsync(cancelSession: true);
        Console.WriteLine("Passed model-timeout recovery, continued chat, timeout audit and user-cancellation checks. No network or CRM calls made.");
    }

    private static async Task VerifyChatLoopAsync(bool cancelSession)
    {
        var directory = Path.Combine(Path.GetTempPath(), "model-timeout-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "chat.local.json");
        var config = new ChatSettings([new("demo", "DEMO", "https://mcp.example.com/")],
            [new("demo.read", "demo", "read", "reviewed-hash", "Read demo", true, JsonSerializer.SerializeToElement(new { }),
                new HarnessBinding(Guid.NewGuid(), Guid.NewGuid(), "MCP:read", Guid.NewGuid()))]);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, ChatApp.Json));
        var names = new[] { "CHAT_MODEL_ENDPOINT", "CHAT_MODEL", "CHAT_EXECUTION_MODE" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var input = Console.In;
        var output = Console.Out;
        using var captured = new StringWriter();
        using var session = new CancellationTokenSource();
        var handler = new ScriptedModel(cancelSession ? session : null);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            Environment.SetEnvironmentVariable("CHAT_MODEL_ENDPOINT", "https://model.example.com/chat");
            Environment.SetEnvironmentVariable("CHAT_MODEL", "test-model");
            Environment.SetEnvironmentVariable("CHAT_EXECUTION_MODE", "harness");
            Console.SetIn(new StringReader("First question\nSecond question\n/exit\n"));
            Console.SetOut(captured);
            if (cancelSession)
            {
                try { await ChatApp.RunAsync(configPath, session.Token, client, TimeSpan.FromSeconds(5)); throw new Exception("Cancelled chat stayed active."); }
                catch (OperationCanceledException) { }
                if (handler.Calls != 1) throw new Exception("Session cancellation started another model request.");
            }
            else
            {
                var exit = await ChatApp.RunAsync(configPath, session.Token, client, TimeSpan.FromMilliseconds(100));
                if (exit != 0 || handler.Calls != 2 || !captured.ToString().Contains("chat is still open") ||
                    !captured.ToString().Contains("second question handled"))
                    throw new Exception("A model timeout ended chat or prevented the next question.");
            }
            var audit = Directory.GetFiles(Path.Combine(directory, "audit"), "session-*.jsonl").Single();
            var records = File.ReadAllLines(audit).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var kinds = records.Select(record => record.GetProperty("kind").GetString()).ToArray();
            if (kinds.Contains("DispatchStarted") || kinds.Contains("WorkflowStarted"))
                throw new Exception("Model timeout dispatched or retried a CRM operation.");
            if (cancelSession)
            {
                if (!kinds.Contains("RequestCancelled") || kinds.Contains("ModelRequestTimedOut"))
                    throw new Exception("User cancellation was classified as model timeout.");
            }
            else if (kinds.Count(k => k == "ModelRequestTimedOut") != 1 || kinds.Contains("RequestCancelled") ||
                !kinds.Contains("SessionEnded") || kinds.Count(k => k == "RequestStarted") != 2)
                throw new Exception("Recovered model timeout has incorrect audit/lifecycle evidence.");
        }
        finally
        {
            Console.SetIn(input);
            Console.SetOut(output);
            foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ScriptedModel(CancellationTokenSource? session) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Calls == 1)
            {
                session?.Cancel();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
            { choices = new[] { new { message = new { content = "{\"refusal\":\"second question handled\"}" } } } }) };
        }
    }

    private sealed class SlowBodyModel : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowBody() });
    }
    private sealed class SlowBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new Exception("Response buffering must pass its cancellation token.");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token);
    }
}
