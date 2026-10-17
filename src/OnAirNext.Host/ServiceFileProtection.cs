using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OnAirNext.ServiceControl;

[SupportedOSPlatform("windows")]
internal static class ServiceFileProtection
{
    public static void Bootstrap(string configPath) => BootstrapInstalled(configPath, AppContext.BaseDirectory);
    public static void BootstrapInstalled(string configPath, string binaryDirectory)
    {
        ServiceEndpoint.RequirePath(configPath, ServiceEndpoint.ConfigPath);
        ServiceEndpoint.RequirePath(binaryDirectory, Path.Combine(ServiceEndpoint.InstallRoot, "bin"));
        AssertProtected(new DirectoryInfo(ServiceEndpoint.DataRoot).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        AssertProtected(new FileInfo(configPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        AssertProtected(new DirectoryInfo(binaryDirectory).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }
    public static void ValidateSettings(string engineData, string ffmpeg)
    {
        ServiceEndpoint.RequirePath(engineData, ServiceEndpoint.EngineData);
        ServiceEndpoint.RequirePath(ffmpeg, ServiceEndpoint.Ffmpeg);
    }
    internal static void AssertProtected(FileSystemSecurity security)
        => AssertProtected(security, allowLocalServiceOwner: false);

    internal static void AssertServiceDataProtected(FileSystemSecurity security)
        => AssertProtected(security, allowLocalServiceOwner: true);

    private static void AssertProtected(FileSystemSecurity security, bool allowLocalServiceOwner)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var localService = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null).Value;
        var trusted = allowLocalServiceOwner ? new[] { administrators, system, localService } : new[] { administrators, system };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner.Value))
            throw new UnauthorizedAccessException(allowLocalServiceOwner
                ? "Protected service data must be owned by Administrators, SYSTEM or LocalService."
                : "Service configuration and binaries must be owned by Administrators or SYSTEM. Use the guided installer.");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                !trusted.Contains(((SecurityIdentifier)rule.IdentityReference).Value))
                throw new UnauthorizedAccessException("Service configuration and binaries cannot be writable by unprivileged accounts. Use the guided installer.");
    }
}
