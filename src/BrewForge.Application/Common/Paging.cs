using System.Linq.Expressions;
using BrewForge.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Common;

/// <summary>The <c>?page=1&amp;size=20&amp;sort=field,asc</c> parameters of every list endpoint.</summary>
public sealed class PageQuery
{
    public const int MaxSize = 100;

    public int Page { get; init; } = 1;
    public int Size { get; init; } = 20;
    public string? Sort { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

/// <summary>
/// The fields a list may be sorted by. Anything else is refused, so a client
/// can never sort on a column the endpoint did not mean to expose.
/// </summary>
public sealed class SortMap<T>(string defaultField, Expression<Func<T, long>> tieBreaker)
{
    private readonly Dictionary<string, LambdaExpression> _fields = new(StringComparer.OrdinalIgnoreCase);

    public SortMap<T> Add<TKey>(string field, Expression<Func<T, TKey>> key)
    {
        _fields[field] = key;
        return this;
    }

    public IQueryable<T> Apply(IQueryable<T> query, string? sort)
    {
        var parts = (string.IsNullOrWhiteSpace(sort) ? defaultField : sort).Split(',', StringSplitOptions.TrimEntries);
        var field = parts[0];
        var direction = parts.Length > 1 ? parts[1] : "asc";

        var descending = direction.Equals("desc", StringComparison.OrdinalIgnoreCase);
        if (parts.Length > 2 || (!descending && !direction.Equals("asc", StringComparison.OrdinalIgnoreCase)))
        {
            throw DomainException.Validation("The sort parameter is invalid.",
                new ErrorDetail("sort", "expected 'field,asc' or 'field,desc'"));
        }
        if (!_fields.TryGetValue(field, out var key))
        {
            throw DomainException.Validation("The sort parameter is invalid.",
                new ErrorDetail("sort", $"unknown field '{field}'. Allowed: {string.Join(", ", _fields.Keys)}"));
        }

        var method = descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy);
        var ordered = (IOrderedQueryable<T>)query.Provider.CreateQuery<T>(
            Expression.Call(typeof(Queryable), method, [typeof(T), key.ReturnType], query.Expression,
                Expression.Quote(key)));

        // The id makes the order total, so a row cannot appear on two pages.
        return ordered.ThenBy(tieBreaker);
    }
}

public static class PagingExtensions
{
    public static async Task<PagedResult<TOut>> ToPagedAsync<T, TOut>(this IQueryable<T> query, PageQuery paging,
        SortMap<T> sortMap, Func<T, TOut> map, CancellationToken cancellationToken)
    {
        new FieldErrors()
            .Check(paging.Page >= 1, "page", "must be 1 or greater")
            .Check(paging.Size is >= 1 and <= PageQuery.MaxSize, "size", $"must be between 1 and {PageQuery.MaxSize}")
            .ThrowIfAny();

        var total = await query.CountAsync(cancellationToken);
        var rows = await sortMap.Apply(query, paging.Sort)
            .Skip((paging.Page - 1) * paging.Size)
            .Take(paging.Size)
            .ToListAsync(cancellationToken);

        return new PagedResult<TOut>([.. rows.Select(map)], paging.Page, paging.Size, total);
    }

    /// <summary>Parses an enumerated query-string filter such as <c>?status=ACTIVE</c>.</summary>
    public static T? ParseFilter<T>(string? value, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (EnumCode<T>.TryParse(value.Trim(), out var parsed)) return parsed;
        throw DomainException.Validation("A filter value is invalid.",
            new ErrorDetail(field, $"must be one of: {string.Join(", ", EnumCode<T>.AllCodes)}"));
    }
}
