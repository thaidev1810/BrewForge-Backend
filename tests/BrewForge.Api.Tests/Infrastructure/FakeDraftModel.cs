using System.Collections.Concurrent;
using BrewForge.Application.Recipes.Drafting;

namespace BrewForge.Api.Tests.Infrastructure;

/// <summary>
/// Stands in for the language model. A test scripts the answers it wants, in
/// order, and afterwards reads back what the application sent.
/// </summary>
public sealed class FakeDraftModel : IRecipeDraftModel
{
    private readonly ConcurrentQueue<Func<DraftModelRequest, string>> _script = new();
    private readonly ConcurrentQueue<DraftModelRequest> _calls = new();

    public string ModelName => "fake-draft-model";

    public IReadOnlyList<DraftModelRequest> Calls => [.. _calls];

    public FakeDraftModel Answer(string rawResponse)
    {
        _script.Enqueue(_ => rawResponse);
        return this;
    }

    public FakeDraftModel Fail(DraftModelFailure failure)
    {
        _script.Enqueue(_ => throw new DraftModelException(failure, $"scripted failure: {failure}"));
        return this;
    }

    public void Reset()
    {
        _script.Clear();
        _calls.Clear();
    }

    public Task<string> CompleteAsync(DraftModelRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue(request);
        if (!_script.TryDequeue(out var answer))
        {
            throw new InvalidOperationException("The test called the model more often than it scripted answers.");
        }
        return Task.FromResult(answer(request));
    }
}
