namespace OnAirNext.Engine;

public static class RecoveryCompatibility
{
    public const string HeldRotationFeature = "held-rotation-v1";
    public const string StationQueueFeature = "station-queue-v1";
    public static bool SupportsHeldRotation(IReadOnlyCollection<string>? features)
        => features?.Contains(HeldRotationFeature, StringComparer.Ordinal) == true;

    public static void RequireNoHeldMaintenance(PlayoutCheckpoint checkpoint)
    {
        if (checkpoint.QueueEntryId is not null)
            throw new InvalidOperationException("Application maintenance cannot retain an unfinished queued item. Complete or resolve it in the compatible console first.");
        if (checkpoint.HoldsRotation)
            throw new InvalidOperationException("Application maintenance cannot retain an unfinished automatic branding item for an older recovery reader. Complete or explicitly resolve that item in the compatible application first.");
    }
}
