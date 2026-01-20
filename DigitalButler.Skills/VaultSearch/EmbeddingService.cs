using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DigitalButler.Common;

namespace DigitalButler.Skills.VaultSearch;

public interface IEmbeddingService
{
    Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default);
}

public class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly AiSettingsResolver _resolver;
    private readonly VaultSearchOptions _options;
    private readonly ILogger<OpenAiEmbeddingService> _logger;

    private const string TaskName = "embeddings";
    private const int MaxBatchSize = 2048; // OpenAI limit
    private const int MaxRetries = 3;

    public OpenAiEmbeddingService(
        HttpClient httpClient,
        AiSettingsResolver resolver,
        IOptions<VaultSearchOptions> options,
        ILogger<OpenAiEmbeddingService> logger)
    {
        _httpClient = httpClient;
        _resolver = resolver;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var results = await GetEmbeddingsAsync(new[] { text }, ct);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> GetEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var settings = await _resolver.ResolveAsync(TaskName, ct);
        var apiKey = settings.ApiKey;
        var baseUrl = settings.BaseUrl ?? "https://api.openai.com/v1";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("AI API key is not configured for embeddings");
        }

        // Process in batches
        var allEmbeddings = new List<float[]>();
        var batches = textList.Chunk(MaxBatchSize).ToList();

        _logger.LogDebug("Generating embeddings for {Count} texts in {Batches} batches", textList.Count, batches.Count);

        foreach (var batch in batches)
        {
            var batchEmbeddings = await GetBatchEmbeddingsAsync(batch, apiKey, baseUrl, ct);
            allEmbeddings.AddRange(batchEmbeddings);
        }

        return allEmbeddings;
    }

    private async Task<List<float[]>> GetBatchEmbeddingsAsync(
        string[] texts,
        string apiKey,
        string baseUrl,
        CancellationToken ct)
    {
        var endpoint = $"{baseUrl.TrimEnd('/')}/embeddings";
        var model = _options.EmbeddingModel;

        var body = new
        {
            model = model,
            input = texts,
            encoding_format = "float"
        };

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request, ct);
                var rawBody = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    if ((int)response.StatusCode == 429 && attempt < MaxRetries - 1)
                    {
                        // Rate limited - wait and retry
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                        _logger.LogWarning("Rate limited by embeddings API, waiting {Delay} before retry", delay);
                        await Task.Delay(delay, ct);
                        continue;
                    }

                    throw new HttpRequestException(
                        $"Embeddings request failed with {(int)response.StatusCode} {response.ReasonPhrase}. Body: {rawBody}");
                }

                return ParseEmbeddingsResponse(rawBody);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxRetries - 1)
            {
                _logger.LogWarning(ex, "Embeddings request failed, attempt {Attempt}/{MaxRetries}", attempt + 1, MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }

        throw new InvalidOperationException($"Failed to get embeddings after {MaxRetries} attempts");
    }

    private static List<float[]> ParseEmbeddingsResponse(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var dataArray))
        {
            throw new InvalidOperationException("Embeddings response missing 'data' field");
        }

        var results = new List<float[]>();
        foreach (var item in dataArray.EnumerateArray())
        {
            if (!item.TryGetProperty("embedding", out var embeddingArray))
            {
                throw new InvalidOperationException("Embedding item missing 'embedding' field");
            }

            var embedding = new float[embeddingArray.GetArrayLength()];
            int i = 0;
            foreach (var value in embeddingArray.EnumerateArray())
            {
                embedding[i++] = value.GetSingle();
            }
            results.Add(embedding);
        }

        // Sort by index to maintain order
        if (root.TryGetProperty("data", out var dataWithIndex))
        {
            var indexed = new List<(int index, float[] embedding)>();
            int idx = 0;
            foreach (var item in dataWithIndex.EnumerateArray())
            {
                var itemIndex = item.TryGetProperty("index", out var indexProp) ? indexProp.GetInt32() : idx;
                indexed.Add((itemIndex, results[idx]));
                idx++;
            }
            return indexed.OrderBy(x => x.index).Select(x => x.embedding).ToList();
        }

        return results;
    }
}
