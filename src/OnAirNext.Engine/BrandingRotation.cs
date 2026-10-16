namespace OnAirNext.Engine;

public enum BrandingCollisionOrder { JingleFirst, SweeperFirst }
[Flags]
public enum BrandingKinds { None = 0, Jingle = 1, Sweeper = 2 }
public sealed record BrandingRule(bool Enabled, int EveryCompletedSongs);
public sealed record BrandingRules(BrandingRule Jingle, BrandingRule Sweeper,
    BrandingCollisionOrder CollisionOrder = BrandingCollisionOrder.JingleFirst)
{
    public static BrandingRules Disabled { get; } = new(new(false, 2), new(false, 3));
}
public sealed record BrandingDecision(MediaItem? Item, BrandingKinds Due, BrandingKinds Deferred, BrandingKinds Missing);

/// <summary>
/// An immutable, bounded pool prepared off the programme clock. The caller must
/// supply validated, available assets and the projected completion counters at
/// the boundary being prepared, including preceding in-flight completions.
/// This class performs no file/database access and never changes durable counts.
/// </summary>
public sealed class BrandingRotation
{
    public const int MaximumAssets = 4096;
    private readonly BrandingRules _rules;
    private readonly MediaItem[] _jingles, _sweepers;
    public int JingleCount => _jingles.Length;
    public int SweeperCount => _sweepers.Length;

    public BrandingRotation(BrandingRules rules, IEnumerable<MediaItem> validatedAssets)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(validatedAssets);
        ArgumentNullException.ThrowIfNull(rules.Jingle);
        ArgumentNullException.ThrowIfNull(rules.Sweeper);
        if (rules.Jingle.EveryCompletedSongs is < 1 or > 100 || rules.Sweeper.EveryCompletedSongs is < 1 or > 100 ||
            !Enum.IsDefined(rules.CollisionOrder))
            throw new ArgumentException("Branding intervals must be between 1 and 100 completed songs and the collision order must be supported.");
        var assets = validatedAssets.Take(MaximumAssets + 1).ToArray();
        if (assets.Length > MaximumAssets || assets.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) ||
                x.Id.Length > 256 || string.IsNullOrWhiteSpace(x.Path) || x.Kind is not (ContentKind.Jingle or ContentKind.Sweeper)) ||
            assets.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != assets.Length)
            throw new ArgumentException("Choose at most 4,096 validated jingles and sweepers with distinct asset IDs.", nameof(validatedAssets));
        _rules = rules;
        _jingles = assets.Where(x => x.Kind == ContentKind.Jingle).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        _sweepers = assets.Where(x => x.Kind == ContentKind.Sweeper).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    // Ordinals are completed branding plays, not selection attempts. Repeating a
    // preparation or recovering a failed/skipped attempt must choose the same item.
    public BrandingDecision Decide(BrandingCadence projected, long completedJingles, long completedSweepers)
    {
        ArgumentNullException.ThrowIfNull(projected);
        if (string.IsNullOrWhiteSpace(projected.StationId) || projected.MusicSinceJingle < 0 || projected.MusicSinceSweeper < 0 ||
            completedJingles < 0 || completedSweepers < 0)
            throw new ArgumentException("Branding requires a station and nonnegative completion counters.");
        var due = BrandingKinds.None;
        if (_rules.Jingle.Enabled && projected.MusicSinceJingle >= _rules.Jingle.EveryCompletedSongs) due |= BrandingKinds.Jingle;
        if (_rules.Sweeper.Enabled && projected.MusicSinceSweeper >= _rules.Sweeper.EveryCompletedSongs) due |= BrandingKinds.Sweeper;
        var missing = BrandingKinds.None;
        if ((due & BrandingKinds.Jingle) != 0 && _jingles.Length == 0) missing |= BrandingKinds.Jingle;
        if ((due & BrandingKinds.Sweeper) != 0 && _sweepers.Length == 0) missing |= BrandingKinds.Sweeper;
        var available = due & ~missing;
        var chosen = available == (BrandingKinds.Jingle | BrandingKinds.Sweeper)
            ? _rules.CollisionOrder == BrandingCollisionOrder.JingleFirst ? BrandingKinds.Jingle : BrandingKinds.Sweeper
            : available;
        var item = chosen switch
        {
            BrandingKinds.Jingle => _jingles[(int)(completedJingles % _jingles.Length)],
            BrandingKinds.Sweeper => _sweepers[(int)(completedSweepers % _sweepers.Length)],
            _ => null
        };
        // A missing pool does not erase its due counter, substitute another role,
        // or reserve silence. A null item lets the caller continue ordinary music.
        return new(item, due, due & ~chosen, missing);
    }
}
