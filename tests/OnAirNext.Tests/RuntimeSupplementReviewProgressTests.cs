using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class RuntimeSupplementReviewProgressTests
{
    [Fact]
    public void Chunked_progress_preserves_only_complete_allowlisted_steps()
    {
        var stages=new List<string>();var reader=new RuntimeSupplementReviewProgress.Reader(stages.Add);
        var wire="untrusted diagnostic containing a private path\n"+
            RuntimeSupplementReviewProgress.Prefix+"ownership\r\n"+
            RuntimeSupplementReviewProgress.Prefix+"C:/private/password.txt\n"+
            RuntimeSupplementReviewProgress.Prefix+"candidate-image\n";
        foreach(var character in wire)reader.Append(new[]{character});
        Assert.Equal(new[]{"ownership","candidate-image"},stages);
    }

    [Fact]
    public void Oversize_or_unfinished_lines_cannot_spoof_a_progress_frame()
    {
        var stages=new List<string>();var reader=new RuntimeSupplementReviewProgress.Reader(stages.Add);
        reader.Append(new string('x',129)+RuntimeSupplementReviewProgress.Prefix+"ownership\n");
        reader.Append(RuntimeSupplementReviewProgress.Prefix+"provider-readback\n");
        reader.Append(RuntimeSupplementReviewProgress.Prefix+"service");
        Assert.Equal(new[]{"provider-readback"},stages);
    }

    [Fact]
    public void Diagnostic_flood_is_bounded_even_when_ignored()
    {
        var reader=new RuntimeSupplementReviewProgress.Reader(_=>throw new InvalidOperationException());
        var chunk=new string('x',4096);
        for(var i=0;i<128;i++)reader.Append(chunk);
        Assert.Throws<IOException>(()=>reader.Append("x"));
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("service")]
    [InlineData("candidate-image")]
    public void Timeout_names_the_last_verified_step_without_exception_data(string stage)
    {
        var message=RuntimeSupplementReviewProgress.FailureMessage(new OperationCanceledException("secret provider path"),stage);
        Assert.Contains("time limit",message);
        Assert.Contains(RuntimeSupplementReviewProgress.Describe(stage)!,message);
        Assert.Contains("No files were installed",message);
        Assert.DoesNotContain("secret",message);
    }

    [Fact]
    public void Unknown_stage_and_access_exception_cannot_disclose_paths()
    {
        var message=RuntimeSupplementReviewProgress.FailureMessage(new UnauthorizedAccessException("C:/private/file"),"C:/private/file");
        Assert.Contains("protected file",message);
        Assert.DoesNotContain("private",message);
        Assert.True(RuntimeSupplementReviewProgress.ConsoleTimeout>RuntimeSupplementReviewProgress.HelperTimeout);
    }
}
