using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrewForge.Application.Recipes.Drafting;
using Microsoft.Extensions.Options;

namespace BrewForge.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>An OpenAI-compatible API root, without a trailing slash.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>Supplied by configuration or the environment. Never committed, never sent to the browser (SE-01).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>A model that supports the chosen <see cref="JsonMode"/>.</summary>
    public string Model { get; set; } = "";

    /// <summary>How the answer is held to the schema; see <see cref="LlmJsonMode"/>.</summary>
    public LlmJsonMode JsonMode { get; set; } = LlmJsonMode.StructuredOutput;

    /// <summary>MSG-E05: the call is abandoned after this long.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>How the provider is asked for an answer of the schema's shape.</summary>
public enum LlmJsonMode
{
    /// <summary>The schema as a strict <c>json_schema</c> response format (OpenAI Structured Outputs).</summary>
    StructuredOutput,

    /// <summary>
    /// Plain JSON mode (<c>json_object</c>) with the schema written into the system prompt, for providers
    /// without Structured Outputs, such as DeepSeek. The provider then does not enforce the shape; the
    /// application's parser does, as it always does (BR-07).
    /// </summary>
    JsonObject,
}

/// <summary>
/// The adapter behind <see cref="IRecipeDraftModel"/>: one call to an
/// OpenAI-compatible chat endpoint (OpenAI, DeepSeek, a self-hosted server),
/// with <c>recipe-draft.schema.json</c> as a strict <c>json_schema</c>, or in
/// the prompt under plain JSON mode. It returns the text of the
/// answer and nothing more; whether that text conforms is decided by the
/// application layer, which does not take the provider's word for it.
/// </summary>
public sealed class OpenAiRecipeDraftModel(HttpClient http, IOptions<LlmOptions> options) : IRecipeDraftModel
{
    private readonly LlmOptions _options = options.Value;

    public string ModelName => string.IsNullOrWhiteSpace(_options.Model) ? "(not configured)" : _options.Model;

    public async Task<string> CompleteAsync(DraftModelRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new DraftModelException(DraftModelFailure.NotConfigured,
                $"{LlmOptions.Section}:ApiKey and {LlmOptions.Section}:Model must be configured.");
        }

        var structured = _options.JsonMode == LlmJsonMode.StructuredOutput;
        var system = structured
            ? request.SystemPrompt
            : $"{request.SystemPrompt}\n\nAnswer with one JSON object and nothing else. It must be valid against this JSON Schema:\n{request.JsonSchema}";
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
            ["response_format"] = structured
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = RecipeDraftSchema.Name,
                        ["strict"] = true,
                        ["schema"] = JsonNode.Parse(request.JsonSchema),
                    },
                }
                : new JsonObject { ["type"] = "json_object" },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Content = JsonContent.Create(body);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            using var response = await http.SendAsync(message, timeout.Token);
            var payload = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new DraftModelException(DraftModelFailure.Unavailable,
                    $"The model endpoint answered {(int)response.StatusCode}.");
            }
            return ExtractContent(payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DraftModelException(DraftModelFailure.TimedOut,
                $"The model did not answer within {_options.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException exception)
        {
            throw new DraftModelException(DraftModelFailure.Unavailable, "The model endpoint could not be reached.",
                exception);
        }
    }

    /// <summary>
    /// The answer is the content of the first choice. A refusal, or an
    /// envelope of an unexpected shape, is handed back as it is: it will not
    /// satisfy the schema, and it belongs in the log.
    /// </summary>
    private static string ExtractContent(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString()!;
            }
            return payload;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
                                              or IndexOutOfRangeException or InvalidOperationException)
        {
            return payload;
        }
    }
}
