using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using OnAirNext.Engine;
using OnAirNext.ServiceControl;

namespace OnAirNext.Host;

[SupportedOSPlatform("windows")]
internal static class CommissioningOutputStore
{
    private const string ProfileFileName = "commissioning-outputs.dpapi";
    private const string BackupFileName = "commissioning-outputs.previous.dpapi";
    private const int MaxProfileBytes = 65536;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RadioTEDU-OnAirNext|commissioning-output-profile|v1");

    public static ProductionOutputConfiguration? Load(string dataRoot, IEnumerable<string> stationIds, JsonSerializerOptions json)
    {
        var path = Path.Combine(ValidateDirectory(dataRoot), ProfileFileName);
        if (!File.Exists(path)) return null;
        AssertProtectedFile(path);
        var info = new FileInfo(path);
        if (info.Length is < 16 or > MaxProfileBytes * 2) throw new InvalidDataException("The protected commissioning profile has an invalid size.");
        var encrypted = Convert.FromBase64String(File.ReadAllText(path, Encoding.ASCII));
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
            if (plaintext.Length > MaxProfileBytes) throw new InvalidDataException("The protected commissioning profile has an invalid size.");
            var profile = JsonSerializer.Deserialize<ProductionOutputConfiguration>(plaintext, json)
                ?? throw new InvalidDataException("The protected commissioning profile is empty.");
            return profile.Validate(stationIds);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static async Task WriteAsync(string dataRoot, ProductionOutputConfiguration profile, IEnumerable<string> stationIds,
        JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        var directory = ValidateDirectory(dataRoot);
        profile.Validate(stationIds);
        var profilePath = Path.Combine(directory, ProfileFileName);
        var backupPath = Path.Combine(directory, BackupFileName);
        if (File.Exists(profilePath))
        {
            _ = Load(dataRoot, stationIds, json);
            AssertProtectedFile(profilePath);
            File.Copy(profilePath, backupPath, overwrite: true);
            AssertProtectedFile(backupPath);
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(profile, json);
        byte[]? encrypted = null;
        var temporaryPath = profilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (plaintext.Length > MaxProfileBytes) throw new ArgumentException("The commissioning profile exceeds its size limit.");
            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
            await File.WriteAllTextAsync(temporaryPath, Convert.ToBase64String(encrypted), Encoding.ASCII, cancellationToken);
            AssertProtectedFile(temporaryPath);
            File.Move(temporaryPath, profilePath, overwrite: true);
            AssertProtectedFile(profilePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static bool Clear(string dataRoot, IEnumerable<string> stationIds, JsonSerializerOptions json)
    {
        var directory = ValidateDirectory(dataRoot);
        var profilePath = Path.Combine(directory, ProfileFileName);
        if (!File.Exists(profilePath)) return false;
        _ = Load(dataRoot, stationIds, json);
        AssertProtectedFile(profilePath);
        var backupPath = Path.Combine(directory, BackupFileName);
        File.Copy(profilePath, backupPath, overwrite: true);
        AssertProtectedFile(backupPath);
        File.Delete(profilePath);
        return true;
    }

    public static void RequireAllDisarmed(IEnumerable<StationSnapshot> current)
    {
        var stations = current.ToArray();
        if (stations.Length != 8 || stations.Any(station => station.State != "Stopped" || station.RequestedRunning ||
            station.Outputs.Any(output => output.State != "Stopped" || output.EncoderPid is not null || output.QueuedBlocks != 0)))
            throw new InvalidOperationException("Save the production output profile only while all eight channels and outputs are stopped.");
    }

    private static string ValidateDirectory(string dataRoot)
    {
        ServiceEndpoint.RequirePath(dataRoot, ServiceEndpoint.EngineData);
        var directory = new DirectoryInfo(dataRoot);
        if (!directory.Exists) throw new DirectoryNotFoundException("The protected engine data directory is unavailable.");
        ServiceFileProtection.AssertServiceDataProtected(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
        return directory.FullName;
    }

    private static void AssertProtectedFile(string path)
    {
        ServiceFileProtection.AssertServiceDataProtected(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
    }
}
