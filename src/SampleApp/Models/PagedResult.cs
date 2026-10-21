namespace SampleApp.Models;

/// <summary>One page of a collection, with the totals a client needs to request the rest.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);
