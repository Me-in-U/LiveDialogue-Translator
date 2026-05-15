using System.Net.Http;
using System.Text;
using System.Text.Json;
using LiveDialogueTranslator.App.Models;

namespace LiveDialogueTranslator.App.Services;

public sealed class TranslationService : IDisposable
{
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    public Task<string> TranslateAsync(string text, string targetLanguage, TranslateProvider provider, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult("");
        }

        return provider switch
        {
            TranslateProvider.Google => TranslateGoogleAsync(text, targetLanguage, token),
            _ => DummyTranslateAsync(provider, text, token)
        };
    }

    private async Task<string> TranslateGoogleAsync(string text, string targetLanguage, CancellationToken token)
    {
        var target = NormalizeGoogleTargetLanguage(targetLanguage);
        var url = "https://translate.googleapis.com/translate_a/single" +
                  "?client=gtx&sl=auto&dt=t" +
                  $"&tl={Uri.EscapeDataString(target)}" +
                  $"&q={Uri.EscapeDataString(text)}";

        using var response = await httpClient.GetAsync(url, token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return text;
        }

        var translatedSegments = root[0];
        if (translatedSegments.ValueKind != JsonValueKind.Array)
        {
            return text;
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

        return builder.Length == 0 ? text : builder.ToString();
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
