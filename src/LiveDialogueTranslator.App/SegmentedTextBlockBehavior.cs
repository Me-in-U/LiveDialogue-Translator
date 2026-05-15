using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LiveDialogueTranslator.App.ViewModels;

namespace LiveDialogueTranslator.App;

public static class SegmentedTextBlockBehavior
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.RegisterAttached(
            "Segments",
            typeof(IEnumerable),
            typeof(SegmentedTextBlockBehavior),
            new PropertyMetadata(null, OnSegmentsChanged));

    public static readonly DependencyProperty MaxVisualLinesProperty =
        DependencyProperty.RegisterAttached(
            "MaxVisualLines",
            typeof(int),
            typeof(SegmentedTextBlockBehavior),
            new PropertyMetadata(0, OnMaxVisualLinesChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(SegmentBindingState),
            typeof(SegmentedTextBlockBehavior),
            new PropertyMetadata(null));

    public static IEnumerable? GetSegments(TextBlock element)
    {
        return (IEnumerable?)element.GetValue(SegmentsProperty);
    }

    public static void SetSegments(TextBlock element, IEnumerable? value)
    {
        element.SetValue(SegmentsProperty, value);
    }

    public static int GetMaxVisualLines(TextBlock element)
    {
        return (int)element.GetValue(MaxVisualLinesProperty);
    }

    public static void SetMaxVisualLines(TextBlock element, int value)
    {
        element.SetValue(MaxVisualLinesProperty, value);
    }

    private static SegmentBindingState? GetState(TextBlock element)
    {
        return (SegmentBindingState?)element.GetValue(StateProperty);
    }

    private static void SetState(TextBlock element, SegmentBindingState? value)
    {
        element.SetValue(StateProperty, value);
    }

    private static void OnSegmentsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TextBlock textBlock)
        {
            return;
        }

        GetState(textBlock)?.Detach();
        var state = new SegmentBindingState(textBlock, e.NewValue as IEnumerable);
        SetState(textBlock, state);
        state.Attach();
        state.Rebuild();
    }

    private static void OnMaxVisualLinesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is TextBlock textBlock)
        {
            GetState(textBlock)?.Rebuild();
        }
    }

    private sealed class SegmentBindingState
    {
        private const double LineHeightMultiplier = 1.35;

        private readonly TextBlock textBlock;
        private readonly IEnumerable? segments;
        private readonly List<INotifyPropertyChanged> observedSegments = [];
        private INotifyCollectionChanged? observedCollection;

        public SegmentBindingState(TextBlock textBlock, IEnumerable? segments)
        {
            this.textBlock = textBlock;
            this.segments = segments;
        }

        public void Attach()
        {
            observedCollection = segments as INotifyCollectionChanged;
            if (observedCollection != null)
            {
                observedCollection.CollectionChanged += Segments_CollectionChanged;
            }

            textBlock.Loaded += TextBlock_Loaded;
            textBlock.SizeChanged += TextBlock_SizeChanged;
            AttachSegmentSubscriptions();
        }

        public void Detach()
        {
            if (observedCollection != null)
            {
                observedCollection.CollectionChanged -= Segments_CollectionChanged;
                observedCollection = null;
            }

            textBlock.Loaded -= TextBlock_Loaded;
            textBlock.SizeChanged -= TextBlock_SizeChanged;
            DetachSegmentSubscriptions();
        }

        public void Rebuild()
        {
            textBlock.Inlines.Clear();
            if (segments == null)
            {
                return;
            }

            var displaySegments = TrimSegmentsToVisualLines(segments.OfType<OverlayCaptionTextSegmentViewModel>().ToList());
            foreach (var segment in displaySegments)
            {
                textBlock.Inlines.Add(new Run(segment.Text) { Foreground = segment.Brush });
            }
        }

        private void TextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            Rebuild();
        }

        private void TextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Rebuild();
        }

        private void Segments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            DetachSegmentSubscriptions();
            AttachSegmentSubscriptions();
            Rebuild();
        }

        private void Segment_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(OverlayCaptionTextSegmentViewModel.Text) ||
                e.PropertyName == nameof(OverlayCaptionTextSegmentViewModel.Brush))
            {
                Rebuild();
            }
        }

        private void AttachSegmentSubscriptions()
        {
            if (segments == null)
            {
                return;
            }

            foreach (var segment in segments.OfType<INotifyPropertyChanged>())
            {
                segment.PropertyChanged += Segment_PropertyChanged;
                observedSegments.Add(segment);
            }
        }

        private void DetachSegmentSubscriptions()
        {
            foreach (var segment in observedSegments)
            {
                segment.PropertyChanged -= Segment_PropertyChanged;
            }

            observedSegments.Clear();
        }

        private IReadOnlyList<OverlayCaptionTextSegmentViewModel> TrimSegmentsToVisualLines(
            IReadOnlyList<OverlayCaptionTextSegmentViewModel> sourceSegments)
        {
            var maxVisualLines = GetMaxVisualLines(textBlock);
            var availableWidth = textBlock.ActualWidth;
            if (sourceSegments.Count == 0 ||
                maxVisualLines <= 0 ||
                availableWidth <= 1 ||
                double.IsNaN(availableWidth) ||
                double.IsInfinity(availableWidth))
            {
                return sourceSegments;
            }

            var fullText = BuildText(sourceSegments);
            if (FitsWithinVisualLines(fullText, availableWidth, maxVisualLines))
            {
                return sourceSegments;
            }

            var totalCharacters = sourceSegments.Sum(segment => segment.Text.Length);
            var low = 1;
            var high = totalCharacters;
            var bestCharacterCount = 1;
            while (low <= high)
            {
                var candidateCharacterCount = low + ((high - low) / 2);
                var candidateSegments = TrimSegmentsToTail(sourceSegments, candidateCharacterCount);
                var candidateText = BuildText(candidateSegments);
                if (FitsWithinVisualLines(candidateText, availableWidth, maxVisualLines))
                {
                    bestCharacterCount = candidateCharacterCount;
                    low = candidateCharacterCount + 1;
                }
                else
                {
                    high = candidateCharacterCount - 1;
                }
            }

            return TrimSegmentsToTail(sourceSegments, bestCharacterCount);
        }

        private bool FitsWithinVisualLines(string text, double availableWidth, int maxVisualLines)
        {
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }

            var typeface = new Typeface(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch);
            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                textBlock.FlowDirection,
                typeface,
                textBlock.FontSize,
                textBlock.Foreground,
                VisualTreeHelper.GetDpi(textBlock).PixelsPerDip)
            {
                MaxTextWidth = availableWidth
            };
            var maxHeight = Math.Max(1, maxVisualLines) * textBlock.FontSize * LineHeightMultiplier;
            return formattedText.Height <= maxHeight + 0.5;
        }

        private static IReadOnlyList<OverlayCaptionTextSegmentViewModel> TrimSegmentsToTail(
            IReadOnlyList<OverlayCaptionTextSegmentViewModel> sourceSegments,
            int maxCharacters)
        {
            var selectedSegments = new List<OverlayCaptionTextSegmentViewModel>();
            var remainingCharacters = Math.Max(1, maxCharacters);
            for (var index = sourceSegments.Count - 1; index >= 0 && remainingCharacters > 0; index--)
            {
                var segment = sourceSegments[index];
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
            return selectedSegments
                .SkipWhile(segment => string.Equals(segment.Text, Environment.NewLine, StringComparison.Ordinal))
                .ToList();
        }

        private static string BuildText(IEnumerable<OverlayCaptionTextSegmentViewModel> sourceSegments)
        {
            return string.Concat(sourceSegments.Select(segment => segment.Text));
        }
    }
}
