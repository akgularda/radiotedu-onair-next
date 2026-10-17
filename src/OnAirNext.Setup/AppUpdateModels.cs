namespace OnAirNext.Installation;

public sealed record MaintenanceContract(int ContractVersion,int JournalSchemaVersion,bool RetainsCurrentDataOnRollback);

public sealed record AppUpdateReview(UpdateSwapReview Files,string OperatorSid,string PackageRoot,
    string ConfigurationSha256,string OwnershipSha256,string InitialPlanSha256,uint ServiceProcessId,DateTimeOffset ServiceStartedAtUtc,int JournalSchemaVersion,string? OriginalSource=null,string? ReplacementSource=null,string? PreflightStatusSha256=null);

public sealed record AppUpdateCompletion(string ReviewSha256,UpdateDirection Direction,string ImageSha256,DateTimeOffset VerifiedAtUtc);
public sealed record AppUpdateSnapshot(string ReviewSha256,string Folder,DateTimeOffset CapturedAtUtc,SetupFile[] Files);
