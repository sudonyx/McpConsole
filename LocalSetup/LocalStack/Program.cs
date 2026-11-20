using KyoPrep.LocalConfiguration;
using KyoPrep.LocalStack;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = args.FirstOrDefault() != "setup"; cancellation.Cancel(); };
try
{
    var command = args.FirstOrDefault() ?? "help";
    var name = "demo";
    string? root = null;
    string? capability = null;
    var noChat = false;
    var noBuild = false;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--profile": name = args[++i]; break;
            case "--root": root = args[++i]; break;
            case "--capability": capability = args[++i]; break;
            case "--no-chat": noChat = true; break;
            case "--no-build": noBuild = true; break;
            default: throw new ArgumentException("Unknown option. Use help.");
        }
    }
    LocalSettings.ValidateName(name);
    if (command == "setup")
    {
        root ??= Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        Profiles.Setup(name, root);
    }
    else if (command == "start") await Supervisor.StartAsync(Profiles.Load(name), noChat, noBuild, cancellation.Token);
    else if (command == "status") await Supervisor.StatusAsync(Profiles.Load(name), cancellation.Token);
    else if (command == "stop") Supervisor.Stop(name);
    else if (command == "connect") return await Supervisor.ConnectAsync(Profiles.Load(name), capability, cancellation.Token);
    else if (command == "check") { Profiles.Validate(Profiles.Load(name)); Console.WriteLine("Saved configuration is consistent."); }
    else if (command == "self-test") await SelfTests.RunAsync();
    else Console.WriteLine("Commands: setup | start [--no-chat] [--no-build] | status | stop | connect [--capability ID] | check | self-test\nOptions: --profile NAME --root KYOPREP_DIRECTORY (setup only)");
    return 0;
}
catch (OperationCanceledException) { return 130; }
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or KeyNotFoundException)
{
    Console.Error.WriteLine("Local setup stopped: " + ex.Message);
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Local setup stopped ({ex.GetType().Name}). Check saved configuration and service logs; no secrets were printed.");
    return 1;
}
