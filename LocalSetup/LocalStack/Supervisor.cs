using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using KyoPrep.LocalConfiguration;

namespace KyoPrep.LocalStack;

public sealed record OwnedProcess(string Service, int Pid, long StartedUtcTicks, string? Url);
public sealed record ProcessManifest(string Group, List<OwnedProcess> Processes);

public static class Supervisor
{
    private static readonly string[] AllowedEnvironment = ["PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE",
        "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "COMSPEC", "PATHEXT",
        "SYSTEMDRIVE", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "DOTNET_ROOT", "DOTNET_ROOT(x86)",
        "DOTNET_CLI_HOME", "NUGET_PACKAGES", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "SSL_CERT_FILE"];

    public static ProcessStartInfo Command(LocalProfile profile, string service, IEnumerable<string> arguments, bool interactive = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(Profiles.Project(profile, service))!,
            UseShellExecute = false, CreateNoWindow = !interactive,
            RedirectStandardOutput = !interactive, RedirectStandardError = !interactive
        };
        start.Environment.Clear();
        foreach (var name in AllowedEnvironment)
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["KYO_LOCAL_HOME"] = LocalSettings.Home;
        start.Environment["KYO_LOCAL_PROFILE"] = profile.Name;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static Process? FindOwned(OwnedProcess record)
    {
        try
        {
            var process = Process.GetProcessById(record.Pid);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartedUtcTicks) return process;
            process.Dispose();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return null;
    }

    private static ProcessManifest? ReadManifest(string name) => File.Exists(Profiles.ManifestPath(name))
        ? JsonSerializer.Deserialize<ProcessManifest>(File.ReadAllText(Profiles.ManifestPath(name)), Profiles.Json) : null;
    private static void SaveManifest(string name, ProcessManifest manifest) =>
        LocalSettings.AtomicWrite(Profiles.ManifestPath(name), JsonSerializer.Serialize(manifest, Profiles.Json));

    public static void Stop(string name)
    {
        var manifest = ReadManifest(name);
        if (manifest is null) { Console.WriteLine("No launcher-owned processes are recorded."); return; }
        foreach (var record in manifest.Processes.AsEnumerable().Reverse())
        {
            using var process = FindOwned(record);
            if (process is null) continue;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { continue; }
            Console.WriteLine("Stopped " + record.Service + ".");
        }
    }

    public static async Task StatusAsync(LocalProfile profile, CancellationToken token)
    {
        var manifest = ReadManifest(profile.Name);
        if (manifest is null) { Console.WriteLine("Stack is stopped."); return; }
        foreach (var record in manifest.Processes)
        {
            using var process = FindOwned(record);
            var state = process is null ? "stopped" : record.Service == "chat" ? "running" :
                await ReadyAsync(profile, record.Service, manifest.Group, token) ? "ready" : "not ready";
            Console.WriteLine(record.Service + ": " + state);
        }
    }

