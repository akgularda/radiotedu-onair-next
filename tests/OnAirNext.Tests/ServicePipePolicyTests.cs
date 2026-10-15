using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using OnAirNext.ServiceControl;
using Xunit;

namespace OnAirNext.Tests;

[SupportedOSPlatform("windows")]
public sealed class ServicePipePolicyTests
{
    private const string Operator = "S-1-5-21-100-200-300-1001";
    [Theory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-7")]
    [InlineData("S-1-5-2")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-545")]
    public void BroadAndServicePrincipalsCannotBeConfiguredAsOperator(string sid)
        => Assert.Throws<ArgumentException>(() => new ServicePipePolicy("S-1-5-19", sid));

    [Fact]
    public void MembershipInAnUnlistedAccountDoesNotAuthorizeACommand()
    {
        var policy = new ServicePipePolicy("S-1-5-19", Operator);
        Assert.True(policy.Allows(Operator)); Assert.True(policy.Allows("S-1-5-19"));
        Assert.False(policy.Allows(null)); Assert.False(policy.Allows("S-1-5-18"));
        Assert.False(policy.Allows("S-1-5-21-100-200-300-1002"));
        Assert.False(policy.Allows("S-1-5-21-100-200-300-513"));
    }
    [Fact]
    public void OperatorCanReadWriteButCannotCreateASecondServerInstance()
    {
        var acl = new ServicePipePolicy("S-1-5-19", Operator).Security();
        var rules = acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
        var grant = Assert.Single(rules,r=>r.IdentityReference.Value==Operator && r.AccessControlType==AccessControlType.Allow);
        Assert.Equal((PipeAccessRights)0,grant.PipeAccessRights & PipeAccessRights.CreateNewInstance);
        Assert.Equal(PipeAccessRights.ReadWrite,grant.PipeAccessRights & PipeAccessRights.ReadWrite);
        foreach(var sid in new[]{"S-1-5-2","S-1-5-7"}) Assert.Contains(rules,r=>r.IdentityReference.Value==sid && r.AccessControlType==AccessControlType.Deny);
        Assert.Equal("S-1-5-19",Assert.IsType<SecurityIdentifier>(acl.GetOwner(typeof(SecurityIdentifier))).Value);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerIdentityMustBeAvailableBeforeAnyCommandExecutes(bool identify)
    {
        using var identity=WindowsIdentity.GetCurrent(); var owner=identity.User!.Value;
        var policy=new ServicePipePolicy(owner,Operator); var name="onair-next-dev-auth-"+Guid.NewGuid().ToString("N");
        await using var server=policy.CreateServer(name); using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var accepted=server.WaitForConnectionAsync(deadline.Token);
        await using var client=new NamedPipeClientStream(".",name,PipeAccessRights.ReadWrite|PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous,identify?TokenImpersonationLevel.Identification:TokenImpersonationLevel.Anonymous,HandleInheritability.None);
        await client.ConnectAsync(deadline.Token);await accepted;
        await client.WriteAsync(new byte[]{1},deadline.Token);await server.ReadExactlyAsync(new byte[1],deadline.Token);
        if(identify)policy.Authorize(server);
        else
        {
            Assert.Throws<UnauthorizedAccessException>(()=>policy.Authorize(server));
        }
    }
    [Theory]
    [InlineData(ServiceEndpoint.Pipe)]
    [InlineData(ServiceEndpoint.FilePipe)]
    public async Task AConnectedCounterfeitProcessIsRejectedBeforePayloadTransmission(string reservedPipe)
    {
        var name="onair-next-dev-counterfeit-"+Guid.NewGuid().ToString("N");
        await using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly|PipeOptions.FirstPipeInstance);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));var accepted=server.WaitForConnectionAsync(deadline.Token);
        await using var client=ServicePipeClient.Create(name);await client.ConnectAsync(deadline.Token);await accepted;
        Assert.Throws<UnauthorizedAccessException>(()=>ServicePipeClient.AuthenticateServer(client,reservedPipe));
    }
    [Fact]
    public void OrdinaryOperatorCannotOpenTheIsolatedMachineJournal()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.NotEqual("S-1-5-19", identity.User?.Value);
        Assert.Throws<UnauthorizedAccessException>(() => OnAirNext.Engine.EventJournal.OpenIsolatedWindowsService());
    }
    [Fact]
    public void DefaultJournalConstructorCannotBypassIsolationForServiceStorage()
    {
        var path = Path.Combine(ServiceEndpoint.EngineData, "playback.db");
        Assert.Throws<ArgumentException>(() => new OnAirNext.Engine.EventJournal(path));
    }
    [Fact]
    public void LegacyPathsAndRenamedServiceRootsAreRejectedWithoutCreatingThem()
    {
        var legacy=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"RadioTEDU","OnAir","cleanroom.db");
        Assert.Throws<ArgumentException>(()=>ServiceEndpoint.RequirePath(legacy,ServiceEndpoint.ConfigPath));
        Assert.Throws<ArgumentException>(()=>ServiceEndpoint.RequirePath(ServiceEndpoint.EngineData+"-other",ServiceEndpoint.EngineData));
        Assert.Throws<ArgumentException>(()=>ServicePipeClient.Create("legacy-production-pipe"));
    }
}
