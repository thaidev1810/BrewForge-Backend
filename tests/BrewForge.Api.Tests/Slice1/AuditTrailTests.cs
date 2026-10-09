using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Audit;
using BrewForge.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BrewForge.Api.Tests.Slice1;

/// <summary>
/// SE-05: every state-changing operation is recorded with actor, timestamp
/// and payload, in a log that nothing can update or delete.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuditTrailTests(BrewForgeApiFactory factory)
{
    [Fact]
    public async Task State_change_is_recorded_with_actor_time_and_payload()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var adminId = await factory.UserIdAsync(TestUsers.Admin);
        var code = $"ING-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var created = await (await admin.PostAsJsonAsync("/api/v1/ingredients",
                new { ingredientCode = code, name = "Audited ingredient", unit = "g", shelfLifeHours = 3 }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt64();
        await (await admin.PostAsync($"/api/v1/ingredients/{id}/deactivate", null)).ShouldBeAsync(HttpStatusCode.OK);

        var entries = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "Ingredient" && a.EntityId == id)
            .OrderBy(a => a.Id)
            .ToListAsync());

        Assert.Equal(["CREATE", "DEACTIVATE"], entries.Select(e => e.Action));
        Assert.All(entries, entry =>
        {
            Assert.Equal(adminId, entry.UserId);
            Assert.True(entry.CreatedAt >= before);
        });
        var payload = JsonSerializer.Deserialize<JsonElement>(entries[0].PayloadJson!);
        Assert.Equal(code, payload.GetProperty("ingredientCode").GetString());
    }

    [Fact]
    public async Task Refused_request_leaves_no_audit_entry()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var countBefore = await factory.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.EntityType == "Ingredient"));

        await (await admin.PostAsJsonAsync("/api/v1/ingredients",
                new { ingredientCode = "ING-OOLONG", name = "Duplicate", unit = "g", shelfLifeHours = 3 }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict);

        Assert.Equal(countBefore,
            await factory.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.EntityType == "Ingredient")));
    }

    [Fact]
    public async Task Login_is_audited_against_the_user_who_logged_in()
    {
        var userId = await factory.UserIdAsync(TestUsers.TrainingManager);

        await factory.LoginAsync(TestUsers.TrainingManager);

        var logins = await factory.WithDbAsync(db => db.AuditLogs
            .Where(a => a.EntityType == "AppUser" && a.EntityId == userId && a.Action == "LOGIN")
            .ToListAsync());
        Assert.NotEmpty(logins);
        Assert.All(logins, login => Assert.Equal(userId, login.UserId));
    }

    [Theory]
    [InlineData("UPDATE audit_log SET action = 'TAMPERED'")]
    [InlineData("DELETE FROM audit_log")]
    public async Task Database_rejects_any_update_or_delete_of_the_audit_log(string sql)
    {
        await factory.LoginAsync(TestUsers.Admin); // guarantees at least one row

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql)));

        Assert.Contains("append-only", exception.MessageText);
    }

    [Fact]
    public async Task Application_cannot_change_or_remove_an_audit_entry()
    {
        await factory.LoginAsync(TestUsers.Admin);

        await factory.WithDbAsync(async db =>
        {
            var entry = await db.AuditLogs.OrderBy(a => a.Id).FirstAsync();

            db.Entry(entry).Property(nameof(AuditLog.Action)).CurrentValue = "TAMPERED";
            var onUpdate = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Equal("AUDIT", onUpdate.Rule);

            db.ChangeTracker.Clear();
            db.Remove(await db.AuditLogs.OrderBy(a => a.Id).FirstAsync());
            var onDelete = await Assert.ThrowsAsync<DomainException>(() => db.SaveChangesAsync());
            Assert.Equal(ErrorKind.RuleViolation, onDelete.Kind);
        });
    }
}