    private static async Task<bool> ReadyAsync(LocalProfile profile, string service, string instance, CancellationToken token)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
        if (service == "harness") http.DefaultRequestHeaders.Add("X-Harness-Api-Key",
            LocalSettings.ReadSecrets(profile.Name, "harness")["Harness:Boundary:ApiKeys:0"]);
        try
        {
            var url = Profiles.Origin(Profiles.Read(profile.Name, service)["Urls"]);
            using var response = await http.GetAsync(new Uri(url, "/local/ready"), token);
            if (!response.IsSuccessStatusCode) return false;
            await response.Content.LoadIntoBufferAsync(8192, token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return json.RootElement.GetProperty("service").GetString() == service &&
                json.RootElement.GetProperty("profile").GetString() == profile.Name &&
                json.RootElement.GetProperty("instance").GetString() == instance;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { return false; }
    }

    private static async Task<bool> PortBusyAsync(Uri url)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync(url.Host, url.Port).WaitAsync(TimeSpan.FromSeconds(1)); return true; }
        catch (Exception ex) when (ex is SocketException or TimeoutException) { return false; }
    }

    public static async Task<int> ConnectAsync(LocalProfile profile, string? capability, CancellationToken token)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(profile.ChatConfigPath));
        var approved = config.RootElement.GetProperty("capabilities").EnumerateArray()
            .Where(c => c.GetProperty("approvedReadOnly").GetBoolean()).Select(c => c.GetProperty("id").GetString()!).ToArray();
        if (capability is not null && !approved.Contains(capability)) throw new InvalidDataException("That capability is not approved in the reviewed chat configuration.");
        foreach (var id in capability is null ? approved : [capability])
        {
            Console.WriteLine("Authorizing " + id + " (complete browser consent if prompted)...");
            var code = await InteractiveAsync(profile, ["harness-register", profile.ChatConfigPath, id], token);
            if (code != 0) return code;
        }
        return 0;
    }

    private static async Task EnsureConnectionsAsync(LocalProfile profile, CancellationToken token)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(profile.ChatConfigPath));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("X-Harness-Api-Key", LocalSettings.ReadSecrets(profile.Name, "chat")["HARNESS_API_KEY"]);
        var baseUrl = Profiles.Origin(Profiles.Read(profile.Name, "harness")["Urls"]);
        foreach (var cap in config.RootElement.GetProperty("capabilities").EnumerateArray().Where(c => c.GetProperty("approvedReadOnly").GetBoolean()))
        {
            var ready = false;
            if (cap.TryGetProperty("harness", out var binding) && binding.ValueKind == JsonValueKind.Object)
            {
                var path = $"/providers/{binding.GetProperty("providerId").GetGuid()}/credential-bindings/{binding.GetProperty("credentialBindingId").GetGuid()}/mcp-connection";
                using var response = await http.GetAsync(new Uri(baseUrl, path), token);
                if (response.IsSuccessStatusCode)
                {
                    await response.Content.LoadIntoBufferAsync(8192, token);
                    using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    var value = status.RootElement;
                    ready = value.GetProperty("mode").GetString() == "OAuth" && value.GetProperty("state").GetString() == "Ready" &&
                        (value.GetProperty("canRefresh").GetBoolean() || value.GetProperty("expiresAt").ValueKind == JsonValueKind.Null ||
                         value.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
                }
            }
            if (!ready && await ConnectAsync(profile, cap.GetProperty("id").GetString(), token) != 0)
                throw new InvalidDataException("CRM authorization needs attention. Run setup to save the OAuth app settings, then connect.");
        }
        await VerifyPolicyScopesAsync(profile, http, baseUrl, token);
    }

    private static async Task VerifyPolicyScopesAsync(LocalProfile profile, HttpClient http, Uri baseUrl, CancellationToken token)
    {
        // Verify existing reviewed grants. Never export, add or approve a permission during startup.
        using var config = JsonDocument.Parse(File.ReadAllText(profile.ChatConfigPath));
        using var policy = JsonDocument.Parse(File.ReadAllText(Profiles.Read(profile.Name, "decision")["ExecutionAuthorization:PolicyPath"]));
        foreach (var cap in config.RootElement.GetProperty("capabilities").EnumerateArray().Where(c => c.GetProperty("approvedReadOnly").GetBoolean()))
        {
            var binding = cap.GetProperty("harness");
            var path = $"/providers/{binding.GetProperty("providerId").GetGuid()}/api-descriptions/{binding.GetProperty("apiDescriptionVersionId").GetGuid()}/mcp-authorization-scopes";
            using var response = await http.GetAsync(new Uri(baseUrl, path), token);
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(64 * 1024, token);
            using var scopes = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var scope = scopes.RootElement.EnumerateArray().Single(s => s.GetProperty("operationKey").GetString() == binding.GetProperty("operationKey").GetString());
            var server = config.RootElement.GetProperty("servers").EnumerateArray().Single(s =>
                s.GetProperty("id").GetString() == cap.GetProperty("serverId").GetString());
            if (scope.GetProperty("credentialBindingId").GetGuid() != binding.GetProperty("credentialBindingId").GetGuid() ||
                scope.GetProperty("definitionHash").GetString() != cap.GetProperty("definitionHash").GetString() ||
                new Uri(scope.GetProperty("endpoint").GetString()!) != new Uri(server.GetProperty("endpoint").GetString()!))
                throw new InvalidDataException("The reviewed chat capability does not match its registered harness binding. Review the configuration before starting.");
            if (!policy.RootElement.GetProperty("grants").EnumerateArray().Any(g => JsonElement.DeepEquals(g, scope)))
                throw new InvalidDataException("An MCP binding is not covered by the reviewed policy. Review/export the policy explicitly; startup will not grant permission.");
        }
    }

    private static async Task<int> InteractiveAsync(LocalProfile profile, string[] args, CancellationToken token, Action<Process>? started = null)
    {
        var info = Command(profile, "chat", [Profiles.Assembly(profile, "chat"), .. args], interactive: true);
        using var process = Process.Start(info) ?? throw new IOException("Could not start the chat command.");
        try { started?.Invoke(process); await process.WaitForExitAsync(token); return process.ExitCode; }
        finally { if (!process.HasExited) process.Kill(); }
    }

    public static async Task StartAsync(LocalProfile profile, bool noChat, bool noBuild, CancellationToken token)
    {
        Profiles.Validate(profile);
        using var lease = new FileStream(Path.Combine(LocalSettings.ProfileDirectory(profile.Name), "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var service in Profiles.Services.Where(s => s != "chat"))
            if (await PortBusyAsync(Profiles.Origin(Profiles.Read(profile.Name, service)["Urls"])))
                throw new InvalidDataException($"The {service} port is occupied. Stop the existing service first; the launcher will not replace an unrelated process.");
        if (!noBuild)
            foreach (var service in Profiles.Services)
            {
                Console.WriteLine("Building " + service + "...");
                using var build = Process.Start(Command(profile, service, ["build", Profiles.Project(profile, service), "--nologo", "--verbosity", "quiet", "--disable-build-servers", "-m:1"]))!;
                var output = build.StandardOutput.ReadToEndAsync();
                var error = build.StandardError.ReadToEndAsync();
                try { await build.WaitForExitAsync(token); }
                finally { if (!build.HasExited) { build.Kill(entireProcessTree: true); await build.WaitForExitAsync(); } }
                if (build.ExitCode != 0) throw new InvalidDataException($"Build failed for {service}.\n" + await output + await error);
            }
        var manifest = new ProcessManifest(Guid.NewGuid().ToString("N"), []);
        var running = new List<LoggedService>();
        try
        {
            foreach (var service in Profiles.Services.Where(s => s != "chat"))
            {
                var info = Command(profile, service, [Profiles.Assembly(profile, service)]);
                info.Environment["KYO_LOCAL_INSTANCE"] = manifest.Group;
                var started = new LoggedService(info, Path.Combine(LocalSettings.ProfileDirectory(profile.Name), "logs", manifest.Group + "-" + service + ".log"));
                running.Add(started);
                manifest.Processes.Add(new(service, started.Process.Id, started.Process.StartTime.ToUniversalTime().Ticks, Profiles.Read(profile.Name, service)["Urls"]));
                SaveManifest(profile.Name, manifest);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
                while (!await ReadyAsync(profile, service, manifest.Group, token))
                {
                    if (started.Process.HasExited || DateTimeOffset.UtcNow > deadline)
                        throw new InvalidDataException($"The {service} service did not become ready. Inspect its log under the profile's logs directory.");
                    await Task.Delay(300, token);
                }
                Console.WriteLine(service + " ready at " + Profiles.Read(profile.Name, service)["Urls"]);
            }
            if (noChat)
            {
                Console.WriteLine("Services ready. Ctrl+C stops this stack. Run connect or status in another terminal if needed.");
                while (running.All(s => !s.Process.HasExited)) await Task.Delay(500, token);
            }
            else
            {
                await EnsureConnectionsAsync(profile, token);
                Console.WriteLine("Reviewed read scopes verified. Opening workflow chat; /exit stops the stack.");
                using var chatStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var chat = InteractiveAsync(profile, ["chat", profile.ChatConfigPath], chatStop.Token, process =>
                {
                    manifest.Processes.Add(new("chat", process.Id, process.StartTime.ToUniversalTime().Ticks, null));
                    SaveManifest(profile.Name, manifest);
                });
                while (!chat.IsCompleted && running.All(s => !s.Process.HasExited)) await Task.Delay(300, token);
                if (!chat.IsCompleted) chatStop.Cancel();
                if (await chat != 0) throw new InvalidDataException("Chat exited with an error. Check its message and the saved profile settings.");
            }
        }
        finally
        {
            foreach (var service in running.AsEnumerable().Reverse()) await service.DisposeAsync();
            if (ReadManifest(profile.Name)?.Group == manifest.Group) File.Delete(Profiles.ManifestPath(profile.Name));
            Console.WriteLine("Launcher-owned services stopped.");
        }
    }

    private sealed class LoggedService : IAsyncDisposable
    {
        public Process Process { get; }
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim gate = new(1);
        private readonly Task[] drains;
        private long written;
        public LoggedService(ProcessStartInfo info, string log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            writer = new StreamWriter(new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            Process = Process.Start(info) ?? throw new IOException("Could not start a local service.");
            drains = [Drain(Process.StandardOutput), Drain(Process.StandardError)];
        }
        private async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await gate.WaitAsync();
                try
                {
                    if (written < 10 * 1024 * 1024)
                    {
                        var bounded = line.Length > 16384 ? line[..16384] + " [truncated]" : line;
                        written += bounded.Length;
                        await writer.WriteLineAsync(bounded);
                        await writer.FlushAsync();
                    }
                }
                finally { gate.Release(); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
            await Task.WhenAll(drains);
            writer.Dispose(); gate.Dispose(); Process.Dispose();
        }
    }
}
