using OnAirNext.Web;
using Xunit;

namespace OnAirNext.Tests;

public sealed class ReportGrantTests
{
    [Fact]
    public void DownloadTicketsAreSingleUseAndBindTheExactExportAndEngine()
    {
        var grants = new ReportDownloadGrants(); var at = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N"); var ticket = grants.Create("onair-next-dev-example", id, at);
        var claimed = grants.Claim(ticket, at); Assert.NotNull(claimed);
        Assert.Equal(id, claimed.ReportId); Assert.Equal("onair-next-dev-example", claimed.Engine);
        Assert.Null(grants.Claim(ticket, at)); Assert.Null(grants.Claim(new string('x', 64), at));
    }
    [Fact]
    public void ExpiredTicketCannotBeReplayedAndDoesNotConsumeAnUnrelatedTicket()
    {
        var grants = new ReportDownloadGrants(); var at = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N"); var old = grants.Create("onair-next-dev-example", id, at);
        Assert.Null(grants.Claim(old, at.AddSeconds(31)));
        var current = grants.Create("onair-next-dev-example", id, at.AddSeconds(31));
        Assert.Null(grants.Claim(old, at.AddSeconds(31))); Assert.NotNull(grants.Claim(current, at.AddSeconds(31)));
    }
    [Fact]
    public void PendingDownloadTicketsAreBoundedAndExpiredSlotsCanBeReused()
    {
        var grants = new ReportDownloadGrants(); var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < 16; i++) grants.Create("onair-next-dev-example", Guid.NewGuid().ToString("N"), at);
        Assert.Throws<InvalidOperationException>(() => grants.Create("onair-next-dev-example", Guid.NewGuid().ToString("N"), at));
        Assert.NotEmpty(grants.Create("onair-next-dev-example", Guid.NewGuid().ToString("N"), at.AddSeconds(31)));
    }
}
