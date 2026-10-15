using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OnAirNext.ServiceControl;

[SupportedOSPlatform("windows")]
internal sealed class ServicePipePolicy
{
    private readonly string _owner;
    public string OperatorSid { get; }
    public ServicePipePolicy(string owner, string operatorSid)
    {
        _owner = new SecurityIdentifier(owner).Value;
        var account = new SecurityIdentifier(operatorSid);
        if (!account.IsAccountSid() && !account.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            throw new ArgumentException("Select an individual Windows operator account.");
        OperatorSid = account.Value;
    }
    public bool Allows(string? callerSid) => callerSid is not null && (callerSid == _owner || callerSid == OperatorSid);
    public PipeSecurity Security()
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        var owner = new SecurityIdentifier(_owner); security.SetOwner(owner);
        foreach (var denied in new[] { WellKnownSidType.NetworkSid, WellKnownSidType.AnonymousSid })
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(denied, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(OperatorSid), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }
    public NamedPipeServerStream CreateServer(string name = ServiceEndpoint.Pipe)
    {
        if (!ServiceEndpoint.IsIsolated(name)) throw new ArgumentException("Use an isolated development pipe.");
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, Security());
    }
    public void Authorize(NamedPipeServerStream pipe)
    {
        string? caller = null;
        // Query identity only. No file/database work executes under the client's token.
        pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true); caller = identity?.User?.Value; });
        if (!Allows(caller)) throw new UnauthorizedAccessException("This Windows account is not an authorized studio operator.");
    }
}
