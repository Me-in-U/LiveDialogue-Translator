using LiveDialogueTranslator.Core.Protocol;

namespace LiveDialogueTranslator.Core.Transcripts;

public sealed class SpeakerSegmentTimeline
{
    private const int MaxSegments = 240;
    private const long NearbyToleranceMs = 2500;
    private readonly List<SpeakerSegmentEvent> segments = [];

    public void Add(SpeakerSegmentEvent segment)
    {
        if (segment.EndMs <= segment.StartMs || string.IsNullOrWhiteSpace(segment.SpeakerId))
        {
            return;
        }

        segments.Add(segment);
        if (segments.Count > MaxSegments)
        {
            segments.RemoveRange(0, segments.Count - MaxSegments);
        }
    }

    public void Clear()
    {
        segments.Clear();
    }

    public string ResolveSpeaker(long startMs, long endMs, string fallbackSpeakerId)
    {
        if (endMs < startMs)
        {
            (startMs, endMs) = (endMs, startMs);
        }

        var overlapBySpeaker = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments)
        {
            var overlap = Math.Min(endMs, segment.EndMs) - Math.Max(startMs, segment.StartMs);
            if (overlap <= 0)
            {
                continue;
            }

            overlapBySpeaker[segment.SpeakerId] = overlapBySpeaker.GetValueOrDefault(segment.SpeakerId) + overlap;
        }

        if (overlapBySpeaker.Count > 0)
        {
            return overlapBySpeaker
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .First()
                .Key;
        }

        var nearby = segments
            .Where(segment => segment.StartMs <= endMs + NearbyToleranceMs &&
                              segment.EndMs >= startMs - NearbyToleranceMs)
            .OrderByDescending(segment => segment.EndMs)
            .FirstOrDefault();

        return nearby?.SpeakerId ?? fallbackSpeakerId;
    }
}
