using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// Talks to the local runtime over HTTP. The runtime exposes the llama.cpp
/// server API, so a completion is POST /completion and an embedding is
/// POST /embedding. Nothing here runs on the UI thread and every call takes a
/// token.
/// </summary>
public sealed class InferenceEndpoint(HttpClient httpClient)
{
    /// <summary>
    /// One round trip. Returns the generated text, or throws
    /// <see cref="InferenceUnavailableException"/> when the runtime is not
    /// reachable, which is what puts an entry into its failed state (D2).
    /// </summary>
    public async Task<string> CompleteAsync(
        string prompt,
        double temperature,
        CancellationToken cancellationToken)
    {
        var request = new CompletionRequest
        {
            Prompt = prompt,
            Temperature = temperature,
            PredictTokens = 1024,
            Stream = false,
        };

        var response = await PostAsync("/completion", request, cancellationToken).ConfigureAwait(false);

        var body = await response.Content
            .ReadFromJsonAsync<CompletionResponse>(cancellationToken)
            .ConfigureAwait(false);

        return body?.Content?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// One embedding, used by the indexing pipeline. Returns the vector as
    /// float32, which is what the index stores.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var response = await PostAsync("/embedding", new EmbeddingRequest { Content = text }, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content
            .ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
            .ConfigureAwait(false);

        return body?.Embedding ?? [];
    }

    /// <summary>True when the runtime answers. Used to gate routes that need a model.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync("/health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> PostAsync<T>(
        string path,
        T payload,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.PostAsJsonAsync(path, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InferenceUnavailableException("The local runtime is not running.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new InferenceUnavailableException($"The local runtime answered {(int)response.StatusCode}.");
        }

        return response;
    }

    private sealed class CompletionRequest
    {
        [JsonPropertyName("prompt")]
        public required string Prompt { get; init; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("n_predict")]
        public int PredictTokens { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }
    }

    private sealed class CompletionResponse
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed class EmbeddingRequest
    {
        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; init; }
    }
}

/// <summary>
/// The runtime could not be reached or refused the call. Callers turn this into
/// the entry's failed state rather than a toast.
/// </summary>
public sealed class InferenceUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
