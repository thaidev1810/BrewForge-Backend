using BrewForge.Application.Abstractions;
using BrewForge.Application.Common;
using BrewForge.Domain.Common;
using BrewForge.Domain.MasterData;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Application.MasterData;

public sealed record SettingDto(string Key, string Value, DateTimeOffset UpdatedAt, long? UpdatedBy);

public sealed record SettingRequest(string? Value);

/// <summary>The few values an administrator maintains (Admin "System Settings"). The keys are fixed; they are not created or deleted here.</summary>
public sealed class SettingsService(IBrewForgeDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<IReadOnlyList<SettingDto>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await db.SystemSettings.AsNoTracking().OrderBy(s => s.SettingKey).ToListAsync(cancellationToken)).Select(ToDto)];

    public async Task<SettingDto> UpdateAsync(string key, SettingRequest request, CancellationToken cancellationToken)
    {
        var setting = await db.SystemSettings.SingleOrDefaultAsync(s => s.SettingKey == key, cancellationToken)
            ?? throw DomainException.NotFound("Setting", key);

        setting.Set(request.Value!, currentUser.UserId, clock.GetUtcNow());
        db.Audit(AuditEntities.SystemSetting, () => setting.Id, AuditActions.Update, new { key, setting.Value });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(setting);
    }

    /// <summary>The audit pass mark in force: the setting, or the default if it is missing or unreadable.</summary>
    public async Task<int> AuditPassMarkAsync(CancellationToken cancellationToken)
    {
        var value = await db.SystemSettings.AsNoTracking().Where(s => s.SettingKey == SystemSetting.AuditPassMark)
            .Select(s => s.Value).SingleOrDefaultAsync(cancellationToken);
        return int.TryParse(value, out var mark) ? mark : Domain.Quality.BranchAudit.DefaultPassMark;
    }

    private static SettingDto ToDto(SystemSetting s) => new(s.SettingKey, s.Value, s.UpdatedAt, s.UpdatedBy);
}
