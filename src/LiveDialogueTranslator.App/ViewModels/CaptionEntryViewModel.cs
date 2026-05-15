using LiveDialogueTranslator.App.Models;
using LiveDialogueTranslator.Core.Transcripts;

namespace LiveDialogueTranslator.App.ViewModels;

public sealed class CaptionEntryViewModel
{
    public CaptionEntryViewModel(
        CaptionEntry entry,
        string? translatedText = null,
        CaptionDisplayMode displayMode = CaptionDisplayMode.Original)
    {
        Id = entry.Id;
        SpeakerId = entry.SpeakerId;
        SpeakerName = entry.SpeakerName;
        OriginalText = entry.Text;
        TranslatedText = translatedText ?? "";
        Text = OriginalText;
        DisplayOriginalText = BuildDisplayOriginalText(OriginalText, TranslatedText, displayMode);
        DisplayTranslatedText = BuildDisplayTranslatedText(OriginalText, TranslatedText, displayMode);
        DisplayText = BuildDisplayText(DisplayOriginalText, DisplayTranslatedText);
        LatencyText = entry.LatencyMs.HasValue ? $"{entry.LatencyMs.Value} ms" : "";
        IsFinal = entry.IsFinal;
        StartMs = entry.StartMs;
        EndMs = entry.EndMs;
    }

    public Guid Id { get; }
    public string SpeakerId { get; }
    public string SpeakerName { get; }
    public string OriginalText { get; }
    public string TranslatedText { get; }
    public string DisplayOriginalText { get; }
    public string DisplayTranslatedText { get; }
    public string Text { get; }
    public string DisplayText { get; }
    public string LatencyText { get; }
    public bool IsFinal { get; }
    public long StartMs { get; }
    public long EndMs { get; }

    private static string BuildDisplayOriginalText(string original, string translated, CaptionDisplayMode displayMode)
    {
        return displayMode switch
        {
            CaptionDisplayMode.Translated when !string.IsNullOrWhiteSpace(translated) => "",
            _ => original
        };
    }

    private static string BuildDisplayTranslatedText(string original, string translated, CaptionDisplayMode displayMode)
    {
        if (IsDuplicateTranslation(original, translated))
        {
            return "";
        }

        return displayMode is CaptionDisplayMode.Translated or CaptionDisplayMode.Both
            ? translated
            : "";
    }

    private static bool IsDuplicateTranslation(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translated))
        {
            return false;
        }

        return string.Equals(
            NormalizeComparableText(original),
            NormalizeComparableText(translated),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableText(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildDisplayText(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return translated;
        }

        return string.IsNullOrWhiteSpace(translated)
            ? original
            : $"{original}{Environment.NewLine}{Environment.NewLine}{translated}";
    }
}
