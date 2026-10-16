namespace SampleApp.Tests.Flyway;

public class FeatureBranchDdlTests
{
    [Fact]
    public void NormalizeRole_AcceptsAppSpClientId()
    {
        var role = Lakebase.FeatureBranchDdl.NormalizeRole("00000000-0000-4000-8000-000000000001");
        Assert.Equal("00000000-0000-4000-8000-000000000001", role);
    }

    [Fact]
    public void NormalizeRole_RejectsSql()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Lakebase.FeatureBranchDdl.NormalizeRole("app; DROP TABLE"));
    }
}
