using System.Text.Json;
using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.Launch;
using BrewForge.Domain.MasterData;
using BrewForge.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.Launch;

// ---------------------------------------------------------------- requests

public sealed record PilotCriterionRequest(CriterionType? Type, decimal? CupsPerDayPerBranch, long? ControlRecipeId,
    decimal? MinPercentOfControl, decimal? MaxDropSecondHalfPct);

/// <summary>
/// UC-21. <c>criteria</c> is the list that is stored in <c>criteria_json</c>.
/// The version is chosen when the pilot is created and does not change.
/// </summary>
public sealed record PilotRequest(long? RecipeVersionId, string? Name, DateOnly? StartDate, DateOnly? EndDate,
    int? MinCertifiedStaff, IReadOnlyList<long>? BranchIds, IReadOnlyList<PilotCriterionRequest>? Criteria);

public sealed record DecisionRequest(LaunchDecisionType? Decision, string? Note);

// ---------------------------------------------------------------- responses

public sealed record PilotBranchDto(long BranchId, string? BranchCode, string? BranchName,
    PilotBranchState ReadinessState, DateTimeOffset? WentLiveAt);

/// <summary>The decision, with the figures exactly as they were evaluated when it was taken (BR-27).</summary>
public sealed record LaunchDecisionDto(LaunchDecisionType Decision, long DecidedBy, DateTimeOffset DecidedAt,
    JsonElement Evaluated);

public sealed record PilotDto(long Id, long RecipeVersionId, long RecipeId, string RecipeCode, string RecipeName,
    int VersionNo, string Name, DateOnly StartDate, DateOnly EndDate, int MinCertifiedStaff, PilotState State,
    long CreatedBy, IReadOnlyList<PilotCriterion> Criteria, IReadOnlyList<PilotBranchDto> Branches,
    LaunchDecisionDto? Decision);

/// <summary>One pilot branch against the gate: certified staff counted now, against the threshold.</summary>
public sealed record BranchReadinessDto(long BranchId, string? BranchCode, PilotBranchState ReadinessState,
    int CertifiedCount, int MinCertifiedStaff, bool CoverageMet, LaunchStatus? LaunchStatus, long? LiveVersionId,
    DateTimeOffset? WentLiveAt);

public sealed record PilotReadinessDto(long PilotId, PilotState State, long RecipeVersionId, int MinCertifiedStaff,
    IReadOnlyList<BranchReadinessDto> Branches);

