namespace ChestCounter;

/// <summary>An Epic account that recorded replays on this PC. Stats are kept per profile.</summary>
public sealed record Profile(string Key, string Name, int Matches, int Chests, DateTime LastPlayed);

/// <summary>
/// Replays saved on a PC are normally those of its player, so the replay owner (the recording player) is tracked.
/// When several accounts recorded replays here, each one gets its own profile and the player picks the account
/// they play on: the overlay and the report only show that profile.
/// </summary>
public static class Profiles
{
    /// <summary>Stable key of a match's account: Epic id (survives a name change), else the display name.</summary>
    public static string Key(MatchRecord m) => !string.IsNullOrEmpty(m.EpicId) ? m.EpicId : m.PlayerName ?? "";

    public static List<Profile> List(TrackerData d) => d.Matches
        .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var latest = g.OrderByDescending(When).First();
            var name = g.OrderByDescending(When).Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            return new Profile(g.Key, name ?? L.T("Compte inconnu", "Unknown account"), g.Count(), g.Sum(m => m.ChestsOpened), When(latest));
        })
        .OrderByDescending(p => p.LastPlayed)
        .ToList();

    private static DateTime When(MatchRecord m) => m.MatchStartUtc ?? m.AnalyzedAtUtc;

    /// <summary>The selected profile when it exists, else the account of the most recent match (null = no match yet on this PC).</summary>
    public static Profile? Current(TrackerData d, Config c)
    {
        var all = List(d);
        if (all.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(c.Profile) && !c.Profile.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var match = all.FirstOrDefault(p =>
                p.Key.Equals(c.Profile, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(p.Name) && p.Name.Equals(c.Profile, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;
        }

        // Default to the account that played the most recent match (List is ordered by LastPlayed descending)
        return all.FirstOrDefault();
    }

    /// <summary>The data of one profile only (same objects, filtered list), as seen by the overlay and the report.</summary>
    public static TrackerData View(TrackerData d, Config c)
    {
        var current = Current(d, c);
        if (current == null)
        {
            return new TrackerData
            {
                Version = d.Version,
                AnalyzerVersion = d.AnalyzerVersion,
                CreatedUtc = d.CreatedUtc,
                Matches = new List<MatchRecord>(),
                ProcessedFiles = d.ProcessedFiles,
            };
        }
        return new TrackerData
        {
            Version = d.Version,
            AnalyzerVersion = d.AnalyzerVersion,
            CreatedUtc = d.CreatedUtc,
            Matches = d.Matches.Where(m =>
                string.Equals(Key(m), current.Key, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(current.Name) && string.Equals(m.PlayerName, current.Name, StringComparison.OrdinalIgnoreCase))).ToList(),
            ProcessedFiles = d.ProcessedFiles,
        };
    }

    public static string Describe(Profile p) =>
        $"{p.Name}  ({L.Count(p.Matches, "partie", "parties", "match", "matches")}, {L.Count(p.Chests, "coffre", "chest")})";
}
