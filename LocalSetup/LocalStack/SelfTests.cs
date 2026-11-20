using System.Diagnostics;
using System.Text.Json;
using KyoPrep.LocalConfiguration;
using Microsoft.Extensions.Configuration;

namespace KyoPrep.LocalStack;

public static class SelfTests
{
    public static Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected setup tests require Windows.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var canonicalPath = Path.Combine(repositoryRoot, "McpConsole", "LocalSetup", "Shared", "LocalSettings.cs");
        if (File.Exists(canonicalPath))
        {
            var canonical = File.ReadAllText(canonicalPath);
            foreach (var copy in new[] { "GenericHarness/Harness.Api", "DecisionApi/src/Meridian.Api", "StateMachine/src/DurableStateMachine.Api" })
                Check(File.ReadAllText(Path.Combine(repositoryRoot, copy, "LocalConfiguration", "LocalSettings.cs")) == canonical,
                    "Local configuration bootstrap copies disagree.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "local-stack-check-" + Guid.NewGuid().ToString("N"));
        var previousHome = Environment.GetEnvironmentVariable("KYO_LOCAL_HOME");
        var previousProfile = Environment.GetEnvironmentVariable("KYO_LOCAL_PROFILE");
        var previousInput = Console.In;
        var variables = new[] { "CHAT_MODEL_API_KEY", "CHAT_MODEL_ENDPOINT", "CHAT_MODEL", "HUBSPOT_CLIENT_ID", "HUBSPOT_CLIENT_SECRET",
            "SALESFORCE_CLIENT_ID", "SALESFORCE_CLIENT_SECRET", "HUBSPOT_SCOPES", "SALESFORCE_SCOPES", "ExecutionAuthorization__ApiKey" };
        var previous = variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("KYO_LOCAL_HOME", Path.Combine(directory, "home"));
            Environment.SetEnvironmentVariable("KYO_LOCAL_PROFILE", null);
            foreach (var variable in variables) Environment.SetEnvironmentVariable(variable, null);
            var root = Path.Combine(directory, "repos");
            var profile = new LocalProfile("test", root, Path.Combine(root, "McpConsole", "chat.local.json"));
            foreach (var service in Profiles.Services)
            {
                var project = Profiles.Project(profile, service);
                Directory.CreateDirectory(Path.GetDirectoryName(project)!);
                File.WriteAllText(project, "<Project />");
            }
            File.WriteAllText(profile.ChatConfigPath, JsonSerializer.Serialize(new
            { capabilities = Profiles.DemoReads.Select(id => new { id, approvedReadOnly = true }) }));
            var policy = Path.Combine(root, "DecisionApi", "training-data", "mcp-authorization", "policy.local.json");
            Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
            const string originalPolicy = "{\"version\":\"reviewed-test\",\"grants\":[]}";
            File.WriteAllText(policy, originalPolicy);
            Console.SetIn(new StringReader("http://localhost:5290/v1/chat/completions\nscripted-test\n\n\n\n"));
            Profiles.Setup("test", root);
            profile = Profiles.Load("test");
            Profiles.Validate(profile);
            Check(File.ReadAllText(policy) == originalPolicy, "Setup modified permission grants.");
            var hs = LocalSettings.ReadSecrets("test", "harness");
            var ds = LocalSettings.ReadSecrets("test", "decision");
            var cs = LocalSettings.ReadSecrets("test", "chat");
            Check(hs["ExecutionAuthorization:ApiKey"] == ds["ExecutionAuthorization:ApiKey"], "Shared authorization keys disagree.");
            Check(ds.Count == 1 && !ds.ContainsKey("CHAT_MODEL_API_KEY"), "Decision service received unrelated secrets.");
            foreach (var service in Profiles.Services)
            {
                var text = File.ReadAllText(LocalSettings.SettingsPath("test", service));
                Check(!text.Contains(hs["ExecutionAuthorization:ApiKey"]) && !text.Contains(cs["HARNESS_API_KEY"]), "Plain settings contain a secret.");
                var ciphertext = File.ReadAllText(LocalSettings.SecretsPath("test", service));
                Check(!ciphertext.Contains(cs["HARNESS_API_KEY"]), "Secret store contains plaintext.");
            }

            var chatPath = LocalSettings.SecretsPath("test", "chat");
            var harnessPath = LocalSettings.SecretsPath("test", "harness");
            var chatCipher = File.ReadAllText(chatPath);
            File.WriteAllText(chatPath, File.ReadAllText(harnessPath));
            try { LocalSettings.ReadSecrets("test", "chat"); throw new Exception("Swapped secret file was accepted."); }
            catch (InvalidDataException) { }
            File.WriteAllText(chatPath, chatCipher);
            Environment.SetEnvironmentVariable("KYO_LOCAL_PROFILE", "test");
            Environment.SetEnvironmentVariable("CHAT_MODEL", "environment-override");
            using (var configuration = new ConfigurationManager())
            {
                LocalSettings.AddTo(configuration, "chat", Path.Combine(root, "McpConsole"), ["--CHAT_MODEL", "command-override"]);
                Check(configuration["CHAT_MODEL"] == "command-override", "Configuration precedence changed.");
                Check(configuration["HARNESS_API_KEY"] == cs["HARNESS_API_KEY"], "Service could not load its own protected secret.");
            }
            Environment.SetEnvironmentVariable("CHAT_MODEL_API_KEY", "must-not-inherit");
            Environment.SetEnvironmentVariable("ExecutionAuthorization__ApiKey", "must-not-inherit");
            var command = Supervisor.Command(profile, "workflow", ["run"]);
            Check(!command.Environment.ContainsKey("CHAT_MODEL_API_KEY") && !command.Environment.ContainsKey("ExecutionAuthorization__ApiKey"), "Launcher leaked unrelated terminal secrets into a child.");
            Check(command.Environment["KYO_LOCAL_PROFILE"] == "test", "Child profile identity is missing.");
            var chatSettings = Profiles.Read("test", "chat");
            cs["CHAT_MODEL_API_KEY"] = "synthetic-model-key";
            cs["LocalSetup:ModelOrigin"] = "http://localhost:5290";
            LocalSettings.SaveSecrets("test", "chat", cs);
            var originalEndpoint = chatSettings["CHAT_MODEL_ENDPOINT"];
            chatSettings["CHAT_MODEL_ENDPOINT"] = "https://different.example.com/chat/completions";
            Profiles.Save("test", "chat", chatSettings);
            try { Profiles.Validate(profile); throw new Exception("Model key was accepted for a different origin."); }
            catch (InvalidDataException) { }
            chatSettings["CHAT_MODEL_ENDPOINT"] = originalEndpoint;
            Profiles.Save("test", "chat", chatSettings);
            cs["HARNESS_API_KEY"] = "wrong-key";
            LocalSettings.SaveSecrets("test", "chat", cs);
            try { Profiles.Validate(profile); throw new Exception("Mismatched service keys passed validation."); }
            catch (InvalidDataException) { }
            foreach (var invalid in new[] { "../other", "x/y", "", "x:y" })
            {
                try { LocalSettings.ProfileDirectory(invalid); throw new Exception("Unsafe profile name accepted."); }
                catch (ArgumentException) { }
            }
            using var current = Process.GetCurrentProcess();
            var owned = new OwnedProcess("test", current.Id, current.StartTime.ToUniversalTime().Ticks, null);
            using var matched = Supervisor.FindOwned(owned);
            Check(matched is not null && Supervisor.FindOwned(owned with { StartedUtcTicks = owned.StartedUtcTicks - 1 }) is null,
                "Process ownership check accepted a reused PID.");
            Console.WriteLine("Passed protected configuration, secret isolation, key consistency, override precedence, policy preservation, environment isolation and process ownership checks.");
            return Task.CompletedTask;
        }
        finally
        {
            Console.SetIn(previousInput);
            foreach (var variable in variables) Environment.SetEnvironmentVariable(variable, previous[variable]);
            Environment.SetEnvironmentVariable("KYO_LOCAL_HOME", previousHome);
            Environment.SetEnvironmentVariable("KYO_LOCAL_PROFILE", previousProfile);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static void Check(bool condition, string error) { if (!condition) throw new Exception(error); }
}
