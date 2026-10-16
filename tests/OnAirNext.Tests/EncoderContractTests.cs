using OnAirNext.Engine;
using Xunit;

namespace OnAirNext.Tests;

public sealed class EncoderContractTests
{
    private static OutputSettings Output(CodecProfile profile)=>new("codec-contract","127.0.0.1",18001,"/next-codec-contract","synthetic-fixture",profile);
    [Theory]
    [InlineData(CodecProfile.AacLc192)]
    [InlineData(CodecProfile.HeAacV2_64)]
    public void NativeAacCannotSubstituteEitherApprovedFdkProfile(CodecProfile profile)
        => Assert.Throws<NotSupportedException>(()=>EncoderCapabilities.ValidateListing(" A....D aac native AAC encoder\n A....D flac FLAC encoder\n",[Output(profile)]));
    [Fact]
    public void ExactFdkAndFlacCapabilitiesAcceptTheConfiguredProfiles()
        => EncoderCapabilities.ValidateListing(" A....D libfdk_aac Fraunhofer AAC\n A....D flac FLAC\n",Enum.GetValues<CodecProfile>().Select(Output));
    [Fact]
    public void EncoderNameInADescriptionDoesNotAdmitAFallback()
        => Assert.Throws<NotSupportedException>(()=>EncoderCapabilities.ValidateListing(" A....D aac description mentions libfdk_aac\n",[Output(CodecProfile.AacLc192)]));
}
