using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OnAirNext.ConsoleProtocol;
using OnAirNext.Installation;

namespace OnAirNext.Web;

internal sealed class ConsoleAccess : IDisposable
{
    private readonly FileStream _held;
    private readonly string _token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Version {get;}=Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"unknown";
    public ConsoleAccess()
    {
        var folder=ConsoleEndpointLocation.Directory;SetupPaths.RequireNoLinks(folder);
        if(OperatingSystem.IsWindows())
        {
            using var identity=WindowsIdentity.GetCurrent();var owner=identity.User??throw new UnauthorizedAccessException("The studio operator account is unavailable.");
            var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(owner);
            foreach(var sid in new[]{owner,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                security.AddAccessRule(new(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            var directory=new DirectoryInfo(folder);if(directory.Exists)directory.SetAccessControl(security);else directory.Create(security);
        }
        else{Directory.CreateDirectory(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);}
        var path=Path.Combine(folder,"console.lock");SetupPaths.RequireNoLinks(path);
        _held=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        try
        {
            var descriptor=ConsoleEndpointLocation.Descriptor;SetupPaths.RequireNoLinks(descriptor);
            var temporary=Path.Combine(folder,Guid.NewGuid().ToString("N")+".partial");
            try
            {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                    var bytes=JsonSerializer.SerializeToUtf8Bytes(new ConsoleEndpoint(Environment.ProcessId,Version,_token));file.Write(bytes);file.Flush(true);
                }
                File.Move(temporary,descriptor,true);
            }
            finally{SetupPaths.RequireNoLinks(temporary);if(File.Exists(temporary))File.Delete(temporary);}
        }
        catch{_held.Dispose();throw;}
    }
    public bool Allows(string supplied)=>supplied.Length==_token.Length&&CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied),Encoding.UTF8.GetBytes(_token));
    public void Dispose()=>_held.Dispose();
}
