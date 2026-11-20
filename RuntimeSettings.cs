using KyoPrep.LocalConfiguration;
using Microsoft.Extensions.Configuration;

internal static class RuntimeSettings
{
    private static readonly Lazy<ConfigurationManager> Settings = new(() =>
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), optional: true);
        LocalSettings.AddTo(configuration, "chat", Directory.GetCurrentDirectory());
        return configuration;
    });
    // Retain live environment overrides for existing operator commands and offline regression tests.
    internal static string? Get(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { } supplied) return supplied;
        var value = Settings.Value[name];
        if (name == "CHAT_MODEL_API_KEY" && !string.IsNullOrEmpty(value) &&
            Environment.GetEnvironmentVariable("KYO_LOCAL_PROFILE") is { Length: > 0 })
        {
            var endpoint = Environment.GetEnvironmentVariable("CHAT_MODEL_ENDPOINT") ?? Settings.Value["CHAT_MODEL_ENDPOINT"];
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                uri.GetLeftPart(UriPartial.Authority) != Settings.Value["LocalSetup:ModelOrigin"])
                throw new ArgumentException("The model endpoint changed. Run local setup to configure its API key.");
        }
        return value;
    }
}
