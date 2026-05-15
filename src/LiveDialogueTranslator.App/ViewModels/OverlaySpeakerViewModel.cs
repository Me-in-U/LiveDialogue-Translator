using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LiveDialogueTranslator.App.Models;

namespace LiveDialogueTranslator.App.ViewModels;

public sealed class OverlayCaptionTextSegmentViewModel : INotifyPropertyChanged
{
    private Brush brush;

    public OverlayCaptionTextSegmentViewModel(string text, bool isChangedSegment, Brush brush)
    {
        Text = text;
        IsChangedSegment = isChangedSegment;
        this.brush = brush;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Text { get; }
    public bool IsChangedSegment { get; }

    public Brush Brush
    {
        get => brush;
        private set
        {
            if (Equals(brush, value))
            {
                return;
            }

            brush = value;
            OnPropertyChanged();
        }
    }

    public void ApplyBrush(Brush activeBrush, Brush inactiveBrush, bool isCurrentBatchLine)
    {
        Brush = isCurrentBatchLine && IsChangedSegment ? activeBrush : inactiveBrush;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class OverlayCaptionLineViewModel : INotifyPropertyChanged
{
    private string displayOriginalText;
    private string displayTranslatedText;
    private bool isCurrentBatchLine;
    private Brush activeOriginalBrush = BrushFrom("#FF8BD3FF");
    private Brush activeTranslationBrush = BrushFrom("#FFFFD166");
    private Brush inactiveOriginalBrush = BrushFrom("#FFFFFFFF");
    private Brush inactiveTranslationBrush = BrushFrom("#FFEFE7A0");

    public OverlayCaptionLineViewModel(CaptionEntryViewModel entry, bool markIncomingAsCurrentBatch = true)
    {
        Id = entry.Id;
        StartMs = entry.StartMs;
        EndMs = entry.EndMs;
        displayOriginalText = entry.DisplayOriginalText;
        displayTranslatedText = entry.DisplayTranslatedText;
        RebuildSegments("", displayOriginalText, "", displayTranslatedText, markIncomingAsCurrentBatch);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid Id { get; }
    public long StartMs { get; private set; }
    public long EndMs { get; private set; }
    public ObservableCollection<OverlayCaptionTextSegmentViewModel> OriginalSegments { get; } = [];
    public ObservableCollection<OverlayCaptionTextSegmentViewModel> TranslationSegments { get; } = [];
    public Brush OriginalBrush => IsCurrentBatchLine ? activeOriginalBrush : inactiveOriginalBrush;
    public Brush TranslationBrush => IsCurrentBatchLine ? activeTranslationBrush : inactiveTranslationBrush;

    public bool IsCurrentBatchLine
    {
        get => isCurrentBatchLine;
        private set
        {
            if (isCurrentBatchLine == value)
            {
                return;
            }

            isCurrentBatchLine = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OriginalBrush));
            OnPropertyChanged(nameof(TranslationBrush));
            ApplySegmentBrushes();
        }
    }

    public string DisplayOriginalText
    {
        get => displayOriginalText;
        private set
        {
            if (displayOriginalText == value)
            {
                return;
            }

            displayOriginalText = value;
            OnPropertyChanged();
        }
    }

    public string DisplayTranslatedText
    {
        get => displayTranslatedText;
        private set
        {
            if (displayTranslatedText == value)
            {
                return;
            }

            displayTranslatedText = value;
            OnPropertyChanged();
        }
    }

    public void Update(
        CaptionEntryViewModel entry,
        bool markIncomingAsCurrentBatch = true,
        bool preserveCurrentBatchSegments = false)
    {
        var previousOriginal = DisplayOriginalText;
        var previousTranslated = DisplayTranslatedText;
        StartMs = entry.StartMs;
        EndMs = entry.EndMs;
        DisplayOriginalText = entry.DisplayOriginalText;
        DisplayTranslatedText = entry.DisplayTranslatedText;
        RebuildSegments(
            previousOriginal,
            DisplayOriginalText,
            previousTranslated,
            DisplayTranslatedText,
            markIncomingAsCurrentBatch,
            preserveCurrentBatchSegments);
    }

    public void ApplyColors(OverlayWindowSettings settings)
    {
        ApplyColors(
            BrushFrom(settings.ActiveOriginalColor, "#FF8BD3FF"),
            BrushFrom(settings.ActiveTranslationColor, "#FFFFD166"),
            BrushFrom(settings.InactiveOriginalColor, "#FFFFFFFF"),
            BrushFrom(settings.InactiveTranslationColor, "#FFEFE7A0"));
    }

    public void ApplyColors(
        Brush activeOriginal,
        Brush activeTranslation,
        Brush inactiveOriginal,
        Brush inactiveTranslation)
    {
        activeOriginalBrush = activeOriginal;
        activeTranslationBrush = activeTranslation;
        inactiveOriginalBrush = inactiveOriginal;
        inactiveTranslationBrush = inactiveTranslation;
        OnPropertyChanged(nameof(OriginalBrush));
        OnPropertyChanged(nameof(TranslationBrush));
        ApplySegmentBrushes();
    }

    public void SetCurrentBatchLine(bool value)
    {
        IsCurrentBatchLine = value;
        ApplySegmentBrushes();
    }

    private void RebuildSegments(
        string previousOriginal,
        string currentOriginal,
        string previousTranslated,
        string currentTranslated,
        bool markIncomingAsCurrentBatch,
        bool preserveExistingSegments = false)
    {
        RebuildSegmentCollection(
            OriginalSegments,
            previousOriginal,
            currentOriginal,
            markIncomingAsCurrentBatch,
            preserveExistingSegments,
            activeOriginalBrush,
            inactiveOriginalBrush);
        RebuildSegmentCollection(
            TranslationSegments,
            previousTranslated,
            currentTranslated,
            markIncomingAsCurrentBatch,
            preserveExistingSegments,
            activeTranslationBrush,
            inactiveTranslationBrush);
    }

    private void RebuildSegmentCollection(
        ObservableCollection<OverlayCaptionTextSegmentViewModel> collection,
        string previousText,
        string currentText,
        bool markIncomingAsCurrentBatch,
        bool preserveExistingSegments,
        Brush activeBrush,
        Brush inactiveBrush)
    {
        if (preserveExistingSegments &&
            collection.Count > 0 &&
            string.Equals(previousText, currentText, StringComparison.Ordinal))
        {
            return;
        }

        collection.Clear();
        foreach (var (text, isChangedSegment) in SplitChangedText(previousText, currentText, markIncomingAsCurrentBatch))
        {
            collection.Add(new OverlayCaptionTextSegmentViewModel(
                text,
                isChangedSegment,
                IsCurrentBatchLine && isChangedSegment ? activeBrush : inactiveBrush));
        }
    }

    private void ApplySegmentBrushes()
    {
        foreach (var segment in OriginalSegments)
        {
            segment.ApplyBrush(activeOriginalBrush, inactiveOriginalBrush, IsCurrentBatchLine);
        }

        foreach (var segment in TranslationSegments)
        {
            segment.ApplyBrush(activeTranslationBrush, inactiveTranslationBrush, IsCurrentBatchLine);
        }
    }

    private static IEnumerable<(string Text, bool IsChangedSegment)> SplitChangedText(
        string previousText,
        string currentText,
        bool markIncomingAsCurrentBatch)
    {
        if (string.IsNullOrEmpty(currentText))
        {
            yield break;
        }

        if (!markIncomingAsCurrentBatch)
        {
            yield return (currentText, false);
            yield break;
        }

        if (string.IsNullOrEmpty(previousText))
        {
            yield return (currentText, true);
            yield break;
        }

        var commonPrefixLength = CommonPrefixLength(previousText, currentText);
        if (commonPrefixLength <= 0)
        {
            yield return (currentText, true);
            yield break;
        }

        if (commonPrefixLength >= currentText.Length)
        {
            yield return (currentText, false);
            yield break;
        }

        yield return (currentText[..commonPrefixLength], false);
        yield return (currentText[commonPrefixLength..], true);
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var maxLength = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < maxLength && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static Brush BrushFrom(string value, string fallback = "#FFFFFFFF")
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return BrushFrom(fallback, "#FFFFFFFF");
        }
        catch (NotSupportedException)
        {
            return BrushFrom(fallback, "#FFFFFFFF");
        }
    }
}

public sealed class OverlaySpeakerViewModel : INotifyPropertyChanged
{
    private const int DisplayCharactersPerLineBudget = 72;

