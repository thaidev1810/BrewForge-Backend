using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>A published course with a class at a branch, ready for a test to open and teach.</summary>
public sealed record ClassSetup(long CourseId, long RecipeVersionId, long ClassId, IReadOnlyList<long> SessionIds,
    IReadOnlyList<long> ModuleIds);

/// <summary>Builds classes, sessions and enrolments through the API, the way a trainer would.</summary>
public static class TrainingScenario
{
    public const string Classes = "/api/v1/training-classes";
    public const string Sessions = "/api/v1/training-sessions";
    public const string Enrollments = "/api/v1/enrollments";
    public const string Regulations = "/api/v1/training-regulations";

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// Arranges a certificate that "was issued earlier". The application has
    /// no way to create one directly, which is the point; a test that only
    /// needs a certified trainer puts the row where the database keeps them.
    /// </summary>
    public static Task CertifyAsync(this BrewForgeApiFactory factory, long userId, long courseId, long recipeVersionId) =>
        factory.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"INSERT INTO certificate (user_id, course_id, recipe_version_id) VALUES ({userId}, {courseId}, {recipeVersionId})"));

    /// <summary>
    /// A PUBLISHED course and a PLANNED class for it, with the given number
    /// of sessions taught by the seeded trainer, who is certified first.
    /// </summary>
    public static async Task<ClassSetup> NewClassAsync(this BrewForgeApiFactory factory, int sessions = 0,
        string branchCode = "B01")
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var course = await factory.NewPublishedCourseAsync();
        var courseId = course.Id();
        var versionId = course.GetProperty("recipeVersionId").GetInt64();
        var moduleIds = course.GetProperty("modules").EnumerateArray().Select(m => m.Id()).ToList();

        var created = await (await trainer.PostAsJsonAsync(Classes, new
            {
                courseId, branchId = await factory.BranchIdAsync(branchCode), name = $"Class {Guid.NewGuid():N}"[..16],
                startDate = Today, endDate = Today.AddDays(30),
            }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var classId = created.Id();

        var sessionIds = new List<long>();
        if (sessions > 0)
        {
            await factory.CertifyAsync(await factory.UserIdAsync(TestUsers.Trainer), courseId, versionId);
            for (var i = 0; i < sessions; i++)
            {
                sessionIds.Add((await factory.AddSessionAsync(classId, dayOffset: i)).Id());
            }
        }
        return new ClassSetup(courseId, versionId, classId, sessionIds, moduleIds);
    }

    public static async Task<JsonElement> AddSessionAsync(this BrewForgeApiFactory factory, long classId,
        int dayOffset = 0, HttpStatusCode expected = HttpStatusCode.Created, long? trainerId = null,
        long[]? moduleIds = null)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var response = await trainer.PostAsJsonAsync($"{Classes}/{classId}/sessions", new
        {
            scheduledDate = Today.AddDays(dayOffset), startTime = "09:00", durationMinutes = 120, location = "Branch 1",
            trainerId = trainerId ?? await factory.UserIdAsync(TestUsers.Trainer), moduleIds = moduleIds ?? [],
        });
        return await response.ShouldBeAsync(expected);
    }

    /// <summary>Opens the class. Without ids it enrols every active trainee of the class's branch.</summary>
    public static async Task<JsonElement> OpenClassAsync(this BrewForgeApiFactory factory, long classId,
        long[]? traineeIds = null)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return await (await trainer.PostAsJsonAsync($"{Classes}/{classId}/open", new { traineeIds }))
            .ShouldBeAsync(HttpStatusCode.OK);
    }

    public static Task<long> EnrollmentIdAsync(this BrewForgeApiFactory factory, long classId, string username) =>
        factory.WithDbAsync(db => db.Enrollments
            .Where(e => e.TrainingClassId == classId && e.User.Username == username)
            .Select(e => e.Id).SingleAsync());

    /// <summary>The learner completes the given modules, in order.</summary>
    public static async Task<JsonElement> CompleteModulesAsync(this BrewForgeApiFactory factory, string username,
        long enrollmentId, IEnumerable<long> moduleIds)
    {
        using var learner = await factory.ClientForAsync(username);
        JsonElement last = default;
        foreach (var moduleId in moduleIds)
        {
            last = await (await learner.PostAsync($"{Enrollments}/{enrollmentId}/modules/{moduleId}/complete", null))
                .ShouldBeAsync(HttpStatusCode.OK);
        }
        return last;
    }

    public static async Task<HttpResponseMessage> RecordAttendanceAsync(this BrewForgeApiFactory factory, long sessionId,
        params (long EnrollmentId, string Status)[] entries)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return await trainer.PutAsJsonAsync($"{Sessions}/{sessionId}/attendance", new
        {
            entries = entries.Select(e => new { enrollmentId = e.EnrollmentId, status = e.Status, note = (string?)null }),
        });
    }

    public static async Task<JsonElement> EligibilityAsync(this BrewForgeApiFactory factory, long enrollmentId,
        string username = TestUsers.Trainer)
    {
        using var client = await factory.ClientForAsync(username);
        return await (await client.GetAsync($"{Enrollments}/{enrollmentId}/eligibility")).ShouldBeAsync(HttpStatusCode.OK);
    }

    public static async Task<JsonElement> GetEnrollmentAsync(this BrewForgeApiFactory factory, long enrollmentId)
    {
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        return await (await trainer.GetAsync($"{Enrollments}/{enrollmentId}")).ShouldBeAsync(HttpStatusCode.OK);
    }
}
