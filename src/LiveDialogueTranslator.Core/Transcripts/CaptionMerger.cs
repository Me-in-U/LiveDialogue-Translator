using LiveDialogueTranslator.Core.Protocol;
using LiveDialogueTranslator.Core.Speakers;

namespace LiveDialogueTranslator.Core.Transcripts;

public sealed class CaptionMerger
{
    private const long AdjacentCaptionGapMs = 2500;
    private readonly int maxEntries;
    private readonly SpeakerNameMap speakerNames;
    private readonly List<CaptionEntry> entries = [];

    public CaptionMerger(int maxEntries, SpeakerNameMap? speakerNames = null)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "At least one entry must be retained.");
        }

        this.maxEntries = maxEntries;
        this.speakerNames = speakerNames ?? new SpeakerNameMap();
    }

    public IReadOnlyList<CaptionEntry> Entries => entries;

    public CaptionEntry? Apply(IWorkerEvent workerEvent)
    {
        return workerEvent switch
        {
            PartialCaptionEvent partial => ApplyCaption(
                partial.SpeakerId,
                partial.Text,
                partial.StartMs,
                partial.EndMs,
                partial.LatencyMs,
                isFinal: false),
            FinalCaptionEvent final => ApplyCaption(
                final.SpeakerId,
                final.Text,
                final.StartMs,
                final.EndMs,
                final.LatencyMs,
                isFinal: true),
            _ => null
        };
    }

    private CaptionEntry ApplyCaption(
        string speakerId,
        string text,
        long startMs,
        long endMs,
        int? latencyMs,
        bool isFinal)
    {
        var normalizedText = text.Trim();

        if (entries.Count > 0)
        {
            var latestPartialIndex = FindLatestSpeakerEntryIndex(speakerId, entry => !entry.IsFinal);
            if (latestPartialIndex >= 0)
            {
                var last = entries[latestPartialIndex];
                var replacement = last with
                {
                    Text = normalizedText,
                    StartMs = startMs,
                    EndMs = endMs,
                    LatencyMs = latencyMs,
                    CapturedAt = DateTimeOffset.UtcNow,
                    IsFinal = isFinal,
                    SpeakerName = speakerNames.DisplayName(speakerId)
                };
                entries[latestPartialIndex] = replacement;
                SortEntries();
                return replacement;
            }

            var latestFinalIndex = FindLatestSpeakerEntryIndex(speakerId, entry => entry.IsFinal);
            if (isFinal &&
                latestFinalIndex >= 0 &&
                entries[latestFinalIndex] is { } latestFinal &&
                startMs >= latestFinal.StartMs &&
                startMs - latestFinal.EndMs <= AdjacentCaptionGapMs)
            {
                var merged = latestFinal with
                {
                    Text = JoinCaptionText(latestFinal.Text, normalizedText),
                    EndMs = Math.Max(latestFinal.EndMs, endMs),
                    LatencyMs = latencyMs,
                    CapturedAt = DateTimeOffset.UtcNow,
                    IsFinal = true
                };
                entries[latestFinalIndex] = merged;
                SortEntries();
                return merged;
            }
        }

        var entry = NewEntry(speakerId, normalizedText, startMs, endMs, latencyMs, isFinal);
        entries.Add(entry);
        SortEntries();
        TrimSpeakerEntries(speakerId);

        return entry;
    }

    private int FindLatestSpeakerEntryIndex(string speakerId, Func<CaptionEntry, bool> predicate)
    {
        var bestIndex = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!string.Equals(entry.SpeakerId, speakerId, StringComparison.OrdinalIgnoreCase) || !predicate(entry))
            {
                continue;
            }

            if (bestIndex < 0 ||
                entry.EndMs > entries[bestIndex].EndMs ||
                entry.EndMs == entries[bestIndex].EndMs && entry.StartMs > entries[bestIndex].StartMs)
            {
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private void SortEntries()
    {
        entries.Sort((left, right) =>
        {
            var endCompare = left.EndMs.CompareTo(right.EndMs);
            if (endCompare != 0)
            {
                return endCompare;
            }

            var startCompare = left.StartMs.CompareTo(right.StartMs);
            if (startCompare != 0)
            {
                return startCompare;
            }

            return left.CapturedAt.CompareTo(right.CapturedAt);
        });
    }

    private CaptionEntry NewEntry(string speakerId, string text, long startMs, long endMs, int? latencyMs, bool isFinal)
    {
        return new CaptionEntry(
            Guid.NewGuid(),
            speakerId,
            speakerNames.DisplayName(speakerId),
            text,
            startMs,
            endMs,
            latencyMs,
            DateTimeOffset.UtcNow,
            isFinal);
    }

    private void TrimSpeakerEntries(string speakerId)
    {
        while (entries.Count(entry => string.Equals(entry.SpeakerId, speakerId, StringComparison.OrdinalIgnoreCase)) > maxEntries)
        {
            var index = entries.FindIndex(entry => string.Equals(entry.SpeakerId, speakerId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            entries.RemoveAt(index);
        }
    }

    private static string JoinCaptionText(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return incoming;
        }

        if (string.IsNullOrWhiteSpace(incoming))
        {
            return existing;
        }

        return $"{existing.TrimEnd()} {incoming.TrimStart()}";
    }
}
