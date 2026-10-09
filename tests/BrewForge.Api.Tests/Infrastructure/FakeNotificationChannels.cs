using System.Collections.Concurrent;
using BrewForge.Application.Abstractions;

namespace BrewForge.Api.Tests.Infrastructure;

public sealed record SentEmail(string ToAddress, string ToName, string Subject, string Body);

public sealed record SentPush(PushTarget Target, string PayloadJson);

/// <summary>The mail server the API talks to in tests: it keeps what it is given and sends nothing.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<SentEmail> Sent { get; } = new();

    public bool IsConfigured { get; set; } = true;

    /// <summary>While set, every message is refused, as by a mail server that is down.</summary>
    public bool Down { get; set; }

    public Task SendAsync(string toAddress, string toName, string subject, string body,
        CancellationToken cancellationToken)
    {
        if (Down) throw new InvalidOperationException("The mail server is not answering.");
        Sent.Enqueue(new SentEmail(toAddress, toName, subject, body));
        return Task.CompletedTask;
    }

    public void Reset()
    {
        Sent.Clear();
        (IsConfigured, Down) = (true, false);
    }
}

/// <summary>The push services the API talks to in tests.</summary>
public sealed class FakePushSender : IPushSender
{
    public const string Key = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";

    public ConcurrentQueue<SentPush> Sent { get; } = new();

    /// <summary>What the push service of an endpoint answers. Anything not listed is delivered.</summary>
    public ConcurrentDictionary<string, PushResult> Answers { get; } = new();

    public bool IsConfigured { get; set; } = true;

    public string? PublicKey => IsConfigured ? Key : null;

    public Task<PushResult> SendAsync(PushTarget target, string payloadJson, CancellationToken cancellationToken)
    {
        var answer = Answers.GetValueOrDefault(target.Endpoint, PushResult.Delivered);
        if (answer == PushResult.Delivered) Sent.Enqueue(new SentPush(target, payloadJson));
        return Task.FromResult(answer);
    }

    public void Reset()
    {
        Sent.Clear();
        Answers.Clear();
        IsConfigured = true;
    }
}
