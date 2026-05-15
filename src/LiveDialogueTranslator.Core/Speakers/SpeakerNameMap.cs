using LiveDialogueTranslator.Core.Localization;

namespace LiveDialogueTranslator.Core.Speakers;

public sealed class SpeakerNameMap
{
    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly ResolvedAppLanguage language;

    public SpeakerNameMap(ResolvedAppLanguage language = ResolvedAppLanguage.English)
    {
        this.language = language;
    }

    public IReadOnlyDictionary<string, string> Names => names;

    public void Rename(string speakerId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            throw new ArgumentException("Speaker id is required.", nameof(speakerId));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            names.Remove(speakerId);
            return;
        }

        names[speakerId] = displayName.Trim();
    }

    public string DisplayName(string speakerId)
    {
        if (string.Equals(speakerId, "mic", StringComparison.OrdinalIgnoreCase))
        {
            return language == ResolvedAppLanguage.Korean ? "나" : "You";
        }

        if (names.TryGetValue(speakerId, out var displayName))
        {
            return displayName;
        }

        if (speakerId.StartsWith("speaker_", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(speakerId["speaker_".Length..], out var index))
        {
            return language == ResolvedAppLanguage.Korean ? $"화자 {index}" : $"Speaker {index}";
        }

        return speakerId;
    }
}
