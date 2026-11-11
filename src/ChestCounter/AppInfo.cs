namespace ChestCounter;

/// <summary>App metadata, version and process synchronization.</summary>
public static class AppInfo
{
    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";
    public static string DisplayVersion => "v" + VersionText;

    public const string AppName = "Burnaviour Chest Tracker";
    public const string Author = "Burnaviour";
    public const string SiteUrl = "https://burnaviour.dev";
    public const string CreatorCode = "BRNYT";
    public static string IssuesUrl => SiteUrl;

    /// <summary>Path of executable when running as published exe.</summary>
    public static string? ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            return p != null && (Path.GetFileName(p).Equals("Burnaviour Chest Tracker.exe", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(p).Equals("NB Fortnite Chest Tracker.exe", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(p).Equals("ChestCounter.exe", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ? p : null;
        }
    }

    // Names of the per-user kernel objects that let the processes find each other.
    public static string InstanceMutexFor(string dataDir)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant()));
        return @"Local\Burnaviour.ChestTracker.Instance." + Convert.ToHexString(hash, 0, 8);
    }
    public const string StandbyMutex = @"Local\Burnaviour.ChestTracker.Standby";
    public const string StandbyStopEvent = @"Local\Burnaviour.ChestTracker.StandbyStop";
}
