using SampleApp.Models;

namespace SampleApp.Tests.Hardening;

public class PagingTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 10)]
    [InlineData(400, 100)]
    public void Validate_AcceptsInRangeValues(int page, int pageSize)
        => Assert.Empty(Paging.Validate(page, pageSize));

    [Theory]
    [InlineData(0, 10, "page")]
    [InlineData(-1, 10, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public void Validate_RejectsOutOfRangeValues(int page, int pageSize, string key)
        => Assert.Equal(key, Assert.Single(Paging.Validate(page, pageSize)).Key);
}
