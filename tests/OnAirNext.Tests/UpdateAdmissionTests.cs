using System.Text.Json;
using System.Text.Json.Nodes;
using OnAirNext.Installation;
using OnAirNext.ServiceControl;
using Xunit;

namespace OnAirNext.Tests;

public sealed class UpdateAdmissionTests
{
    private static JsonObject Status()=>JsonSerializer.SerializeToNode(new
    {
        controller=new{mode="windows-service",service=ServiceEndpoint.Name},children=new Dictionary<string,string>(),
        audit=new{Error=(string?)null,PendingEvents=0},
        stations=SetupPaths.StationIds.Select(id=>new{Id=id,State="Stopped",RequestedRunning=false,Outputs=Array.Empty<object>()}).ToArray()
    })!.AsObject();
    private static JsonObject Config()=>JsonSerializer.SerializeToNode(new
    {
        Ffmpeg=ServiceEndpoint.Ffmpeg,DataRoot=ServiceEndpoint.EngineData,PipeName=ServiceEndpoint.Pipe,OperatorSid="fixture-operator",
        Stations=SetupPaths.StationIds.Select(id=>new{Id=id,AutoStart=false,Outputs=Array.Empty<object>()}).ToArray()
    })!.AsObject();
    [Fact]
    public void CompleteStoppedDisarmedRosterIsAdmitted()
    {
        UpdateAdmission.RequireUnarmedStatus(JsonSerializer.SerializeToElement(Status()));
        UpdateAdmission.RequireUnarmedConfiguration(JsonSerializer.SerializeToElement(Config()),"fixture-operator");
    }
    [Fact]
    public void SeparateAdministratorCanUseOnlyARecentOperatorStoppedStateReview()
    {
        var now=new DateTimeOffset(2026,10,17,9,0,0,TimeSpan.Zero);
        UpdateAdmission.RequireFreshOperatorReview(now.AddSeconds(-45),new string('A',64),now);
        Assert.Throws<InvalidOperationException>(()=>UpdateAdmission.RequireFreshOperatorReview(now.AddMinutes(-4),new string('A',64),now));
        Assert.Throws<InvalidOperationException>(()=>UpdateAdmission.RequireFreshOperatorReview(now.AddSeconds(1),new string('A',64),now));
        Assert.Throws<InvalidOperationException>(()=>UpdateAdmission.RequireFreshOperatorReview(now.AddSeconds(-45),"tampered",now));
    }
    [Theory]
    [InlineData("running-intent")][InlineData("playing")][InlineData("stopped-output")][InlineData("child")]
    [InlineData("journal-error")][InlineData("journal-pending")][InlineData("duplicate-id")][InlineData("missing-intent")]
    [InlineData("foreign-service")][InlineData("developer-controller")][InlineData("missing-station")][InlineData("wrong-output-kind")]
    public void ActiveIncompleteOrUntrustedStatusCannotAuthorizeMaintenance(string change)
    {
        var status=Status();var station=status["stations"]![0]!;
        switch(change)
        {
            case "running-intent":station["RequestedRunning"]=true;break;
            case "playing":station["State"]="Running";break;
            case "stopped-output":station["Outputs"]!.AsArray().Add(new JsonObject{{"State","Stopped"}});break;
            case "child":status["children"]!["123"]="encoder";break;
            case "journal-error":status["audit"]!["Error"]="disk full";break;
            case "journal-pending":status["audit"]!["PendingEvents"]=1;break;
            case "duplicate-id":status["stations"]![1]!["Id"]="1";break;
            case "missing-intent":station.AsObject().Remove("RequestedRunning");break;
            case "foreign-service":status["controller"]!["service"]="other-service";break;
            case "developer-controller":status["controller"]!["mode"]="developer";break;
            case "missing-station":status["stations"]!.AsArray().RemoveAt(0);break;
            case "wrong-output-kind":station["Outputs"]=new JsonObject();break;
        }
        Assert.Throws<InvalidOperationException>(()=>UpdateAdmission.RequireUnarmedStatus(JsonSerializer.SerializeToElement(status)));
    }
    [Theory]
    [InlineData("autostart")][InlineData("output")][InlineData("duplicate-id")][InlineData("missing-autostart")]
    [InlineData("foreign-operator")][InlineData("foreign-pipe")][InlineData("foreign-data")][InlineData("foreign-encoder")]
    public void ConfigurationMustIndependentlyMatchTheOwnedStoppedInstallation(string change)
    {
        var config=Config();var station=config["Stations"]![0]!;
        switch(change)
        {
            case "autostart":station["AutoStart"]=true;break;
            case "output":station["Outputs"]!.AsArray().Add(new JsonObject{{"Id","configured"}});break;
            case "duplicate-id":config["Stations"]![1]!["Id"]="1";break;
            case "missing-autostart":station.AsObject().Remove("AutoStart");break;
            case "foreign-operator":config["OperatorSid"]="another-operator";break;
            case "foreign-pipe":config["PipeName"]="different-pipe";break;
            case "foreign-data":config["DataRoot"]=Path.Combine(ServiceEndpoint.DataRoot,"other-data");break;
            case "foreign-encoder":config["Ffmpeg"]=Path.Combine(ServiceEndpoint.InstallRoot,"other.exe");break;
        }
        Assert.Throws<InvalidOperationException>(()=>UpdateAdmission.RequireUnarmedConfiguration(JsonSerializer.SerializeToElement(config),"fixture-operator"));
    }
}
