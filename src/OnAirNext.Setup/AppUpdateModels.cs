namespace OnAirNext.Installation;

public sealed record MaintenanceContract(int ContractVersion,int JournalSchemaVersion,bool RetainsCurrentDataOnRollback);

public sealed record AppUpdateReview(UpdateSwapReview Files,string OperatorSid,string PackageRoot,
    string ConfigurationSha256,string OwnershipSha256,string InitialPlanSha256,uint ServiceProcessId,DateTimeOffset ServiceStartedAtUtc,int JournalSchemaVersion);
