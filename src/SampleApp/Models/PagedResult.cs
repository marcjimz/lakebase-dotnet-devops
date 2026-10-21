using Microsoft.EntityFrameworkCore;

namespace SampleApp.Models;

/// <summary>One page of a collection, with the totals a client needs to request the rest.</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public static class Paging
{
    public const int DefaultPageSize = 10;
    public const int MaxPageSize = 100;

    /// <summary>Validation errors for out-of-range paging input, keyed by parameter name.</summary>
    public static IEnumerable<(string Key, string Error)> Validate(int page, int pageSize)
    {
        if (page < 1)
            yield return (nameof(page), "page must be 1 or greater.");
        if (pageSize is < 1 or > MaxPageSize)
            yield return (nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
    }

    /// <summary>Runs one count and one page query. The caller supplies a stable order.</summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IOrderedQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return new PagedResult<T>(items, page, pageSize, totalCount, totalPages);
    }
}
