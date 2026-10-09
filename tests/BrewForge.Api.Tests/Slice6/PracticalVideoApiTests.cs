using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BrewForge.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static BrewForge.Api.Tests.Infrastructure.AssessmentScenario;

namespace BrewForge.Api.Tests.Slice6;

/// <summary>
/// The recording of a practical through the API: who uploads it (BR-14, the
/// trainer who runs the practical), that an evaluation cannot be recorded
/// without one, and who may watch it afterwards.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PracticalVideoApiTests(BrewForgeApiFactory factory)
{
    private const string Videos = "/api/v1/practical-videos";

    [Fact]
    public async Task Recording_is_uploaded_by_the_trainer_kept_and_served_back()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        var trainerId = await factory.UserIdAsync(TestUsers.Trainer);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var response = await factory.UploadPracticalVideoAsync(learner, fileName: "B01 trainee.MP4");
        var video = await response.ShouldBeAsync(HttpStatusCode.Created);

        Assert.Equal($"{Videos}/{video.Id()}", response.Headers.Location?.OriginalString);
        Assert.Equal((learner.EnrollmentId, trainerId, "B01 trainee.MP4", "video/mp4", SampleVideo.LongLength),
            (video.GetProperty("enrollmentId").GetInt64(), video.GetProperty("uploadedBy").GetInt64(),
                video.GetProperty("fileName").GetString(), video.GetProperty("contentType").GetString(),
                video.GetProperty("sizeBytes").GetInt64()));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(SampleVideo)), video.GetProperty("sha256").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, video.GetProperty("evaluationId").ValueKind);

        // Served back byte for byte, and in ranges, so that a player can seek.
        using var whole = await trainer.GetAsync($"{Videos}/{video.Id()}");
        Assert.Equal((HttpStatusCode.OK, "video/mp4"), (whole.StatusCode, whole.Content.Headers.ContentType?.MediaType));
        Assert.Equal(SampleVideo, await whole.Content.ReadAsByteArrayAsync());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Videos}/{video.Id()}");
        request.Headers.Range = new RangeHeaderValue(100, 199);
        using var part = await trainer.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(SampleVideo[100..200], await part.Content.ReadAsByteArrayAsync());

        Assert.True(await factory.WithDbAsync(db => db.AuditLogs.AnyAsync(a =>
            a.EntityType == "Enrollment" && a.EntityId == learner.EnrollmentId && a.Action == "UPLOAD_PRACTICAL_VIDEO"
            && a.UserId == trainerId)));
    }

    [Fact]
    public async Task Recording_is_watched_by_its_trainee_and_by_those_who_oversee_training_and_by_no_other_trainee()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        var video = await (await factory.UploadPracticalVideoAsync(learner)).ShouldBeAsync(HttpStatusCode.Created);

        foreach (var username in new[] { TestUsers.Trainee, TestUsers.TrainingManager, TestUsers.Auditor })
        {
            using var viewer = await factory.ClientForAsync(username);
            using var response = await viewer.GetAsync($"{Videos}/{video.Id()}");
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{username}: {(int)response.StatusCode}");
        }

        // A colleague of the same branch is still not the trainee: for them it does not exist.
        using var colleague = await factory.ClientForAsync("trainee2");
        await (await colleague.GetAsync($"{Videos}/{video.Id()}")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
        await (await colleague.GetAsync($"{learner.Url}/practical-videos")).ShouldBeErrorAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Practical_evaluation_is_refused_without_the_recording_of_the_practical()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        await (await factory.AttemptQuizAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var checklist = await factory.ChecklistAsync(learner.Setup.CourseId);
        var items = checklist.Select(stepId => new { recipeStepId = stepId, passed = true });

        var refusal = await (await trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation", new { items }))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "PRACTICAL_VIDEO");
        var unknown = await (await trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation",
            new { practicalVideoId = 999999999, items })).ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("practicalVideoId", refusal.DetailFields());
        Assert.Contains("practicalVideoId", unknown.DetailFields());
        // Every item passed and the quiz is passed, and still nobody is certified: there was nothing to judge from.
        Assert.Equal(0, await factory.WithDbAsync(db => db.Enrollments.Where(e => e.Id == learner.EnrollmentId)
            .SelectMany(e => e.PracticalEvaluations).CountAsync()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Certificates.CountAsync(c => c.CourseId == learner.Setup.CourseId)));
    }

    [Fact]
    public async Task Evaluation_names_its_recording_and_a_recording_is_the_evidence_of_one_evaluation()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);
        var checklist = await factory.ChecklistAsync(learner.Setup.CourseId);
        var practicalVideoId = (await (await factory.UploadPracticalVideoAsync(learner)).ShouldBeAsync(HttpStatusCode.Created)).Id();
        object Body(bool passed) => new
        {
            practicalVideoId,
            items = checklist.Select(stepId => new { recipeStepId = stepId, passed }),
        };

        var failed = await (await trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation", Body(passed: false)))
            .ShouldBeAsync(HttpStatusCode.OK);
        Assert.Equal(practicalVideoId, failed.GetProperty("practicalVideoId").GetInt64());

        // The second try of the practical is another practical: it needs its own recording.
        var again = await (await trainer.PostAsJsonAsync($"{learner.Url}/practical-evaluation", Body(passed: true)))
            .ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "PRACTICAL_VIDEO");
        Assert.Contains("practicalVideoId", again.DetailFields());
        var passed = await (await factory.EvaluatePracticalAsync(learner)).ShouldBeAsync(HttpStatusCode.OK);
        Assert.NotEqual(practicalVideoId, passed.GetProperty("practicalVideoId").GetInt64());

        // The trainee sees both recordings, each with the evaluation made from it.
        using var trainee = await factory.ClientForAsync(TestUsers.Trainee);
        var videos = (await (await trainee.GetAsync($"{learner.Url}/practical-videos")).ShouldBeAsync(HttpStatusCode.OK))
            .EnumerateArray().ToDictionary(v => v.Id(), v => v.GetProperty("evaluationId").GetInt64());
        Assert.Equal(new Dictionary<long, long>
        {
            [practicalVideoId] = failed.Id(),
            [passed.GetProperty("practicalVideoId").GetInt64()] = passed.Id(),
        }, videos);
    }

    [Fact]
    public async Task BR_14_the_trainee_never_uploads_the_recording_of_their_own_practical()
    {
        // A trainer taking the course holds the role that uploads recordings, but not of their own practical.
        var learner = await factory.NewEligibleLearnerAsync(TestUsers.Trainer);
        var stored = StoredFiles();

        await (await factory.UploadPracticalVideoAsync(learner, uploader: TestUsers.Trainer))
            .ShouldBeErrorAsync(HttpStatusCode.Forbidden, rule: "BR-14");

        Assert.False(await factory.WithDbAsync(db => db.PracticalVideos.AnyAsync(v => v.EnrollmentId == learner.EnrollmentId)));
        Assert.Equal(stored, StoredFiles());
    }

    [Fact]
    public async Task Recording_is_uploaded_and_evaluated_only_by_the_trainer_who_runs_the_practical()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        var outsider = await NewTrainerAsync();
        var checklist = await factory.ChecklistAsync(learner.Setup.CourseId);

        // Another trainer has no part in this class: not their practical to record...
        await (await factory.UploadPracticalVideoAsync(learner, uploader: outsider))
            .ShouldBeErrorAsync(HttpStatusCode.Forbidden, rule: "PRACTICAL_VIDEO");

        // ...and not theirs to judge from somebody else's recording either.
        var practicalVideoId = (await (await factory.UploadPracticalVideoAsync(learner)).ShouldBeAsync(HttpStatusCode.Created)).Id();
        using var client = await factory.ClientForAsync(outsider);
        await (await client.PostAsJsonAsync($"{learner.Url}/practical-evaluation", new
        {
            practicalVideoId,
            items = checklist.Select(stepId => new { recipeStepId = stepId, passed = true }),
        })).ShouldBeErrorAsync(HttpStatusCode.Forbidden, rule: "PRACTICAL_VIDEO");

        Assert.Equal(0, await factory.WithDbAsync(db => db.Enrollments.Where(e => e.Id == learner.EnrollmentId)
            .SelectMany(e => e.PracticalEvaluations).CountAsync()));
    }

    [Fact]
    public async Task Recording_opens_with_the_practical_once_the_enrolment_is_eligible()
    {
        var setup = await factory.NewClassAsync();
        await factory.OpenClassAsync(setup.ClassId, [await factory.UserIdAsync(TestUsers.Trainee)]);
        var enrollmentId = await factory.EnrollmentIdAsync(setup.ClassId, TestUsers.Trainee);
        var notYet = new EligibleLearner(setup, TestUsers.Trainee, await factory.UserIdAsync(TestUsers.Trainee), enrollmentId);

        await (await factory.UploadPracticalVideoAsync(notYet)).ShouldBeErrorAsync(HttpStatusCode.Conflict, rule: "BR-32");
    }

    [Theory]
    [InlineData("notes.txt", 5000)]
    [InlineData("practical.exe", 5000)]
    [InlineData("practical", 5000)]
    [InlineData("practical.mp4", 0)]
    public async Task File_that_is_not_a_video_or_is_empty_is_refused_and_nothing_is_kept(string fileName, int length)
    {
        var learner = await factory.NewEligibleLearnerAsync();
        var stored = StoredFiles();

        var refusal = await (await factory.UploadPracticalVideoAsync(learner, fileName: fileName, content: SampleVideo[..length]))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("file", refusal.DetailFields());
        Assert.Equal(stored, StoredFiles());
    }

    [Fact]
    public async Task Upload_without_a_file_is_refused()
    {
        var learner = await factory.NewEligibleLearnerAsync();
        using var trainer = await factory.ClientForAsync(TestUsers.Trainer);

        var notAForm = await (await trainer.PostAsJsonAsync($"{learner.Url}/practical-videos", new { }))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);
        using var empty = new MultipartFormDataContent { { new StringContent("x"), "note" } };
        var noFile = await (await trainer.PostAsync($"{learner.Url}/practical-videos", empty))
            .ShouldBeErrorAsync(HttpStatusCode.BadRequest);

        Assert.Contains("file", notAForm.DetailFields());
        Assert.Contains("file", noFile.DetailFields());
    }

    /// <summary>A second trainer, who opened no class and teaches no session.</summary>
    private async Task<string> NewTrainerAsync()
    {
        using var admin = await factory.ClientForAsync(TestUsers.Admin);
        var username = $"t{Guid.NewGuid():N}"[..12];
        await (await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username,
            email = $"{username}@brewforge.local",
            password = BrewForgeApiFactory.SeedPassword,
            fullName = "Another Trainer",
            role = "TRAINER",
        })).ShouldBeAsync(HttpStatusCode.Created);
        return username;
    }

    private int StoredFiles() =>
        Directory.Exists(factory.VideoRoot)
            ? Directory.EnumerateFiles(factory.VideoRoot, "*", SearchOption.AllDirectories).Count()
            : 0;
}
