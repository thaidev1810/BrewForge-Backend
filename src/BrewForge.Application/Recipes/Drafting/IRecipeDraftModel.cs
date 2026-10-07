namespace BrewForge.Application.Recipes.Drafting;

/// <summary>What is sent to the language model: the two prompts and the JSON schema the answer must satisfy.</summary>
public sealed record DraftModelRequest(string SystemPrompt, string UserPrompt, string JsonSchema)
{
    /// <summary>The text stored in <c>ai_draft_log.prompt_text</c> (BR-06).</summary>
    public string PromptText => $"[system]\n{SystemPrompt}\n\n[user]\n{UserPrompt}";
}

/// <summary>
/// The language model behind UC-06 and UC-08. The application only knows this
/// port; the adapter that calls the real service lives in the infrastructure
/// layer, and the tests supply a fake.
/// </summary>
public interface IRecipeDraftModel
{
    /// <summary>The model identifier, stored with every call (BR-06).</summary>
    string ModelName { get; }

    /// <summary>
    /// Returns the raw text of the model's answer, whatever it is. Checking it
    /// against the schema is the caller's job, not the adapter's.
    /// </summary>
    /// <exception cref="DraftModelException">The call failed or timed out and there is no answer.</exception>
    Task<string> CompleteAsync(DraftModelRequest request, CancellationToken cancellationToken);
}

public enum DraftModelFailure
{
    /// <summary>No API key or model is configured.</summary>
    NotConfigured,
    /// <summary>The service did not answer within the time limit (MSG-E05).</summary>
    TimedOut,
    /// <summary>The service answered with an error, or could not be reached.</summary>
    Unavailable,
}

public sealed class DraftModelException(DraftModelFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public DraftModelFailure Failure { get; } = failure;
}
