using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OnAirNext.ServiceControl;

[SupportedOSPlatform("windows")]
internal static class ServiceFileProtection
{
    public static void Bootstrap(string configPath)
    {
        ServiceEndpoint.RequirePath(configPath, ServiceEndpoint.ConfigPath);
        ServiceEndpoint.RequirePath(AppContext.BaseDirectory, Path.Combine(ServiceEndpoint.InstallRoot, "bin"));
        AssertProtected(new DirectoryInfo(ServiceEndpoint.DataRoot).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        AssertProtected(new FileInfo(configPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        AssertProtected(new DirectoryInfo(AppContext.BaseDirectory).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }
    public static void ValidateSettings(string engineData, string ffmpeg)
    {
        ServiceEndpoint.RequirePath(engineData, ServiceEndpoint.EngineData);
        ServiceEndpoint.RequirePath(ffmpeg, ServiceEndpoint.Ffmpeg);
    }
    internal static void AssertProtected(FileSystemSecurity security)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
        var trusted = new[] { administrators, system };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner.Value))
            throw new UnauthorizedAccessException("Service configuration and binaries must be owned by Administrators or SYSTEM. Use the guided installer.");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0 &&
                !trusted.Contains(((SecurityIdentifier)rule.IdentityReference).Value))
                throw new UnauthorizedAccessException("Service configuration and binaries cannot be writable by unprivileged accounts. Use the guided installer.");
    }
}
