using System.Globalization;
using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Application.Launch;
using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Sales;

/// <summary>One line of an uploaded file as text, with the line number the user sees in it.</summary>
public sealed record PosFileRow(int RowNumber, IReadOnlyList<string> Cells);

/// <summary>Reads the lines of a CSV or XLSX file. It knows file formats and nothing about sales.</summary>
public interface IPosFileReader
{
    /// <summary>Every non-empty line, the header line included. Refuses a file it cannot read.</summary>
    Task<IReadOnlyList<PosFileRow>> ReadAsync(Stream content, string fileName, CancellationToken cancellationToken);
}

/// <summary>
/// The import result of the API contract. <c>Replaced</c> counts the accepted
/// rows that replaced a record already stored for their day (BR-25);
/// <c>Replayed</c> is true when the request repeated an Idempotency-Key and
/// nothing was imported a second time.
/// </summary>
public sealed record PosImportResultDto(string JobId, string FileName, int Accepted, int Rejected, int Replaced,
    IReadOnlyList<PosRowError> Errors, DateTimeOffset ImportedAt, bool Replayed = false);

/// <summary>
/// UC-23: sales imported from a POS export (SCR-27). Every line is judged on
/// its own. The lines that are accepted are applied even when others are
/// rejected, and each rejected line is reported with its row number and one
/// reason.
/// </summary>
public sealed class PosImportService(IBrewForgeDbContext db, IPosFileReader reader, LaunchHistory launchHistory,
    ICurrentUser currentUser, TimeProvider clock)
{
    public const int MaxRows = 20_000;

    private static readonly JsonSerializerOptions StoredFormat = new(JsonSerializerDefaults.Web);

    /// <summary>What is kept of an import: its result, as the payload of its audit entry.</summary>
    private sealed record StoredImport(PosImportResultDto Result, string? IdempotencyKey, long? BranchId);

    private sealed record Candidate(PosImportLine Line, long BranchId, string BranchCode, long RecipeId, string RecipeCode);

    public async Task<PosImportResultDto> ImportAsync(Stream content, string fileName, string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        if (idempotencyKey is not null && await FindReplayAsync(userId, idempotencyKey, cancellationToken) is { } replay)
        {
            return replay;
        }

        fileName = Path.GetFileName(fileName ?? "");
        if (fileName.Length > 255) fileName = fileName[..255];
        var rows = await reader.ReadAsync(content, fileName, cancellationToken);
        PosImportLine.EnsureLayout(rows.Count == 0 ? null : rows[0].Cells);
        if (rows.Count - 1 > MaxRows)
        {
            throw DomainException.Validation("The file is too large.",
                new ErrorDetail("file", $"has {rows.Count - 1} lines; at most {MaxRows} are imported at once"));
        }

        var now = clock.GetUtcNow();
        var errors = new List<PosRowError>();
        var candidates = await ResolveAsync(rows.Skip(1), TradingCalendar.DateOf(now), errors, cancellationToken);

        // Everything the accepted lines can touch, loaded once.
        var branchIds = candidates.Select(c => c.BranchId).Distinct().ToList();
        var recipeIds = candidates.Select(c => c.RecipeId).Distinct().ToList();
        var launches = (await db.BranchLaunchStatuses.AsNoTracking()
                .Where(l => branchIds.Contains(l.BranchId) && recipeIds.Contains(l.RecipeId))
                .ToListAsync(cancellationToken))
            .ToDictionary(l => (l.BranchId, l.RecipeId));
        var history = await launchHistory.LoadAsync([.. launches.Values.Select(l => l.Id)], cancellationToken);
        var firstDay = candidates.Count == 0 ? default : candidates.Min(c => c.Line.TradingDate);
        var lastDay = candidates.Count == 0 ? default : candidates.Max(c => c.Line.TradingDate);
        var stored = new SalesBook(await db.SalesRecords
            .Where(s => branchIds.Contains(s.BranchId) && recipeIds.Contains(s.RecipeId)
                        && s.TradingDate >= firstDay && s.TradingDate <= lastDay)
            .ToListAsync(cancellationToken));

        var jobNumber = Random.Shared.NextInt64(1, 1L << 48);
        var jobId = jobNumber.ToString("x", CultureInfo.InvariantCulture);
        var inThisFile = new SalesBook([]);
        var (accepted, replaced) = (0, 0);

        foreach (var candidate in candidates)
        {
            var line = candidate.Line;
            SalesRecord record;
            try
            {
                var launch = launches.GetValueOrDefault((candidate.BranchId, candidate.RecipeId))
                             ?? throw BranchLaunchStatus.NotLiveOn(line.TradingDate);
                record = SalesRecord.Record(launch, LaunchHistory.Of(history, launch.Id), line.TradingDate,
                    line.Quantity, SalesSource.PosImport, userId, now);
            }
            catch (DomainException refusal) when (refusal.Rule == "BR-24")
            {
                errors.Add(new PosRowError(line.Row, ErrorCodes.ImportBranchNotLive,
                    $"Branch {candidate.BranchCode} was not live on {line.TradingDate:yyyy-MM-dd} for {candidate.RecipeCode}"));
                continue;
            }

            // BR-25 inside the file: a day appears once. Which of two lines is right is not for the import to guess.
            if (!inThisFile.TryAdd(record))
            {
                errors.Add(new PosRowError(line.Row, ErrorCodes.ImportDuplicateDay,
                    $"Duplicate day for {candidate.BranchCode} / {candidate.RecipeCode} / {line.TradingDate:yyyy-MM-dd}"));
                continue;
            }

            accepted++;
            var existing = stored.Find(record.BranchId, record.RecipeId, record.TradingDate);
            if (existing is null)
            {
                db.SalesRecords.Add(record);
                continue;
            }

            // BR-25 against what is stored: the day is replaced, and the trail keeps what it said before.
            var before = new { cupsSold = existing.CupsSold, source = existing.Source.Code() };
            if (existing.ReplaceFromImport(line.Quantity, userId, now))
            {
                replaced++;
                db.Audit(AuditEntities.SalesRecord, () => existing.Id, AuditActions.ReplaceSales, new
                {
                    jobId, row = line.Row, existing.TradingDate, from = before,
                    to = new { cupsSold = existing.CupsSold, source = existing.Source.Code() },
                });
            }
        }

        var result = new PosImportResultDto(jobId, fileName, accepted, errors.Count, replaced,
            [.. errors.OrderBy(error => error.Row)], now);
        db.Audit(AuditEntities.SalesImport, () => jobNumber, AuditActions.PosImport,
            new StoredImport(result, idempotencyKey, currentUser.RestrictedToBranchId));
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>The stored result of an import. A store-level caller sees the imports of its own branch.</summary>
    public async Task<PosImportResultDto> GetResultAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(jobId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var jobNumber))
        {
            throw DomainException.NotFound("Import", jobId);
        }
        var payload = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.SalesImport && a.EntityId == jobNumber && a.Action == AuditActions.PosImport)
            .Select(a => a.PayloadJson).FirstOrDefaultAsync(cancellationToken);
        var import = payload is null ? null : JsonSerializer.Deserialize<StoredImport>(payload, StoredFormat);

        if (import is null || (currentUser.RestrictedToBranchId is { } own && import.BranchId != own))
        {
            throw DomainException.NotFound("Import", jobId);
        }
        return import.Result;
    }

    /// <summary>
    /// Reads every line and finds the branch and the drink it names. What is
    /// returned can be judged against the launch status; the rest is already
    /// in <paramref name="errors"/>.
    /// </summary>
    private async Task<List<Candidate>> ResolveAsync(IEnumerable<PosFileRow> rows, DateOnly today,
        List<PosRowError> errors, CancellationToken cancellationToken)
    {
        var lines = new List<PosImportLine>();
        foreach (var row in rows)
        {
            if (PosImportLine.Parse(row.RowNumber, row.Cells, out var error) is { } line) lines.Add(line);
            else errors.Add(error!);
        }

        // Codes are matched without regard to case: a POS export is not consistent about it.
        var drinkCodes = lines.Select(l => l.DrinkCode.ToUpperInvariant()).Distinct().ToList();
        var recipes = (await db.Recipes.AsNoTracking().Where(r => drinkCodes.Contains(r.RecipeCode.ToUpper()))
                .Select(r => new { r.Id, r.RecipeCode }).ToListAsync(cancellationToken))
            .ToDictionary(r => r.RecipeCode, StringComparer.OrdinalIgnoreCase);
        // Unfiltered on purpose: a line for another branch must be told apart from a branch that does not exist.
        var branches = (await db.Branches.AsNoTracking().IgnoreQueryFilters()
                .Select(b => new { b.Id, b.BranchCode }).ToListAsync(cancellationToken))
            .ToDictionary(b => b.BranchCode, StringComparer.OrdinalIgnoreCase);
        var own = currentUser.RestrictedToBranchId;

        var candidates = new List<Candidate>();
        foreach (var line in lines)
        {
            if (!branches.TryGetValue(line.BranchCode, out var branch))
            {
                errors.Add(new PosRowError(line.Row, ErrorCodes.ImportUnknownBranch, $"Unknown branch code {line.BranchCode}"));
            }
            else if (own is not null && branch.Id != own)
            {
                errors.Add(new PosRowError(line.Row, ErrorCodes.ImportBranchNotPermitted,
                    $"Branch {branch.BranchCode} is not your branch"));
            }
            else if (!recipes.TryGetValue(line.DrinkCode, out var recipe))
            {
                errors.Add(new PosRowError(line.Row, ErrorCodes.ImportUnknownDrink, $"Unknown drink code {line.DrinkCode}"));
            }
            else if (line.TradingDate > today)
            {
                errors.Add(new PosRowError(line.Row, ErrorCodes.ImportInvalidDate,
                    $"Trading date {line.TradingDate:yyyy-MM-dd} is in the future"));
            }
            else
            {
                candidates.Add(new Candidate(line, branch.Id, branch.BranchCode, recipe.Id, recipe.RecipeCode));
            }
        }
        return candidates;
    }

    /// <summary>
    /// The original result of an import this user already made under this
    /// Idempotency-Key, so that a retried upload does not import twice.
    /// </summary>
    private async Task<PosImportResultDto?> FindReplayAsync(long userId, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var imports = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityType == AuditEntities.SalesImport && a.Action == AuditActions.PosImport && a.UserId == userId)
            .OrderByDescending(a => a.Id).Select(a => a.PayloadJson).Take(200).ToListAsync(cancellationToken);

        var original = imports.OfType<string>()
            .Select(payload => JsonSerializer.Deserialize<StoredImport>(payload, StoredFormat))
            .FirstOrDefault(import => import?.IdempotencyKey == idempotencyKey);
        return original is null ? null : original.Result with { Replayed = true };
    }
}
