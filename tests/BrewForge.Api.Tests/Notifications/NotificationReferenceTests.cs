using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrewForge.Api.Scheduling;
using BrewForge.Api.Tests.Infrastructure;
using BrewForge.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrewForge.Api.Tests.Notifications;

/// <summary>
/// BE-05: a notification says what it is about - <c>referenceType</c> and <c>referenceId</c>, with every id it
/// was raised with in <c>context</c> - so the client opens the right screen without reading the subject; and
/// the reminders, run twice at once, still say each thing once.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationReferenceTests(BrewForgeApiFactory factory)
{
    private const string Inbox = "/api/v1/me/notifications";

    [Fact]
    public async Task A_course_assigned_points_at_the_new_enrolment_which_its_reader_can_open()
    {
        var course = await factory.NewPublishedCourseAsync();
        var user = await NewTraineeAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var result = await (await trainer.PostAsJsonAsync("/api/v1/assignments", new
            { courseId = course.Id(), branchId = await factory.BranchIdAsync("B03"), employeeIds = new[] { user.Id } }))
            .ShouldBeAsync(HttpStatusCode.Created);
        var enrollmentId = Assert.Single(result.GetProperty("enrollmentIds").EnumerateArray()).GetInt64();

        using var self = await factory.ClientForAsync(user.Username);
        var notice = (await (await self.GetAsync($"{Inbox}?size=100")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray()
            .Single(n => n.GetProperty("subject").GetString() == "Course assigned"
                         && n.GetProperty("referenceId").ValueKind == JsonValueKind.Number
                         && n.GetProperty("referenceId").GetInt64() == enrollmentId);

        // The id of a row saved in the same step as the notification, not a 0.
        Assert.Equal("ENROLLMENT", notice.GetProperty("referenceType").GetString());
        Assert.Equal(course.Id(), notice.GetProperty("context").GetProperty("courseId").GetInt64());
        // Opened through the endpoint of that row, which checks the reader like any read.
        await (await self.GetAsync($"/api/v1/enrollments/{enrollmentId}")).ShouldBeAsync(HttpStatusCode.OK);
        using var stranger = await factory.ClientForAsync("trainee4");
        Assert.True((await stranger.GetAsync($"/api/v1/enrollments/{enrollmentId}")).StatusCode
            is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_certificate_issued_points_at_the_certificate_and_no_reference_is_left_at_0()
    {
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var certificate = await factory.CertifyThroughTheCourseAsync(await factory.StudyAsync(setup, TestUsers.Trainee));
        var traineeId = await factory.UserIdAsync(TestUsers.Trainee);

        var issued = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.UserId == traineeId && n.Subject == "Certificate issued").OrderByDescending(n => n.Id).FirstAsync());
        Assert.Equal((NotificationReference.Certificate, certificate.Id()), (issued.ReferenceType, issued.ReferenceId!.Value));

        // Every notice that points somewhere points at a stored row: none is left with the id of a row not yet saved.
        var referenced = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.ReferenceType != null).OrderByDescending(n => n.Id).Take(200).ToListAsync());
        Assert.All(referenced, n => Assert.True(n.ReferenceId > 0, $"{n.Subject} points at {n.ReferenceType} {n.ReferenceId}"));
    }

    [Fact]
    public async Task A_notification_about_nothing_to_open_has_no_reference()
    {
        var userId = await factory.UserIdAsync("trainee3");
        var id = await factory.WithDbAsync(async db =>
        {
            var plain = new Notification(userId, $"Plain {Guid.NewGuid():N}"[..14], "Nothing to open.", DateTimeOffset.UtcNow);
            db.Notifications.Add(plain);
            await db.SaveChangesAsync();
            return plain.Id;
        });

        using var trainee = await factory.ClientForAsync("trainee3");
        var notice = (await (await trainee.GetAsync($"{Inbox}?size=100")).ShouldBeAsync(HttpStatusCode.OK))
            .GetProperty("items").EnumerateArray().Single(n => n.Id() == id);
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null), (notice.GetProperty("referenceType").ValueKind,
            notice.GetProperty("referenceId").ValueKind, notice.GetProperty("context").ValueKind));
    }

    [Fact]
    public async Task Two_runs_of_the_reminders_at_once_remind_once()
    {
        var user = await NewTraineeAsync();
        var course = await factory.NewPublishedCourseAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var enrollmentId = Assert.Single((await (await trainer.PostAsJsonAsync("/api/v1/assignments", new
            { courseId = course.Id(), branchId = await factory.BranchIdAsync("B03"), employeeIds = new[] { user.Id } }))
            .ShouldBeAsync(HttpStatusCode.Created)).GetProperty("enrollmentIds").EnumerateArray()).GetInt64();
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2);
        await factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE enrollment SET due_date = {dueDate} WHERE id = {enrollmentId}"));
        var scheduler = factory.Services.GetRequiredService<TrainingReminderScheduler>();

        await Task.WhenAll(scheduler.RunOnceAsync(CancellationToken.None), scheduler.RunOnceAsync(CancellationToken.None));

        var reminders = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.UserId == user.Id && n.Subject == "Course due soon" && n.ReferenceId == enrollmentId).ToListAsync());
        var single = Assert.Single(reminders);
        Assert.Equal(NotificationReference.Enrollment, single.ReferenceType);
    }

    private async Task<(long Id, string Username)> NewTraineeAsync()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var username = $"be5{Guid.NewGuid():N}"[..14];
        var created = await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username, email = $"{username}@brewforge.local", password = BrewForgeApiFactory.SeedPassword,
            fullName = "Notice Test", role = "TRAINEE", branchId = await factory.BranchIdAsync("B03"),
        })).ShouldBeAsync(HttpStatusCode.Created);
        return (created.Id(), username);
    }
}
