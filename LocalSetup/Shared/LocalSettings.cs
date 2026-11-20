using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace KyoPrep.LocalConfiguration;

// Local configuration format v1. Mirrored in API repositories to keep standalone builds independent.
// The local setup self-test verifies that all copies match. No CRM/workflow dependency.
public static class LocalSettings
{
    public static string Home => Path.GetFullPath(Environment.GetEnvironmentVariable("KYO_LOCAL_HOME") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KyoPrep", "LocalStack"));
    public static string ProfileDirectory(string profile)
    {
        ValidateName(profile);
        return Path.Combine(Home, "profiles", profile);
    }
    public static void ValidateName(string value)
    {
        if (!Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$"))
            throw new ArgumentException("Profile/service names must contain only letters, digits, underscores and hyphens (1-64 characters).");
    }
    public static string SettingsPath(string profile, string service)
    {
        ValidateName(service);
        return Path.Combine(ProfileDirectory(profile), service, "appsettings.Local.json");
    }
    public static string SecretsPath(string profile, string service)
    {
        ValidateName(service);
        return Path.Combine(ProfileDirectory(profile), "secrets", service + ".protected");
    }

    public static void AddTo(ConfigurationManager configuration, string service, string contentRoot, string[]? args = null)
    {
        configuration.AddJsonFile(Path.Combine(contentRoot, "appsettings.Local.json"), optional: true, reloadOnChange: false);
        var profile = Environment.GetEnvironmentVariable("KYO_LOCAL_PROFILE");
        if (!string.IsNullOrWhiteSpace(profile))
        {
            configuration.AddJsonFile(SettingsPath(profile, service), optional: false, reloadOnChange: false);
            configuration.AddInMemoryCollection(ReadSecrets(profile, service).Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));
        }
        // Reapply overrides after local files. The launcher deliberately supplies a clean environment.
        configuration.AddEnvironmentVariables("DOTNET_").AddEnvironmentVariables("ASPNETCORE_").AddEnvironmentVariables();
        if (args is { Length: > 0 }) configuration.AddCommandLine(args);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            string[] required = service switch
            {
                "harness" => ["ExecutionAuthorization:ApiKey", "Harness:Boundary:ApiKeys:0"],
                "decision" => ["ExecutionAuthorization:ApiKey"],
                "workflow" => ["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"],
                "chat" => ["HARNESS_API_KEY", "DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"],
                _ => throw new ArgumentException("Unknown local service.")
            };
            foreach (var key in required)
                if (string.IsNullOrWhiteSpace(configuration[key]))
                    throw new InvalidDataException("Required local setting is missing: " + key + ". Run local setup.");
        }
    }

    private static IDataProtector Protector(string profile, string service)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local configuration secrets require Windows user protection.");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Home, "keys")), builder =>
        {
            builder.SetApplicationName("KyoPrep.LocalConfiguration.v1");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
            else throw new PlatformNotSupportedException("Windows protection is required.");
        });
        return provider.CreateProtector("profile-secrets-v1", profile, service);
    }

    public static Dictionary<string, string> ReadSecrets(string profile, string service)
    {
        var path = SecretsPath(profile, service);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = Protector(profile, service).Unprotect(File.ReadAllText(path));
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? throw new InvalidDataException("Missing local secret data.");
            return new(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        { throw new InvalidDataException("Cannot read protected local settings. Use the original Windows account and keys, or run setup again."); }
    }

    public static void SaveSecrets(string profile, string service, Dictionary<string, string> values) =>
        AtomicWrite(SecretsPath(profile, service), Protector(profile, service).Protect(JsonSerializer.Serialize(values)));

    public static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IResult Ready(string service, IConfiguration configuration, string? policyPath = null)
    {
        var ok = true;
        if (policyPath is not null)
        {
            try
            {
                if (new FileInfo(policyPath).Length > 2 * 1024 * 1024) ok = false;
                else
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
                    ok = document.RootElement.GetProperty("grants").ValueKind == JsonValueKind.Array &&
                        !string.IsNullOrWhiteSpace(document.RootElement.GetProperty("version").GetString());
                }
            }
            catch { ok = false; }
        }
        return Results.Json(new { service, status = ok ? "ready" : "policy-unavailable",
            profile = Environment.GetEnvironmentVariable("KYO_LOCAL_PROFILE"),
            instance = Environment.GetEnvironmentVariable("KYO_LOCAL_INSTANCE") }, statusCode: ok ? 200 : 503);
    }
}