    private string speakerName;
    private string displayText = "";
    private string originalDisplayText = "";
    private string translatedDisplayText = "";
    private bool isFading;
    private bool isCurrent;
    private int linesPerSpeaker;
    private Guid? currentBatchLineId;
    private Brush activeSpeakerBrush = BrushFrom("#FF7DD3FC");
    private Brush inactiveSpeakerBrush = BrushFrom("#CCFFFFFF");
    private Brush activeOriginalBrush = BrushFrom("#FF8BD3FF");
    private Brush activeTranslationBrush = BrushFrom("#FFFFD166");
    private Brush inactiveOriginalBrush = BrushFrom("#FFFFFFFF");
    private Brush inactiveTranslationBrush = BrushFrom("#FFEFE7A0");

    public OverlaySpeakerViewModel(
        CaptionEntryViewModel entry,
        int linesPerSpeaker,
        OverlayWindowSettings? settings = null)
    {
        SpeakerId = entry.SpeakerId;
        speakerName = entry.SpeakerName;
        if (settings != null)
        {
            ApplyColors(settings);
        }

        Apply(entry, linesPerSpeaker);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SpeakerId { get; }

    public string SpeakerName
    {
        get => speakerName;
        private set
        {
            if (speakerName == value)
            {
                return;
            }

            speakerName = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<OverlayCaptionLineViewModel> Lines { get; } = [];
    public ObservableCollection<OverlayCaptionTextSegmentViewModel> OriginalSegments { get; } = [];
    public ObservableCollection<OverlayCaptionTextSegmentViewModel> TranslationSegments { get; } = [];
    public DateTime LastUpdatedUtc { get; private set; }
    public DateTime? FadeStartedUtc { get; private set; }
    public long LatestLineEndMs => Lines.Count == 0 ? 0 : Lines.Max(line => line.EndMs);

    public string DisplayText
    {
        get => displayText;
        private set
        {
            if (displayText == value)
            {
                return;
            }

            displayText = value;
            OnPropertyChanged();
        }
    }

    public string OriginalDisplayText
    {
        get => originalDisplayText;
        private set
        {
            if (originalDisplayText == value)
            {
                return;
            }

            originalDisplayText = value;
            OnPropertyChanged();
        }
    }

    public string TranslatedDisplayText
    {
        get => translatedDisplayText;
        private set
        {
            if (translatedDisplayText == value)
            {
                return;
            }

            translatedDisplayText = value;
            OnPropertyChanged();
        }
    }

    public int LinesPerSpeaker
    {
        get => linesPerSpeaker;
        private set
        {
            if (linesPerSpeaker == value)
            {
                return;
            }

            linesPerSpeaker = value;
            OnPropertyChanged();
        }
    }

    public bool IsFading
    {
        get => isFading;
        private set
        {
            if (isFading == value)
            {
                return;
            }

            isFading = value;
            OnPropertyChanged();
        }
    }

    public bool IsCurrent
    {
        get => isCurrent;
        private set
        {
            if (isCurrent == value)
            {
                return;
            }

            isCurrent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakerBrush));
            OnPropertyChanged(nameof(OriginalBrush));
            OnPropertyChanged(nameof(TranslationBrush));
        }
    }

    public Brush SpeakerBrush => IsCurrent ? activeSpeakerBrush : inactiveSpeakerBrush;
    public Brush OriginalBrush => IsCurrent ? activeOriginalBrush : inactiveOriginalBrush;
    public Brush TranslationBrush => IsCurrent ? activeTranslationBrush : inactiveTranslationBrush;
    private int SegmentDisplayCharacterLimit => Math.Max(1, LinesPerSpeaker) * DisplayCharactersPerLineBudget;

    public void ApplyColors(OverlayWindowSettings settings)
    {
        activeSpeakerBrush = BrushFrom(settings.ActiveSpeakerColor, "#FF7DD3FC");
        inactiveSpeakerBrush = BrushFrom(settings.InactiveSpeakerColor, "#CCFFFFFF");
        activeOriginalBrush = BrushFrom(settings.ActiveOriginalColor, "#FF8BD3FF");
        activeTranslationBrush = BrushFrom(settings.ActiveTranslationColor, "#FFFFD166");
        inactiveOriginalBrush = BrushFrom(settings.InactiveOriginalColor, "#FFFFFFFF");
        inactiveTranslationBrush = BrushFrom(settings.InactiveTranslationColor, "#FFEFE7A0");
        foreach (var line in Lines)
        {
            line.ApplyColors(activeOriginalBrush, activeTranslationBrush, inactiveOriginalBrush, inactiveTranslationBrush);
        }

        OnPropertyChanged(nameof(SpeakerBrush));
        OnPropertyChanged(nameof(OriginalBrush));
        OnPropertyChanged(nameof(TranslationBrush));
    }

    public void Apply(CaptionEntryViewModel entry, int linesPerSpeaker, bool refreshActivity = true)
    {
        LinesPerSpeaker = Math.Max(1, linesPerSpeaker);
        SpeakerName = entry.SpeakerName;
        if (refreshActivity)
        {
            LastUpdatedUtc = DateTime.UtcNow;
            FadeStartedUtc = null;
            IsFading = false;
        }

        var existing = Lines.FirstOrDefault(line => line.Id == entry.Id);
        if (existing != null)
        {
            existing.Update(
                entry,
                markIncomingAsCurrentBatch: refreshActivity,
                preserveCurrentBatchSegments: !refreshActivity);
            existing.ApplyColors(activeOriginalBrush, activeTranslationBrush, inactiveOriginalBrush, inactiveTranslationBrush);
            RepositionLine(existing);
        }
        else
        {
            var line = new OverlayCaptionLineViewModel(entry, markIncomingAsCurrentBatch: refreshActivity);
            line.ApplyColors(activeOriginalBrush, activeTranslationBrush, inactiveOriginalBrush, inactiveTranslationBrush);
            InsertLineSorted(line);
        }

        while (Lines.Count > LinesPerSpeaker)
        {
            Lines.RemoveAt(0);
        }

        if (refreshActivity)
        {
            currentBatchLineId = entry.Id;
        }

        ApplyCurrentBatchLineState();
        OriginalDisplayText = BuildDisplayText(Lines.Select(line => line.DisplayOriginalText), LinesPerSpeaker);
        TranslatedDisplayText = BuildDisplayText(Lines.Select(line => line.DisplayTranslatedText), LinesPerSpeaker);
        DisplayText = BuildCombinedDisplayText(OriginalDisplayText, TranslatedDisplayText);
        RefreshDisplaySegments();
        OnPropertyChanged(nameof(LatestLineEndMs));
    }

    public void BeginFade(DateTime utcNow)
    {
        if (IsFading)
        {
            return;
        }

        FadeStartedUtc = utcNow;
        IsFading = true;
    }

    public void ClearLinesAfterFade()
    {
        Lines.Clear();
        OriginalSegments.Clear();
        TranslationSegments.Clear();
        OriginalDisplayText = string.Empty;
        TranslatedDisplayText = string.Empty;
        DisplayText = string.Empty;
        FadeStartedUtc = null;
        IsFading = false;
        OnPropertyChanged(nameof(LatestLineEndMs));
    }

    public void SetCurrent(bool value)
    {
        IsCurrent = value;
        ApplyCurrentBatchLineState();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void InsertLineSorted(OverlayCaptionLineViewModel line)
    {
        var index = 0;
        while (index < Lines.Count && CompareLines(Lines[index], line) <= 0)
        {
            index++;
        }

        Lines.Insert(index, line);
    }

    private void RepositionLine(OverlayCaptionLineViewModel line)
    {
        var oldIndex = Lines.IndexOf(line);
        if (oldIndex < 0)
        {
            return;
        }

        Lines.RemoveAt(oldIndex);
        InsertLineSorted(line);
    }

    private void ApplyCurrentBatchLineState()
    {
        foreach (var line in Lines)
        {
            line.SetCurrentBatchLine(IsCurrent && line.Id == currentBatchLineId);
        }
    }

    private void RefreshDisplaySegments()
    {
        OriginalSegments.Clear();
        TranslationSegments.Clear();
        AppendSegments(OriginalSegments, Lines.Select(line => line.OriginalSegments), SegmentDisplayCharacterLimit);
        AppendSegments(TranslationSegments, Lines.Select(line => line.TranslationSegments), SegmentDisplayCharacterLimit);
    }

    private void AppendSegments(
        ObservableCollection<OverlayCaptionTextSegmentViewModel> target,
        IEnumerable<ObservableCollection<OverlayCaptionTextSegmentViewModel>> lineSegments,
        int maxCharacters)
    {
        var collectedSegments = new List<OverlayCaptionTextSegmentViewModel>();
        var addedAnyLine = false;
        foreach (var segments in lineSegments.Where(segments => segments.Count > 0))
        {
            if (addedAnyLine)
            {
                collectedSegments.Add(new OverlayCaptionTextSegmentViewModel(Environment.NewLine, false, inactiveOriginalBrush));
            }

            foreach (var segment in segments)
            {
                collectedSegments.Add(segment);
            }

            addedAnyLine = true;
        }

        foreach (var segment in TrimSegmentsToTail(collectedSegments, maxCharacters))
        {
            target.Add(segment);
        }
    }

    private static IEnumerable<OverlayCaptionTextSegmentViewModel> TrimSegmentsToTail(
        IReadOnlyList<OverlayCaptionTextSegmentViewModel> segments,
        int maxCharacters)
    {
        if (segments.Count == 0 || maxCharacters <= 0)
        {
            yield break;
        }

        var selectedSegments = new List<OverlayCaptionTextSegmentViewModel>();
        var remainingCharacters = maxCharacters;
        for (var index = segments.Count - 1; index >= 0 && remainingCharacters > 0; index--)
        {
            var segment = segments[index];
            if (string.IsNullOrEmpty(segment.Text))
            {
                continue;
            }

            if (segment.Text.Length <= remainingCharacters)
            {
                selectedSegments.Add(segment);
                remainingCharacters -= segment.Text.Length;
                continue;
            }

            var text = segment.Text[^remainingCharacters..].TrimStart();
            if (!string.IsNullOrEmpty(text))
            {
                selectedSegments.Add(new OverlayCaptionTextSegmentViewModel(
                    text,
                    segment.IsChangedSegment,
                    segment.Brush));
            }

            remainingCharacters = 0;
        }

        selectedSegments.Reverse();

        var skipLeadingNewLines = true;
        foreach (var segment in selectedSegments)
        {
            if (skipLeadingNewLines && string.Equals(segment.Text, Environment.NewLine, StringComparison.Ordinal))
            {
                continue;
            }

            skipLeadingNewLines = false;
            yield return segment;
        }
    }

    private static int CompareLines(OverlayCaptionLineViewModel left, OverlayCaptionLineViewModel right)
    {
        var endCompare = left.EndMs.CompareTo(right.EndMs);
        if (endCompare != 0)
        {
            return endCompare;
        }

        return left.StartMs.CompareTo(right.StartMs);
    }

    private static string BuildDisplayText(IEnumerable<string> texts, int linesPerSpeaker)
    {
        var combinedText = string.Join(
            " ",
            texts.Where(text => !string.IsNullOrWhiteSpace(text)).TakeLast(Math.Max(1, linesPerSpeaker)));
        return TrimTextToTail(combinedText, Math.Max(1, linesPerSpeaker) * DisplayCharactersPerLineBudget);
    }

    private static string TrimTextToTail(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        return text[^maxCharacters..].TrimStart();
    }

    private static string BuildCombinedDisplayText(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return translated;
        }

        return string.IsNullOrWhiteSpace(translated)
            ? original
            : $"{original}{Environment.NewLine}{Environment.NewLine}{translated}";
    }

    private static Brush BrushFrom(string value, string fallback = "#FFFFFFFF")
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return BrushFrom(fallback, "#FFFFFFFF");
        }
        catch (NotSupportedException)
        {
            return BrushFrom(fallback, "#FFFFFFFF");
        }
    }
}
