namespace Heartbeat.Verification;

internal sealed record VerificationOptions(string Repository, string ConfigPath, TimeSpan Timeout,
    bool Keep, bool DisconnectUpload)
{
    public string Scenario { get; init; } = "headless-main";
    public bool IsDesktop => Scenario == "desktop-main";
    public Dictionary<string, string> Artifacts { get; init; } = [];

    public static VerificationOptions Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "run" || args[1] is not ("headless-main" or "desktop-main"))
            throw new ArgumentException("Expected: run headless-main or desktop-main. Use --help for options.");
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        var repository = FindRepository();
        var config = Path.Combine(repository, ".local", "heartbeat-headless.json");
        var seconds = 120;
        var keep = false;
        var fault = false;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--keep") { keep = true; continue; }
            var option = args[i];
            if (++i >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            switch (option)
            {
                case "--artifact":
                    var pair = args[i].Split('=', 2);
                    if (pair.Length != 2 || pair[0] is not ("analytics" or "headless" or "desktop" or "reference")
                        || string.IsNullOrWhiteSpace(pair[1]) || !artifacts.TryAdd(pair[0], Path.GetFullPath(pair[1])))
                        throw new ArgumentException("--artifact expects a unique SERVICE=PATH.");
                    break;
                case "--config": config = Path.GetFullPath(args[i]); break;
                case "--timeout-seconds":
                    if (!int.TryParse(args[i], out seconds) || seconds is < 1 or > 3600)
                        throw new ArgumentException("Timeout must be between 1 and 3600 seconds.");
                    break;
                case "--fault" when args[i] == "disconnect-upload": fault = true; break;
                default: throw new ArgumentException($"Unknown option/value: {option}.");
            }
        }
        if (artifacts.ContainsKey(args[1] == "desktop-main" ? "headless" : "desktop"))
            throw new ArgumentException("Artifact service is not used by this scenario.");
        return new(repository, config, TimeSpan.FromSeconds(seconds), keep, fault)
            { Scenario = args[1], Artifacts = artifacts };
    }

    private static string FindRepository()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Heartbeat.slnx")))
                    return directory.FullName;
        throw new ArgumentException("Cannot find Heartbeat.slnx; run from the repository.");
    }
}
