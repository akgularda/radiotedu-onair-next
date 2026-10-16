namespace OnAirNext.Engine;

// A session starts from one committed journal snapshot, then projects its own
// ordered prepared terminals. It must not reread and add committed terminals a
// second time while FULL-WAL acknowledgements catch up with the programme clock.
public sealed record BrandingProgress(BrandingCadence Cadence, long CompletedMusic = 0,
    long JingleAttemptMusic = -1, long SweeperAttemptMusic = -1)
{
    public BrandingKinds Held =>
        (JingleAttemptMusic == CompletedMusic ? BrandingKinds.Jingle : BrandingKinds.None) |
        (SweeperAttemptMusic == CompletedMusic ? BrandingKinds.Sweeper : BrandingKinds.None);

    public BrandingProgress Validate()
    {
        ArgumentNullException.ThrowIfNull(Cadence);
        if (string.IsNullOrWhiteSpace(Cadence.StationId) || Cadence.MusicSinceJingle < 0 || Cadence.MusicSinceSweeper < 0 ||
            Cadence.CompletedJingles < 0 || Cadence.CompletedSweepers < 0 || CompletedMusic < 0 ||
            JingleAttemptMusic < -1 || JingleAttemptMusic > CompletedMusic || SweeperAttemptMusic < -1 || SweeperAttemptMusic > CompletedMusic)
            throw new InvalidDataException("Branding completion state is invalid; it was not reset.");
        return this;
    }

    public BrandingProgress Project(ContentKind kind, string terminal)
    {
        if (terminal is not ("ended" or "skipped" or "failed" or "decode_failed")) return this;
        var next = this;
        if (kind == ContentKind.Jingle) next = next with { JingleAttemptMusic = CompletedMusic };
        if (kind == ContentKind.Sweeper) next = next with { SweeperAttemptMusic = CompletedMusic };
        if (terminal != "ended") return next;
        checked
        {
            return kind switch
            {
                ContentKind.Music => next with { CompletedMusic = CompletedMusic + 1,
                    Cadence = Cadence with { MusicSinceJingle = Cadence.MusicSinceJingle + 1, MusicSinceSweeper = Cadence.MusicSinceSweeper + 1 } },
                ContentKind.Jingle => next with { Cadence = Cadence with { MusicSinceJingle = 0, CompletedJingles = Cadence.CompletedJingles + 1 } },
                ContentKind.Sweeper => next with { Cadence = Cadence with { MusicSinceSweeper = 0, CompletedSweepers = Cadence.CompletedSweepers + 1 } },
                _ => next
            };
        }
    }
}

public interface IBrandingSource
{
    // Implementations publish immutable, prevalidated pools outside the audio clock.
    BrandingRotation? Snapshot();
    event Action? Changed;
}

public sealed class BrandingSource : IBrandingSource
{
    private BrandingRotation? _current;
    public event Action? Changed;
    public BrandingRotation? Snapshot() => Volatile.Read(ref _current);
    public void Publish(BrandingRotation? prepared)
    {
        if (ReferenceEquals(Interlocked.Exchange(ref _current, prepared), prepared)) return;
        Changed?.Invoke();
    }
}