/// <summary>UC-21, UC-24 and UC-25: pilots, the launch gate, the evaluation and the rollout decision.</summary>
public sealed class PilotService(IBrewForgeDbContext db, LaunchReadinessService readiness, LaunchHistory history,
    ICurrentUser currentUser, TimeProvider clock)
{
    private static readonly SortMap<PilotProgram> Sorting = new SortMap<PilotProgram>("startDate,desc", p => p.Id)
        .Add("id", p => p.Id)
        .Add("name", p => p.Name)
        .Add("startDate", p => p.StartDate)
        .Add("endDate", p => p.EndDate)
        .Add("state", p => p.State);

    /// <summary>How the evaluated figures are written into <c>evaluated_json</c>.</summary>
    private static readonly JsonSerializerOptions EvaluationFormat = new(JsonSerializerDefaults.Web)
    {
        Converters = { new EnumCodeJsonConverterFactory() },
    };

    private DateOnly Today => TradingCalendar.DateOf(clock.GetUtcNow());

    public async Task<PagedResult<PilotDto>> ListAsync(PageQuery paging, string? state, long? recipeVersionId,
        CancellationToken cancellationToken)
    {
        await EndDueAsync(cancellationToken);
        var stateFilter = PagingExtensions.ParseFilter<PilotState>(state, "state");

        var query = db.PilotPrograms.AsNoTracking().Include(p => p.Branches).Include(p => p.Decision).AsQueryable();
        if (stateFilter is not null) query = query.Where(p => p.State == stateFilter);
        if (recipeVersionId is not null) query = query.Where(p => p.RecipeVersionId == recipeVersionId);

        var page = await query.ToPagedAsync(paging, Sorting, p => p, cancellationToken);
        return new PagedResult<PilotDto>(await ToDtosAsync(page.Items, cancellationToken), page.Page, page.Size, page.Total);
    }

    public async Task<PilotDto> GetAsync(long id, CancellationToken cancellationToken) =>
        (await ToDtosAsync([await FindAsync(id, cancellationToken)], cancellationToken))[0];

    /// <summary>UC-21. 409 BR-28 if the version already has a pilot in DRAFT or RUNNING state.</summary>
    public async Task<PilotDto> CreateAsync(PilotRequest request, CancellationToken cancellationToken)
    {
        await EndDueAsync(cancellationToken);
        Require(request, creating: true);

        var version = await db.RecipeVersions.AsNoTracking()
                          .SingleOrDefaultAsync(v => v.Id == request.RecipeVersionId, cancellationToken)
                      ?? throw DomainException.NotFound("Recipe version", request.RecipeVersionId!);
        var recipe = await db.Recipes.AsNoTracking().SingleAsync(r => r.Id == version.RecipeId, cancellationToken);
        var criteria = await CriteriaAsync(request, cancellationToken);
        await EnsureBranchesAsync(request.BranchIds!, cancellationToken);
        var pilotsOfVersion = await db.PilotPrograms.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.RecipeVersionId == version.Id).ToListAsync(cancellationToken);

        var pilot = PilotProgram.Create(recipe, version, pilotsOfVersion, request.Name, request.StartDate!.Value,
            request.EndDate!.Value, criteria, request.MinCertifiedStaff ?? LaunchReadinessService.DefaultMinCertifiedStaff,
            request.BranchIds!.ToList(), currentUser.RequireUserId());

        db.PilotPrograms.Add(pilot);
        db.Audit(AuditEntities.PilotProgram, () => pilot.Id, AuditActions.Create, Summary(pilot));
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([pilot], cancellationToken))[0];
    }

    /// <summary>409 BR-26 once the pilot is RUNNING: what it is measured against is fixed.</summary>
    public async Task<PilotDto> UpdateAsync(long id, PilotRequest request, CancellationToken cancellationToken)
    {
        var pilot = await FindAsync(id, cancellationToken);
        // The state is judged first: a pilot that has started is not open to change, whatever the body says.
        pilot.EnsureEditable();
        Require(request, creating: false);
        if (request.RecipeVersionId is { } versionId && versionId != pilot.RecipeVersionId)
        {
            throw DomainException.Validation("The version of a pilot cannot be changed. Cancel it and create another.",
                new ErrorDetail("recipeVersionId", "cannot be changed"));
        }

        var criteria = await CriteriaAsync(request, cancellationToken);
        await EnsureBranchesAsync(request.BranchIds!, cancellationToken);
        var recipeId = await RecipeIdOfAsync(pilot, cancellationToken);

        pilot.Update(recipeId, request.Name, request.StartDate!.Value, request.EndDate!.Value, criteria,
            request.MinCertifiedStaff ?? pilot.MinCertifiedStaff, request.BranchIds!.ToList());
        db.Audit(AuditEntities.PilotProgram, () => pilot.Id, AuditActions.Update, Summary(pilot));
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([pilot], cancellationToken))[0];
    }

    /// <summary>
    /// DRAFT to RUNNING. Freezes the criteria, and plans the drink at every
    /// pilot branch that does not sell it: PREPARING until enough staff are
    /// certified, READY at once where they already are.
    /// </summary>
    public async Task<PilotDto> StartAsync(long id, CancellationToken cancellationToken)
    {
        var pilot = await FindAsync(id, cancellationToken);
        pilot.Start(Today);

        var recipeId = await RecipeIdOfAsync(pilot, cancellationToken);
        foreach (var branch in pilot.Branches)
        {
            await readiness.PlanAsync(branch.BranchId, recipeId, pilot.RecipeVersionId, pilot.MinCertifiedStaff,
                cancellationToken);
            pilot.RecomputeReadiness(branch.BranchId,
                await readiness.CertifiedCountAsync(branch.BranchId, pilot.RecipeVersionId, cancellationToken));
        }

        db.Audit(AuditEntities.PilotProgram, () => pilot.Id, AuditActions.Start, Summary(pilot));
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([pilot], cancellationToken))[0];
    }

    /// <summary>DRAFT or RUNNING to CANCELLED. What the branches sell does not change by this.</summary>
    public async Task<PilotDto> CancelAsync(long id, CancellationToken cancellationToken)
    {
        var pilot = await FindAsync(id, cancellationToken);
        pilot.Cancel();
        db.Audit(AuditEntities.PilotProgram, () => pilot.Id, AuditActions.Cancel);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([pilot], cancellationToken))[0];
    }

    /// <summary>Certified staff per pilot branch against the threshold, counted now.</summary>
    public async Task<PilotReadinessDto> ReadinessAsync(long id, CancellationToken cancellationToken)
    {
        var pilot = await FindAsync(id, cancellationToken);
        var recipeId = await RecipeIdOfAsync(pilot, cancellationToken);
        var branchIds = pilot.Branches.Select(b => b.BranchId).ToList();
        var codes = await db.Branches.AsNoTracking().IgnoreQueryFilters().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.BranchCode, cancellationToken);
        var launches = await db.BranchLaunchStatuses.AsNoTracking()
            .Where(l => l.RecipeId == recipeId && branchIds.Contains(l.BranchId))
            .ToDictionaryAsync(l => l.BranchId, cancellationToken);

        var rows = new List<BranchReadinessDto>();
        foreach (var branch in pilot.Branches.OrderBy(b => b.BranchId))
        {
            var certified = await readiness.CertifiedCountAsync(branch.BranchId, pilot.RecipeVersionId, cancellationToken);
            var launch = launches.GetValueOrDefault(branch.BranchId);
            rows.Add(new BranchReadinessDto(branch.BranchId, codes.GetValueOrDefault(branch.BranchId), branch.ReadinessState,
                certified, pilot.MinCertifiedStaff, certified >= pilot.MinCertifiedStaff, launch?.Status,
                launch is { IsLive: true } ? launch.RecipeVersionId : null, branch.WentLiveAt));
        }
        return new PilotReadinessDto(pilot.Id, pilot.State, pilot.RecipeVersionId, pilot.MinCertifiedStaff, rows);
    }

    /// <summary>
    /// The launch gate. 409 BR-23 if the branch is not READY: the certified
    /// staff are counted at this moment, and the branch is opened for sale
    /// only if there are enough of them. After a ROLLOUT decision the same
    /// gate opens the drink at the other branches of the chain.
    /// </summary>
    public async Task<PilotDto> GoLiveAsync(long id, long branchId, CancellationToken cancellationToken)
    {
        var pilot = await FindAsync(id, cancellationToken);
        var recipeId = await RecipeIdOfAsync(pilot, cancellationToken);
        var rolledOut = pilot is { State: PilotState.Ended, Decision.Decision: LaunchDecisionType.Rollout };
        if (!rolledOut) pilot.EnsureGateApplies(branchId); // before anything is looked up: is there a gate to pass?

        var launch = await db.BranchLaunchStatuses
                         .SingleOrDefaultAsync(l => l.BranchId == branchId && l.RecipeId == recipeId, cancellationToken)
                     ?? throw DomainException.NotFound("Launch status of branch", branchId);
        var certified = await readiness.CertifiedCountAsync(branchId, pilot.RecipeVersionId, cancellationToken);
        var now = clock.GetUtcNow();

        var previousVersion = rolledOut
            ? launch.OpenForSale(pilot.RecipeVersionId, pilot.MinCertifiedStaff, certified, now)
            : pilot.GoLive(branchId, launch, certified, now);

        if (previousVersion is not null) history.RecordVersionMove(launch, previousVersion);
        db.Audit(AuditEntities.BranchLaunchStatus, () => launch.Id, AuditActions.GoLive, new
        {
            pilotId = pilot.Id, branchId, recipeId, recipeVersionId = pilot.RecipeVersionId, certifiedCount = certified,
            pilot.MinCertifiedStaff,
        });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([pilot], cancellationToken))[0];
    }

    /// <summary>UC-24. A verdict per criterion, per branch and overall; interim while the pilot is running.</summary>
    public async Task<PilotEvaluation> EvaluationAsync(long id, CancellationToken cancellationToken) =>
        await EvaluateAsync(await FindAsync(id, cancellationToken), cancellationToken);

    /// <summary>
    /// UC-25. 409 BR-27 unless the pilot is ENDED. The figures as evaluated at
    /// this moment are stored with the decision. ROLLOUT plans the drink at
    /// every other active branch, each of which then goes live through the
    /// gate; DISCONTINUE withdraws it from the pilot branches; REVISE changes
    /// nothing at the branches and sends the recipe back for a new version.
    /// </summary>
    public async Task<PilotDto> DecideAsync(long id, DecisionRequest request, CancellationToken cancellationToken)
    {
        new FieldErrors().Check(request.Decision is not null, "decision", "is required: ROLLOUT, REVISE or DISCONTINUE")
            .MaxLength("note", request.Note, 500).ThrowIfAny();
        var pilot = await FindAsync(id, cancellationToken);
        var evaluation = await EvaluateAsync(pilot, cancellationToken);

        var decision = pilot.Decide(request.Decision!.Value, JsonSerializer.Serialize(evaluation, EvaluationFormat),
            currentUser.RequireUserId(), clock.GetUtcNow());
        var recipeId = await RecipeIdOfAsync(pilot, cancellationToken);
        var pilotBranchIds = pilot.Branches.Select(b => b.BranchId).ToList();

        if (decision.Decision == LaunchDecisionType.Rollout)
        {
            var others = await db.Branches.Where(b => b.Status == BranchStatus.Active && !pilotBranchIds.Contains(b.Id))
                .Select(b => b.Id).ToListAsync(cancellationToken);
            foreach (var branchId in others)
            {
                await readiness.PlanAsync(branchId, recipeId, pilot.RecipeVersionId, pilot.MinCertifiedStaff, cancellationToken);
            }
        }
        else if (decision.Decision == LaunchDecisionType.Discontinue)
        {
            var live = await db.BranchLaunchStatuses
                .Where(l => l.RecipeId == recipeId && pilotBranchIds.Contains(l.BranchId) && l.Status == LaunchStatus.Live
                            && l.RecipeVersionId == pilot.RecipeVersionId)
                .ToListAsync(cancellationToken);
            foreach (var launch in live)
            {
                launch.Withdraw();
                history.RecordWithdrawal(launch);
            }
        }

        db.Audit(AuditEntities.PilotProgram, () => pilot.Id, AuditActions.Decide, new
        {
            decision = decision.Decision.Code(), overall = evaluation.Overall.Code(), evaluation.CoverageComplete,
            note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([pilot], cancellationToken))[0];
    }

    /// <summary>
    /// RUNNING to ENDED belongs to the calendar, not to a user: a pilot ends
    /// the day after its <c>end_date</c>. The scheduler of the API makes the
    /// transition on its own; it is also made here the first time anything
    /// looks at pilots after that day, which is before anything can depend on
    /// it, so nothing waits for the scheduler's next run.
    /// </summary>
    public async Task EndDueAsync(CancellationToken cancellationToken)
    {
        var today = Today;
        var due = await db.PilotPrograms.IgnoreQueryFilters()
            .Where(p => p.State == PilotState.Running && p.EndDate < today).ToListAsync(cancellationToken);
        foreach (var pilot in due.Where(pilot => pilot.EndIfDue(today)))
        {
            db.Audit(AuditEntities.PilotProgram, () => pilot.Id, AuditActions.End,
                new { pilot.EndDate, trigger = "end_date passed" });
        }
        if (due.Count > 0) await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<PilotEvaluation> EvaluateAsync(PilotProgram pilot, CancellationToken cancellationToken)
    {
        var branchIds = pilot.Branches.Select(b => b.BranchId).ToList();
        var sales = await db.SalesRecords.AsNoTracking()
            .Where(s => s.RecipeVersionId == pilot.RecipeVersionId && branchIds.Contains(s.BranchId)
                        && s.TradingDate >= pilot.StartDate && s.TradingDate <= pilot.EndDate)
            .Select(s => new DailyCups(s.BranchId, s.TradingDate, s.CupsSold)).ToListAsync(cancellationToken);

        var controlRecipeId = pilot.Criteria().ControlRecipeId;
        List<DailyCups> control = controlRecipeId is null
            ? []
            : await db.SalesRecords.AsNoTracking()
                .Where(s => s.RecipeId == controlRecipeId && branchIds.Contains(s.BranchId)
                            && s.TradingDate >= pilot.StartDate && s.TradingDate <= pilot.EndDate)
                .Select(s => new DailyCups(s.BranchId, s.TradingDate, s.CupsSold)).ToListAsync(cancellationToken);

        return PilotEvaluator.Evaluate(pilot, Today, sales, control);
    }

    private async Task<PilotProgram> FindAsync(long id, CancellationToken cancellationToken)
    {
        await EndDueAsync(cancellationToken);
        return await db.PilotPrograms.Include(p => p.Branches).Include(p => p.Decision)
                   .SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
               ?? throw DomainException.NotFound("Pilot", id);
    }

    private Task<long> RecipeIdOfAsync(PilotProgram pilot, CancellationToken cancellationToken) =>
        db.RecipeVersions.Where(v => v.Id == pilot.RecipeVersionId).Select(v => v.RecipeId).SingleAsync(cancellationToken);

    private static void Require(PilotRequest request, bool creating) =>
        new FieldErrors()
            .Check(!creating || request.RecipeVersionId is not null, "recipeVersionId", "is required")
            .Check(request.StartDate is not null, "startDate", "is required")
            .Check(request.EndDate is not null, "endDate", "is required")
            .Check(request.BranchIds is { Count: > 0 }, "branchIds", "at least one branch is required")
            .Check(request.Criteria is { Count: > 0 }, "criteria", "at least one criterion is required")
            .Check(request.Criteria is null || request.Criteria.All(c => c.Type is not null), "criteria",
                "every criterion needs a type: ABSOLUTE, RELATIVE or RETENTION")
            .ThrowIfAny();

    private async Task<PilotCriteria> CriteriaAsync(PilotRequest request, CancellationToken cancellationToken)
    {
        var criteria = PilotCriteria.Create(request.Criteria!.Select(c => new PilotCriterion(c.Type!.Value,
            c.CupsPerDayPerBranch, c.ControlRecipeId, c.MinPercentOfControl, c.MaxDropSecondHalfPct)));
        if (criteria.ControlRecipeId is { } controlId && !await db.Recipes.AnyAsync(r => r.Id == controlId, cancellationToken))
        {
            throw DomainException.Validation("The control drink does not exist.",
                new ErrorDetail("criteria", $"controlRecipeId {controlId} is not a recipe"));
        }
        return criteria;
    }

    private async Task EnsureBranchesAsync(IReadOnlyList<long> branchIds, CancellationToken cancellationToken)
    {
        var active = await db.Branches.AsNoTracking()
            .Where(b => branchIds.Contains(b.Id) && b.Status == BranchStatus.Active).Select(b => b.Id)
            .ToListAsync(cancellationToken);
        var unknown = branchIds.Except(active).ToList();
        if (unknown.Count > 0)
        {
            throw DomainException.Validation("A pilot runs at active branches.",
                new ErrorDetail("branchIds", $"not an active branch: {string.Join(", ", unknown)}"));
        }
    }

    private static object Summary(PilotProgram pilot) => new
    {
        pilot.RecipeVersionId, pilot.Name, pilot.StartDate, pilot.EndDate, pilot.MinCertifiedStaff,
        state = pilot.State.Code(), branches = pilot.Branches.Select(b => b.BranchId),
        criteria = pilot.Criteria().Criteria,
    };

    private async Task<IReadOnlyList<PilotDto>> ToDtosAsync(IReadOnlyList<PilotProgram> pilots,
        CancellationToken cancellationToken)
    {
        var versionIds = pilots.Select(p => p.RecipeVersionId).Distinct().ToList();
        var versions = await db.RecipeVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .Join(db.Recipes, v => v.RecipeId, r => r.Id, (v, r) => new { v.Id, v.VersionNo, RecipeId = r.Id, r.RecipeCode, r.Name })
            .ToDictionaryAsync(v => v.Id, cancellationToken);
        var branchIds = pilots.SelectMany(p => p.Branches).Select(b => b.BranchId).Distinct().ToList();
        var branches = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => new { b.BranchCode, b.Name }, cancellationToken);

        return
        [
            .. pilots.Select(pilot =>
            {
                var version = versions[pilot.RecipeVersionId];
                return new PilotDto(pilot.Id, pilot.RecipeVersionId, version.RecipeId, version.RecipeCode, version.Name,
                    version.VersionNo, pilot.Name, pilot.StartDate, pilot.EndDate, pilot.MinCertifiedStaff, pilot.State,
                    pilot.CreatedBy, pilot.Criteria().Criteria,
                    [
                        .. pilot.Branches.OrderBy(b => b.BranchId).Select(b =>
                        {
                            var branch = branches.GetValueOrDefault(b.BranchId);
                            return new PilotBranchDto(b.BranchId, branch?.BranchCode, branch?.Name, b.ReadinessState, b.WentLiveAt);
                        }),
                    ],
                    pilot.Decision is { } decision
                        ? new LaunchDecisionDto(decision.Decision, decision.DecidedBy, decision.DecidedAt,
                            JsonSerializer.Deserialize<JsonElement>(decision.EvaluatedJson))
                        : null);
            }),
        ];
    }
}
