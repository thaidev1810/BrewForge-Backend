using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests;

/// <summary>
/// The schema in db/migrations is authoritative. These tests hold the code to
/// it: the migration creates exactly what the developer pack promises, and
/// every entity maps column for column onto its table - no invented column,
/// no forgotten one.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SchemaMappingTests(BrewForgeApiFactory factory)
{
    [Fact]
    public async Task Migration_creates_the_34_tables_62_foreign_keys_and_3_triggers()
    {
        var (tables, foreignKeys, triggers) = await factory.WithDbAsync(async db => (
            await ScalarAsync(db, """SELECT count(*)::int AS "Value" FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'"""),
            await ScalarAsync(db, """SELECT count(*)::int AS "Value" FROM information_schema.table_constraints WHERE table_schema = 'public' AND constraint_type = 'FOREIGN KEY'"""),
            await ScalarAsync(db, """SELECT count(DISTINCT trigger_name)::int AS "Value" FROM information_schema.triggers WHERE trigger_schema = 'public'""")));

        Assert.Equal(34, tables);
        Assert.Equal(62, foreignKeys);
        Assert.Equal(3, triggers);
    }

    [Fact]
    public async Task Every_entity_maps_column_for_column_onto_its_table()
    {
        var problems = await factory.WithDbAsync(async db =>
        {
            var found = new List<string>();
            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName()!;
                var actual = await db.Database
                    .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = {table}")
                    .ToListAsync();
                if (actual.Count == 0)
                {
                    found.Add($"{entity.ClrType.Name}: table '{table}' does not exist");
                    continue;
                }

                var mapped = entity.GetProperties().Select(p => p.GetColumnName()).ToList();
                found.AddRange(mapped.Except(actual).Select(c => $"{table}.{c}: mapped by {entity.ClrType.Name} but not in the schema"));
                found.AddRange(actual.Except(mapped).Select(c => $"{table}.{c}: in the schema but not mapped by {entity.ClrType.Name}"));
            }
            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Every_entity_can_be_read_through_its_mapping()
    {
        await factory.WithDbAsync(async db =>
        {
            foreach (var entity in db.Model.GetEntityTypes())
            {
                // Materialising one row exercises every column type and every enum code.
                var query = (IQueryable<object>)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                    .MakeGenericMethod(entity.ClrType).Invoke(db, null)!;
                await query.IgnoreQueryFilters().Take(1).ToListAsync();
            }
        });
    }

    [Fact]
    public async Task Eight_roles_of_the_enumeration_are_seeded()
    {
        var roles = await factory.WithDbAsync(db =>
            db.Database.SqlQuery<string>($"SELECT role_name AS \"Value\" FROM role ORDER BY role_name").ToListAsync());

        Assert.Equal(
        [
            "ADMIN", "BRANCH_MANAGER", "QUALITY_AUDITOR", "RD_MANAGER", "RD_SPECIALIST", "TRAINEE", "TRAINER",
            "TRAINING_MANAGER",
        ], roles);
    }

    private static Task<int> ScalarAsync(DbContext db, string sql) =>
        db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
