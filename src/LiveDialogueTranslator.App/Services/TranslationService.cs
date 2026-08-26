using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using LiveDialogueTranslator.App.Models;

namespace LiveDialogueTranslator.App.Services;

public sealed class TranslationRateLimitException : HttpRequestException
{
    public TranslationRateLimitException(TimeSpan retryAfter)
        : base("Google translation is temporarily rate limited.", null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}

public sealed class TranslationService : IDisposable
{
    private const int MaxCacheEntries = 256;
    private static readonly TimeSpan PublicRequestInterval = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan InitialRateLimitBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumInlineRetryDelay = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan MaximumRateLimitBackoff = TimeSpan.FromSeconds(30);
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private readonly SemaphoreSlim googleRequestGate = new(1, 1);
    private readonly Dictionary<string, string> translationCache = new(StringComparer.Ordinal);
    private readonly Queue<string> translationCacheOrder = new();
    private DateTimeOffset nextPublicRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset rateLimitedUntil = DateTimeOffset.MinValue;
    private TimeSpan currentRateLimitBackoff = InitialRateLimitBackoff;

    public TranslationService()
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LiveDialogueTranslator/1.1");
    }

    public Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        TranslateProvider provider,
        string? googleApiKey,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult("");
        }

        return provider switch
        {
            TranslateProvider.Google => TranslateGoogleAsync(text, targetLanguage, googleApiKey, token),
            _ => DummyTranslateAsync(provider, text, token)
        };
    }

    private async Task<string> TranslateGoogleAsync(
        string text,
        string targetLanguage,
        string? googleApiKey,
        CancellationToken token)
    {
        var target = NormalizeGoogleTargetLanguage(targetLanguage);
        var normalizedText = text.Trim();
        var cacheKey = $"{target}\n{normalizedText}";

        await googleRequestGate.WaitAsync(token);
        try
        {
            if (translationCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var useOfficialApi = !string.IsNullOrWhiteSpace(googleApiKey);
            var responseBody = await SendGoogleRequestAsync(
                () => useOfficialApi
                    ? CreateOfficialGoogleRequest(normalizedText, target, googleApiKey!)
                    : CreatePublicGoogleRequest(normalizedText, target),
                useOfficialApi,
                token);
            var translated = useOfficialApi
                ? ParseOfficialGoogleResponse(responseBody, normalizedText)
                : ParsePublicGoogleResponse(responseBody, normalizedText);

            AddToCache(cacheKey, translated);
            return translated;
        }
        finally
        {
            googleRequestGate.Release();
        }
    }

    private async Task<string> SendGoogleRequestAsync(
        Func<HttpRequestMessage> requestFactory,
        bool useOfficialApi,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await WaitForGoogleAvailabilityAsync(useOfficialApi, token);

            using var request = requestFactory();
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!useOfficialApi)
            {
                nextPublicRequestAt = DateTimeOffset.UtcNow + PublicRequestInterval;
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Google translation request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).",
                        null,
                        response.StatusCode);
                }

                rateLimitedUntil = DateTimeOffset.MinValue;
                currentRateLimitBackoff = InitialRateLimitBackoff;
                return await response.Content.ReadAsStringAsync(token);
            }

            var retryAfter = ResolveRetryAfter(response);
            rateLimitedUntil = DateTimeOffset.UtcNow + retryAfter;
            currentRateLimitBackoff = TimeSpan.FromMilliseconds(Math.Min(
                MaximumRateLimitBackoff.TotalMilliseconds,
                Math.Max(
                    InitialRateLimitBackoff.TotalMilliseconds,
                    Math.Max(currentRateLimitBackoff.TotalMilliseconds * 2, retryAfter.TotalMilliseconds * 2))));

            if (attempt == 0 && retryAfter <= MaximumInlineRetryDelay)
            {
                continue;
            }

            throw new TranslationRateLimitException(retryAfter);
        }

        throw new TranslationRateLimitException(currentRateLimitBackoff);
    }

    private async Task WaitForGoogleAvailabilityAsync(bool useOfficialApi, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var availableAt = rateLimitedUntil;
        if (!useOfficialApi && nextPublicRequestAt > availableAt)
        {
            availableAt = nextPublicRequestAt;
        }

        if (availableAt > now)
        {
            await Task.Delay(availableAt - now, token);
        }
    }

    private TimeSpan ResolveRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return CapRetryDelay(delta);
        }

        if (retryAfter?.Date is { } retryDate)
        {
            var datedDelay = retryDate - DateTimeOffset.UtcNow;
            if (datedDelay > TimeSpan.Zero)
            {
                return CapRetryDelay(datedDelay);
            }
        }

        return CapRetryDelay(currentRateLimitBackoff);
    }

    private static TimeSpan CapRetryDelay(TimeSpan delay)
    {
        return delay <= MaximumRateLimitBackoff ? delay : MaximumRateLimitBackoff;
    }

    private static HttpRequestMessage CreatePublicGoogleRequest(string text, string target)
    {
        var url = "https://translate.googleapis.com/translate_a/single" +
                  "?client=gtx&sl=auto&dt=t" +
                  $"&tl={Uri.EscapeDataString(target)}" +
                  $"&q={Uri.EscapeDataString(text)}";
        return new HttpRequestMessage(HttpMethod.Get, url);
    }

    private static HttpRequestMessage CreateOfficialGoogleRequest(string text, string target, string apiKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://translation.googleapis.com/language/translate/v2");
        request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", apiKey.Trim());
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["q"] = text,
            ["target"] = target,
            ["format"] = "text"
        });
        return request;
    }

    private static string ParsePublicGoogleResponse(string responseBody, string originalText)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return originalText;
        }

        var translatedSegments = root[0];
        if (translatedSegments.ValueKind != JsonValueKind.Array)
        {
            return originalText;
        }

        var builder = new StringBuilder();
        foreach (var segment in translatedSegments.EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array &&
                segment.GetArrayLength() > 0 &&
                segment[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(segment[0].GetString());
            }
        }

        return builder.Length == 0 ? originalText : builder.ToString();
    }

    private static string ParseOfficialGoogleResponse(string responseBody, string originalText)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("translations", out var translations) ||
            translations.ValueKind != JsonValueKind.Array ||
            translations.GetArrayLength() == 0 ||
            !translations[0].TryGetProperty("translatedText", out var translatedText))
        {
            return originalText;
        }

        return WebUtility.HtmlDecode(translatedText.GetString()) is { Length: > 0 } decoded
            ? decoded
            : originalText;
    }

    private void AddToCache(string cacheKey, string translated)
    {
        if (translationCache.ContainsKey(cacheKey))
        {
            translationCache[cacheKey] = translated;
            return;
        }

        translationCache[cacheKey] = translated;
        translationCacheOrder.Enqueue(cacheKey);
        while (translationCacheOrder.Count > MaxCacheEntries)
        {
            translationCache.Remove(translationCacheOrder.Dequeue());
        }
    }

    private static string NormalizeGoogleTargetLanguage(string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return "ko";
        }

        var normalized = targetLanguage.Trim();
        return normalized.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    private static Task<string> DummyTranslateAsync(TranslateProvider provider, string text, CancellationToken token)
    {
        _ = provider;
        _ = text;
        token.ThrowIfCancellationRequested();
        return Task.FromResult("");
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
