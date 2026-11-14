using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>
/// What the Treasure Hunt tab needs, per match: when it started, when each chest was opened, the chests by kind
/// and the treasure found. The page works out the weeks, Treasure Hours and odds from it.
/// </summary>
public static class TreasureStats
{
    public sealed record Find(string Type, string Name, string? PrizeId, string Source, int? At, bool Exact);
    public sealed record Match(string Start, int Chests, int[] Times, Dictionary<string, int> Kinds, Find[] Finds, int? Placement, int Seconds,
        int? Eliminations, MatchStats? Stats, Dictionary<string, int> Loot);
    public sealed record Result(Match[] Matches, int LiveChests);

    public static Result Build(IEnumerable<MatchRecord> matches, MatchRecord? live)
    {
        var list = matches
            .OrderBy(m => m.MatchStartUtc ?? m.AnalyzedAtUtc)
            .Select(ToMatch)
            .ToArray();
        return new Result(list, live?.ChestsOpened ?? 0);
    }

    private static Match ToMatch(MatchRecord m)
    {
        var start = DateTime.SpecifyKind(m.MatchStartUtc ?? m.AnalyzedAtUtc, DateTimeKind.Utc);
        // Chests broken open count as opened too
        var opened = m.Chests;
        var kinds = opened
            .GroupBy(c => L.NameEn(c.Kind))
            .ToDictionary(g => g.Key, g => g.Count());
        var finds = m.TreasureFinds.Select(f => new Find(
            TypeOf(f),
            f.PrizeId != null && f.PrizeId.StartsWith("DelilahPrize:", StringComparison.OrdinalIgnoreCase) ? TreasureNames.Friendly(f.PrizeId) : L.NameEn(f.Item),
            f.PrizeId,
            L.NameEn(f.SourceKind),
            f.ObtainedAtSeconds is { } t ? (int)t : null,
            f.TimeExact)).ToArray();
        return new Match(start.ToString("o"), m.ChestsOpened, opened.Select(c => (int)c.TimeSeconds).ToArray(), kinds, finds, m.Placement, m.DurationSeconds ?? 0,
            m.Eliminations, m.Stats, LootOf(opened));
    }

    /// <summary>Item ids end with their rarity: WID_Shotgun_CoreDPS_Athena_SR, WID_Pistol_Scavenger_Athena_UC_Ore_T03…</summary>
    private static readonly Regex RarityCode = new(@"_(C|UC|R|VR|SR|UR)(?=_|$)", RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string> Rarities = new()
    {
        ["C"] = "common", ["UC"] = "uncommon", ["R"] = "rare", ["VR"] = "epic", ["SR"] = "legendary", ["UR"] = "mythic",
    };

    /// <summary>How many items of each rarity the chests gave (items without a rarity, like ammo, are left out).</summary>
    private static Dictionary<string, int> LootOf(IEnumerable<ChestRecord> chests) => chests
        .SelectMany(c => c.Items)
        .Select(item => RarityCode.Match(item))
        .Where(m => m.Success)
        .GroupBy(m => Rarities[m.Groups[1].Value])
        .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>"prize": a weekly gold treasure from a golden chest; "gizmo": a treasure gizmo; "other": anything else.</summary>
    private static string TypeOf(TreasureFind f)
    {
        if (f.PrizeId != null && f.PrizeId.StartsWith("DelilahPrize:", StringComparison.OrdinalIgnoreCase)) return "prize";
        if (f.Item.StartsWith("Gizmo", StringComparison.OrdinalIgnoreCase)) return "gizmo";
        return "other";
    }
}
