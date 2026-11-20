using System.Security.Cryptography;
using System.Text.Json;
using KyoPrep.LocalConfiguration;

namespace KyoPrep.LocalStack;

public sealed record LocalProfile(string Name, string RepositoryRoot, string ChatConfigPath);

public static class Profiles
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly string[] Services = ["decision", "harness", "workflow", "chat"];
    public static readonly string[] DemoReads = ["hubspot.demo-companies", "hubspot.demo-contacts", "salesforce.demo-opportunities"];
    public static string ManifestPath(string name) => Path.Combine(LocalSettings.ProfileDirectory(name), "processes.json");
    public static string ProfilePath(string name) => Path.Combine(LocalSettings.ProfileDirectory(name), "profile.json");
    public static LocalProfile Load(string name)
    {
        var profile = JsonSerializer.Deserialize<LocalProfile>(File.ReadAllText(ProfilePath(name)), Json)
            ?? throw new InvalidDataException("Missing local profile. Run setup first.");
        if (profile.Name != name) throw new InvalidDataException("Profile identity mismatch.");
        return profile;
    }
    public static Dictionary<string, string> Read(string name, string service) => File.Exists(LocalSettings.SettingsPath(name, service))
        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LocalSettings.SettingsPath(name, service)), Json)!
        : new(StringComparer.OrdinalIgnoreCase);
    public static void Save(string name, string service, Dictionary<string, string> values) =>
        LocalSettings.AtomicWrite(LocalSettings.SettingsPath(name, service), JsonSerializer.Serialize(values, Json));
    public static string Project(LocalProfile profile, string service) => Path.Combine(profile.RepositoryRoot, service switch
    {
        "decision" => "DecisionApi/src/Meridian.Api/Meridian.Api.csproj",
        "harness" => "GenericHarness/Harness.Api/Harness.Api.csproj",
        "workflow" => "StateMachine/src/DurableStateMachine.Api/DurableStateMachine.Api.csproj",
        "chat" => "McpConsole/McpConsole.csproj",
        _ => throw new ArgumentException("Unknown local service.")
    });
    public static string Assembly(LocalProfile profile, string service) => Path.Combine(Path.GetDirectoryName(Project(profile, service))!,
        "bin", "Debug", "net10.0", Path.GetFileNameWithoutExtension(Project(profile, service)) + ".dll");
    public static Uri Origin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidDataException("Local service URLs must be HTTP loopback origins without paths or credentials.");
        return uri;
    }

    public static void Validate(LocalProfile profile, bool requireModel = true)
    {
        foreach (var service in Services)
            if (!File.Exists(Project(profile, service))) throw new InvalidDataException($"Cannot find the {service} project under the saved repository root.");
        if (!File.Exists(profile.ChatConfigPath)) throw new InvalidDataException("The reviewed chat configuration is missing.");
        using (var reviewed = JsonDocument.Parse(File.ReadAllText(profile.ChatConfigPath)))
        {
            var approved = reviewed.RootElement.GetProperty("capabilities").EnumerateArray()
                .Where(c => c.GetProperty("approvedReadOnly").GetBoolean()).Select(c => c.GetProperty("id").GetString()).ToArray();
            if (approved.Length != DemoReads.Length || !DemoReads.All(id => approved.Contains(id)))
                throw new InvalidDataException("This local profile requires the three reviewed demo reads. Setup does not approve additional capabilities.");
        }
        var settings = Services.ToDictionary(s => s, s => Read(profile.Name, s));
        var secrets = Services.ToDictionary(s => s, s => LocalSettings.ReadSecrets(profile.Name, s));
        var origins = Services.Where(s => s != "chat").ToDictionary(s => s, s => Origin(settings[s]["Urls"]));
        if (origins.Values.Select(u => u.Port).Distinct().Count() != 3) throw new InvalidDataException("Each service needs a different port.");
        if (settings["harness"].GetValueOrDefault("Credentials:Store") != "ProtectedFiles")
            throw new InvalidDataException("The local stack requires Windows-protected CRM credential storage.");
        static void Same(string? left, string? right, string message)
        { if (string.IsNullOrWhiteSpace(left) || left != right) throw new InvalidDataException(message); }
        Same(secrets["harness"].GetValueOrDefault("ExecutionAuthorization:ApiKey"), secrets["decision"].GetValueOrDefault("ExecutionAuthorization:ApiKey"), "Harness and DecisionApi authorization keys do not match. Run setup.");
        Same(secrets["harness"].GetValueOrDefault("Harness:Boundary:ApiKeys:0"), secrets["chat"].GetValueOrDefault("HARNESS_API_KEY"), "Harness and chat API keys do not match. Run setup.");
        Same(secrets["workflow"].GetValueOrDefault("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"), secrets["chat"].GetValueOrDefault("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"), "Workflow ingestion keys do not match. Run setup.");
        if (Origin(settings["harness"]["ExecutionAuthorization:BaseUrl"]) != origins["decision"] ||
            Origin(settings["chat"]["HARNESS_BASE_URL"]) != origins["harness"] ||
            Origin(settings["chat"]["DURABLE_STATE_MACHINE_API_URL"]) != origins["workflow"])
            throw new InvalidDataException("Service URLs disagree across local settings. Correct the profile settings.");
        if (!File.Exists(settings["decision"]["ExecutionAuthorization:PolicyPath"]))
            throw new InvalidDataException("The reviewed authorization policy is missing. Setup does not create permission grants.");
        using var policy = JsonDocument.Parse(File.ReadAllText(settings["decision"]["ExecutionAuthorization:PolicyPath"]));
        if (policy.RootElement.GetProperty("grants").ValueKind != JsonValueKind.Array ||
            string.IsNullOrWhiteSpace(policy.RootElement.GetProperty("version").GetString())) throw new InvalidDataException("Invalid authorization policy.");
        var endpoint = ModelEndpoint(settings["chat"]["CHAT_MODEL_ENDPOINT"]);
        if (!string.IsNullOrWhiteSpace(secrets["chat"].GetValueOrDefault("CHAT_MODEL_API_KEY")) &&
            secrets["chat"].GetValueOrDefault("LocalSetup:ModelOrigin") != endpoint.GetLeftPart(UriPartial.Authority))
            throw new InvalidDataException("The model endpoint no longer matches its stored API key. Run setup to configure credentials for the new endpoint.");
        if (requireModel && !endpoint.IsLoopback && string.IsNullOrWhiteSpace(secrets["chat"].GetValueOrDefault("CHAT_MODEL_API_KEY")))
            throw new InvalidDataException("The model API key is missing. Run setup.");
        if (settings["chat"].GetValueOrDefault("CHAT_EXECUTION_MODE") != "workflow")
            throw new InvalidDataException("The local stack requires workflow chat mode.");
    }

    public static void Setup(string name, string root)
    {
        var directory = LocalSettings.ProfileDirectory(name);
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.Combine(directory, "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = File.Exists(ProfilePath(name)) ? Load(name) : null;
        root = Path.GetFullPath(root);
        var previousDefault = previous is not null ? Path.Combine(previous.RepositoryRoot, "McpConsole", "chat.local.json") : null;
        var profile = new LocalProfile(name, root, previous is not null && previous.ChatConfigPath != previousDefault
            ? previous.ChatConfigPath : Path.Combine(root, "McpConsole", "chat.local.json"));
        if (!File.Exists(profile.ChatConfigPath)) throw new InvalidDataException("Create and review chat.local.json before setting up this stack.");
        var data = Path.Combine(directory, "data");
        Directory.CreateDirectory(data);
        var harness = Read(name, "harness");
        var decision = Read(name, "decision");
        var workflow = Read(name, "workflow");
        var chat = Read(name, "chat");
        var hs = LocalSettings.ReadSecrets(name, "harness");
        var ds = LocalSettings.ReadSecrets(name, "decision");
        var ws = LocalSettings.ReadSecrets(name, "workflow");
        var cs = LocalSettings.ReadSecrets(name, "chat");
        string Key(Dictionary<string, string> existing, string key) => existing.GetValueOrDefault(key) ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        hs["Harness:Boundary:ApiKeys:0"] = cs["HARNESS_API_KEY"] = Key(hs, "Harness:Boundary:ApiKeys:0");
        hs["ExecutionAuthorization:ApiKey"] = ds["ExecutionAuthorization:ApiKey"] = Key(ds, "ExecutionAuthorization:ApiKey");
        ws["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = cs["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = Key(ws, "DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        harness.TryAdd("Urls", "http://localhost:5247/");
        decision.TryAdd("Urls", "http://localhost:5267/");
        workflow.TryAdd("Urls", "http://localhost:5271/");
        harness["Credentials:Store"] = "ProtectedFiles";
        harness["ExecutionAuthorization:BaseUrl"] = decision["Urls"];
        var existingHarnessDb = Path.Combine(root, "GenericHarness", "Harness.Api", "harness.development.db");
        harness.TryAdd("ConnectionStrings:Harness", "Data Source=" + (File.Exists(existingHarnessDb) ? existingHarnessDb : Path.Combine(data, "harness.db")));
        var existingWorkflowDb = Path.Combine(Path.GetTempPath(), "mcp-chat-workflow.db");
        workflow.TryAdd("DURABLE_STATE_MACHINE_DB", File.Exists(existingWorkflowDb) ? existingWorkflowDb : Path.Combine(data, "workflow.db"));
        decision.TryAdd("ExecutionAuthorization:PolicyPath", Path.Combine(root, "DecisionApi", "training-data", "mcp-authorization", "policy.local.json"));
        decision.TryAdd("ExecutionAuthorization:HistoryPath", Path.Combine(root, "DecisionApi", "training-data", "mcp-authorization", "history"));
        chat["HARNESS_BASE_URL"] = harness["Urls"];
        chat["DURABLE_STATE_MACHINE_API_URL"] = workflow["Urls"];
        chat["CHAT_EXECUTION_MODE"] = "workflow";
        var previousEndpoint = chat.GetValueOrDefault("CHAT_MODEL_ENDPOINT");
        var defaultEndpoint = previousEndpoint ?? Environment.GetEnvironmentVariable("CHAT_MODEL_ENDPOINT") ?? "https://api.openai.com/v1/chat/completions";
        ModelEndpoint(defaultEndpoint);
        chat["CHAT_MODEL_ENDPOINT"] = Prompt("Model endpoint", defaultEndpoint);
        var selectedEndpoint = ModelEndpoint(chat["CHAT_MODEL_ENDPOINT"]);
        var oldOrigin = cs.GetValueOrDefault("LocalSetup:ModelOrigin") ?? (previousEndpoint is not null ? new Uri(previousEndpoint).GetLeftPart(UriPartial.Authority) : null);
        var originChanged = oldOrigin is not null && oldOrigin != selectedEndpoint.GetLeftPart(UriPartial.Authority);
        if (originChanged) cs.Remove("CHAT_MODEL_API_KEY");
        chat["CHAT_MODEL"] = Prompt("Model name", chat.GetValueOrDefault("CHAT_MODEL") ?? Environment.GetEnvironmentVariable("CHAT_MODEL") ?? "gpt-5.4-mini");
        var modelKey = !originChanged && !cs.ContainsKey("CHAT_MODEL_API_KEY") ? Environment.GetEnvironmentVariable("CHAT_MODEL_API_KEY") : null;
        modelKey ??= Secret(cs.ContainsKey("CHAT_MODEL_API_KEY") ? "Model API key (blank keeps stored value)" : "Model API key (blank for an unauthenticated local model)");
        if (!string.IsNullOrWhiteSpace(modelKey)) cs["CHAT_MODEL_API_KEY"] = modelKey;
        if (cs.ContainsKey("CHAT_MODEL_API_KEY")) cs["LocalSetup:ModelOrigin"] = selectedEndpoint.GetLeftPart(UriPartial.Authority);
        foreach (var prefix in new[] { "HUBSPOT", "SALESFORCE" })
        {
            chat[prefix + "_REDIRECT_URI"] = chat.GetValueOrDefault(prefix + "_REDIRECT_URI") ?? "http://localhost:8765/oauth/callback/";
            var clientId = Prompt(prefix + " client ID (optional if the CRM connection is already provisioned)",
                chat.GetValueOrDefault(prefix + "_CLIENT_ID") ?? Environment.GetEnvironmentVariable(prefix + "_CLIENT_ID") ?? "");
            if (string.IsNullOrWhiteSpace(clientId)) continue;
            chat[prefix + "_CLIENT_ID"] = clientId;
            var secret = cs.ContainsKey(prefix + "_CLIENT_SECRET") ? null : Environment.GetEnvironmentVariable(prefix + "_CLIENT_SECRET");
            secret ??= Secret(prefix + (cs.ContainsKey(prefix + "_CLIENT_SECRET") ? " client secret (blank keeps stored value)" : " client secret (blank for a public client)"));
            if (secret.Length > 0) cs[prefix + "_CLIENT_SECRET"] = secret;
            var scopes = Environment.GetEnvironmentVariable(prefix + "_SCOPES");
            if (!string.IsNullOrWhiteSpace(scopes)) chat[prefix + "_SCOPES"] = scopes;
        }
        foreach (var item in new[] { ("harness", harness, hs), ("decision", decision, ds), ("workflow", workflow, ws), ("chat", chat, cs) })
        {
            if (item.Item1 != "chat") item.Item2.TryAdd("Logging:LogLevel:Default", "Warning");
            Save(name, item.Item1, item.Item2);
            LocalSettings.SaveSecrets(name, item.Item1, item.Item3);
        }
        LocalSettings.AtomicWrite(ProfilePath(name), JsonSerializer.Serialize(profile, Json));
        Validate(profile);
        Console.WriteLine($"Saved profile '{name}' in {directory}. Internal keys are encrypted; existing policy and CRM grants were not changed.");
        Console.WriteLine("Workflow database: " + workflow["DURABLE_STATE_MACHINE_DB"]);
        Console.WriteLine("Run start to build, check readiness and open chat.");
    }

    private static string Prompt(string label, string fallback)
    {
        Console.Write(label + (fallback.Length > 0 ? $" [{fallback}]" : "") + ": ");
        return Console.ReadLine() is { Length: > 0 } value ? value : fallback;
    }
    private static Uri ModelEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.Query.Length != 0 ||
            (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback)))
            throw new InvalidDataException("Model endpoint must be HTTPS or HTTP loopback without embedded credentials.");
        return endpoint;
    }
    private static string Secret(string label)
    {
        Console.Write(label + ": ");
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine();
        return value.ToString();
    }
}
