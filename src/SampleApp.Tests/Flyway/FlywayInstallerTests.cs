using Lakebase;

namespace SampleApp.Tests.Flyway;

public class FlywayInstallerTests
{
    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    [InlineData("linux-x64")]
    [InlineData("win-x64")]
    public void PinnedArtifact_ExistsForSupportedPlatforms(string rid)
        => Assert.True(FlywayInstaller.IsSupported(rid));

    [Fact]
    public void UnsupportedPlatform_IsRejected()
        => Assert.False(FlywayInstaller.IsSupported("linux-arm64"));

    [Theory]
    [InlineData(false, "flyway")]
    [InlineData(true, "flyway.cmd")]
    public void BinaryFileName_MatchesPlatformLauncher(bool windows, string expected)
        => Assert.Equal(expected, FlywayInstaller.BinaryFileName(windows));
}
