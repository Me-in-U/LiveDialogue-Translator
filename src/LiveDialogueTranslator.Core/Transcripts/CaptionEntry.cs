namespace LiveDialogueTranslator.Core.Transcripts;

public sealed record CaptionEntry(
    Guid Id,
    string SpeakerId,
    string SpeakerName,
    string Text,
    long StartMs,
    long EndMs,
    int? LatencyMs,
    DateTimeOffset CapturedAt,
    bool IsFinal);
