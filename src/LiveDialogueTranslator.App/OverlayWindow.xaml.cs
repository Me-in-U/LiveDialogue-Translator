using System.Collections;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LiveDialogueTranslator.App.Models;
using LiveDialogueTranslator.App.Services;
using LiveDialogueTranslator.App.ViewModels;

namespace LiveDialogueTranslator.App;

public partial class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private static readonly TimeSpan InactiveTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ActiveBatchWindow = TimeSpan.FromMilliseconds(750);
    private readonly ObservableCollection<OverlaySpeakerViewModel> speakers = [];
    private readonly Dictionary<string, long> clearedSpeakerEndMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeSpeakerIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer inactivityTimer;
    private bool clickThrough;
    private readonly Localizer localizer;
    private bool allowSettingsChanged;
    private bool applyingOverlaySettings;
    private bool applyingControlValues;
    private bool autoHeightPending;
    private DateTime lastActiveSpeakerUpdateUtc = DateTime.MinValue;
    private OverlayWindowSettings currentSettings = OverlayWindowSettings.Default();

    public event EventHandler<OverlayWindowSettings>? OverlaySettingsChanged;

    public OverlayWindow(Localizer localizer, OverlayWindowSettings settings)
    {
        InitializeComponent();
        this.localizer = localizer;
        OverlaySpeakers.ItemsSource = speakers;
        ApplyOverlaySettings(settings);
        SourceInitialized += (_, _) => ApplyClickThrough(currentSettings.ClickThrough);
        inactivityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        inactivityTimer.Tick += InactivityTimer_Tick;
        inactivityTimer.Start();
        ApplyLocalization();
        Loaded += (_, _) => allowSettingsChanged = true;
    }

    private void ApplyLocalization()
    {
        Title = localizer.Text("OverlayWindow");
        FontIncreaseButton.ToolTip = localizer.Text("FontIncrease");
        FontDecreaseButton.ToolTip = localizer.Text("FontDecrease");
        BorderToggleButton.ToolTip = localizer.Text("OverlayBorder");
        BorderToggleButton.Content = localizer.Text("OverlayBorder");
        OverlayOpacityControlSlider.ToolTip = localizer.Text("OverlayOpacity");
        OverlayResetButton.ToolTip = localizer.Text("OverlayResetTooltip");
        OverlayResetButton.Content = localizer.Text("OverlayReset");
        ClickThroughButton.ToolTip = localizer.Text("ClickThrough");
        ClickThroughButton.Content = localizer.Text("Lock");
    }

    public void UpdateEntries(IEnumerable entries)
    {
        UpdateEntries(entries.OfType<CaptionEntryViewModel>(), linesPerSpeaker: 3, allowCreateMissing: false);
    }

    public void UpdateEntries(IEnumerable<CaptionEntryViewModel> entries, int linesPerSpeaker)
    {
        UpdateEntries(entries, linesPerSpeaker, allowCreateMissing: false);
    }

    public void SeedEntries(IEnumerable<CaptionEntryViewModel> entries, int linesPerSpeaker)
    {
        UpdateEntries(entries, linesPerSpeaker, allowCreateMissing: true);
    }

    public void ClearSessionEntries()
    {
        speakers.Clear();
        clearedSpeakerEndMs.Clear();
        activeSpeakerIds.Clear();
        lastActiveSpeakerUpdateUtc = DateTime.MinValue;
        AdjustHeightToContent();
    }

    private void UpdateEntries(IEnumerable<CaptionEntryViewModel> entries, int linesPerSpeaker, bool allowCreateMissing)
    {
        foreach (var entry in entries.OrderBy(entry => entry.StartMs))
        {
            UpdateEntry(entry, linesPerSpeaker, refreshActivity: false, allowCreateMissing);
        }
    }

    public void UpdateEntry(CaptionEntryViewModel entry, int linesPerSpeaker)
    {
        UpdateEntry(entry, linesPerSpeaker, refreshActivity: true, allowCreateMissing: true);
    }

    private void UpdateEntry(CaptionEntryViewModel entry, int linesPerSpeaker, bool refreshActivity, bool allowCreateMissing)
    {
        linesPerSpeaker = Math.Max(1, linesPerSpeaker);
        if (IsBeforeClearedSpeakerBoundary(entry))
        {
            return;
        }

        var speaker = speakers.FirstOrDefault(candidate => candidate.SpeakerId == entry.SpeakerId);
        if (speaker?.IsFading == true && refreshActivity)
        {
            RecordClearedSpeakerBoundary(speaker);
            activeSpeakerIds.Remove(speaker.SpeakerId);
            speaker.ClearLinesAfterFade();
            speakers.Remove(speaker);
            speaker = null;
        }

        if (speaker == null && !allowCreateMissing)
        {
            return;
        }

        if (speaker == null)
        {
            speaker = new OverlaySpeakerViewModel(entry, linesPerSpeaker, currentSettings);
            InsertSpeakerSorted(speaker);
            if (refreshActivity)
            {
                MarkActiveSpeaker(speaker);
            }

            AdjustHeightToContent();
            return;
        }

        speaker.Apply(entry, linesPerSpeaker, refreshActivity);
        if (refreshActivity)
        {
            MarkActiveSpeaker(speaker);
        }

        AdjustHeightToContent();
    }

    public void ApplyOverlaySettings(OverlayWindowSettings settings)
    {
        applyingOverlaySettings = true;
        try
        {
            currentSettings = settings;
            Width = Math.Max(MinWidth, settings.Width);
            Height = Math.Max(MinHeight, settings.Height);
            if (settings.Left.HasValue)
            {
                Left = settings.Left.Value;
            }

            if (settings.Top.HasValue)
            {
                Top = settings.Top.Value;
            }

            OverlaySpeakers.FontSize = Math.Clamp(settings.FontSize, 11, 32);
            currentSettings.Opacity = NormalizeOpacity(settings.Opacity);
            currentSettings.ClickThrough = settings.ClickThrough;
            ApplyFrameVisuals();
            ApplyClickThrough(currentSettings.ClickThrough);
            ApplyOverlayColors();
            if (settings.AutoHeight)
            {
                AdjustHeightToContent();
            }
        }
        finally
        {
            applyingOverlaySettings = false;
        }
    }

    public OverlayWindowSettings CaptureOverlaySettings()
    {
        return new OverlayWindowSettings
        {
            Left = Left,
            Top = Top,
            Width = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width),
            Height = Math.Max(MinHeight, ActualHeight > 0 ? ActualHeight : Height),
            FontSize = OverlaySpeakers.FontSize,
            Opacity = NormalizeOpacity(currentSettings.Opacity),
            ClickThrough = currentSettings.ClickThrough,
            ShowBorder = currentSettings.ShowBorder,
            AutoHeight = currentSettings.AutoHeight,
            ActiveSpeakerColor = currentSettings.ActiveSpeakerColor,
            InactiveSpeakerColor = currentSettings.InactiveSpeakerColor,
            ActiveOriginalColor = currentSettings.ActiveOriginalColor,
            ActiveTranslationColor = currentSettings.ActiveTranslationColor,
            InactiveOriginalColor = currentSettings.InactiveOriginalColor,
            InactiveTranslationColor = currentSettings.InactiveTranslationColor
        };
    }

    private static double NormalizeOpacity(double opacity)
    {
        return double.IsNaN(opacity) ? 1.0 : Math.Clamp(opacity, 0.0, 1.0);
    }

    private bool OverlayTemplateReady =>
        OverlayFrame != null &&
        OverlaySpeakers != null &&
        BorderToggleButton != null &&
        ClickThroughButton != null &&
        OverlayOpacityControlSlider != null &&
        ControlPanel != null;

    private void ApplyFrameVisuals()
    {
        if (!OverlayTemplateReady)
        {
            return;
        }

        OverlayFrame.Background = CreateOverlayBackgroundBrush(currentSettings.Opacity, currentSettings.ClickThrough);
        OverlayFrame.BorderThickness = currentSettings.ShowBorder ? new Thickness(1) : new Thickness(0);
        BorderToggleButton.Opacity = currentSettings.ShowBorder ? 1.0 : 0.45;
        ClickThroughButton.Opacity = currentSettings.ClickThrough ? 1.0 : 0.45;
        SetOpacityControlValue(currentSettings.Opacity);
    }

    private static Brush CreateOverlayBackgroundBrush(double opacity, bool clickThrough)
    {
        var alpha = clickThrough
            ? (byte)0
            : (byte)Math.Max(1, (int)Math.Round(byte.MaxValue * NormalizeOpacity(opacity)));
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    private void SetOpacityControlValue(double opacity)
    {
        applyingControlValues = true;
        try
        {
            OverlayOpacityControlSlider.Value = NormalizeOpacity(opacity) * 100.0;
        }
        finally
        {
            applyingControlValues = false;
        }
    }

    private void ApplyOverlayColors()
    {
        foreach (var speaker in speakers)
        {
            speaker.ApplyColors(currentSettings);
        }
    }

    private bool IsBeforeClearedSpeakerBoundary(CaptionEntryViewModel entry)
    {
        return clearedSpeakerEndMs.TryGetValue(entry.SpeakerId, out var clearedEndMs) &&
               entry.EndMs <= clearedEndMs;
    }

    private void RecordClearedSpeakerBoundary(OverlaySpeakerViewModel speaker)
    {
        var latestEndMs = speaker.LatestLineEndMs;
        if (latestEndMs <= 0)
        {
            return;
        }

        if (!clearedSpeakerEndMs.TryGetValue(speaker.SpeakerId, out var currentEndMs) ||
            latestEndMs > currentEndMs)
        {
            clearedSpeakerEndMs[speaker.SpeakerId] = latestEndMs;
        }
    }

    private void InsertSpeakerSorted(OverlaySpeakerViewModel speaker)
    {
        var index = 0;
        while (index < speakers.Count && CompareSpeakers(speakers[index].SpeakerId, speaker.SpeakerId) <= 0)
        {
            index++;
        }

        speakers.Insert(index, speaker);
    }

    private void MarkActiveSpeaker(OverlaySpeakerViewModel activeSpeaker)
    {
        var now = DateTime.UtcNow;
        if (lastActiveSpeakerUpdateUtc == DateTime.MinValue ||
            now - lastActiveSpeakerUpdateUtc > ActiveBatchWindow)
        {
            activeSpeakerIds.Clear();
        }

        lastActiveSpeakerUpdateUtc = now;
        activeSpeakerIds.Add(activeSpeaker.SpeakerId);
        ApplyActiveSpeakers();
    }

    private void ApplyActiveSpeakers()
    {
        foreach (var speaker in speakers)
        {
            speaker.SetCurrent(activeSpeakerIds.Contains(speaker.SpeakerId));
        }
    }

    private void InactivityTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var speaker in speakers.ToArray())
        {
            if (!speaker.IsFading && now - speaker.LastUpdatedUtc > InactiveTimeout)
            {
                activeSpeakerIds.Remove(speaker.SpeakerId);
                speaker.SetCurrent(false);
                speaker.BeginFade(now);
            }
            else if (speaker.IsFading && speaker.FadeStartedUtc.HasValue && now - speaker.FadeStartedUtc.Value > FadeDuration)
            {
                RecordClearedSpeakerBoundary(speaker);
                activeSpeakerIds.Remove(speaker.SpeakerId);
                speaker.ClearLinesAfterFade();
                speakers.Remove(speaker);
                removed = true;
            }
        }

        if (removed)
        {
            AdjustHeightToContent();
        }
    }

    private static int CompareSpeakers(string left, string right)
    {
        var leftKey = SpeakerSortKey(left);
        var rightKey = SpeakerSortKey(right);
        var keyCompare = leftKey.CompareTo(rightKey);
        return keyCompare != 0 ? keyCompare : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int SpeakerSortKey(string speakerId)
    {
        if (speakerId.Equals("mic", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        const string prefix = "speaker_";
        if (speakerId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(speakerId[prefix.Length..], out var number))
        {
            return number;
        }

        return int.MaxValue;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !clickThrough)
        {
            DragMove();
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!clickThrough)
        {
            ControlPanel.Visibility = Visibility.Visible;
        }
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        ControlPanel.Visibility = Visibility.Collapsed;
    }

    private void FontIncrease_Click(object sender, RoutedEventArgs e)
    {
        OverlaySpeakers.FontSize = Math.Min(32, OverlaySpeakers.FontSize + 1);
        AdjustHeightToContent();
        NotifyOverlaySettingsChanged();
    }

    private void FontDecrease_Click(object sender, RoutedEventArgs e)
    {
        OverlaySpeakers.FontSize = Math.Max(11, OverlaySpeakers.FontSize - 1);
        AdjustHeightToContent();
        NotifyOverlaySettingsChanged();
    }

    private void BorderToggle_Click(object sender, RoutedEventArgs e)
    {
        currentSettings.ShowBorder = !currentSettings.ShowBorder;
        ApplyFrameVisuals();
        NotifyOverlaySettingsChanged();
    }

    private void OverlayOpacityControlSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!OverlayTemplateReady)
        {
            return;
        }

        if (applyingOverlaySettings || applyingControlValues)
        {
            return;
        }

        currentSettings.Opacity = NormalizeOpacity(OverlayOpacityControlSlider.Value / 100.0);
        ApplyFrameVisuals();
        NotifyOverlaySettingsChanged();
    }

    private void OverlayResetButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = OverlayWindowSettings.Default();
        defaults.Left = Left;
        defaults.Top = Top;
        ApplyOverlaySettings(defaults);
        if (allowSettingsChanged)
        {
            OverlaySettingsChanged?.Invoke(this, defaults);
        }
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e)
    {
        currentSettings.ClickThrough = !currentSettings.ClickThrough;
        ApplyClickThrough(currentSettings.ClickThrough);
        NotifyOverlaySettingsChanged();
    }

    private void ApplyClickThrough(bool enabled)
    {
        currentSettings.ClickThrough = enabled;
        clickThrough = enabled;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            ApplyFrameVisuals();
            return;
        }

        var style = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, enabled
            ? style | WsExTransparent | WsExLayered
            : style & ~WsExTransparent);
        ApplyFrameVisuals();
        ControlPanel.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        inactivityTimer.Stop();
        base.OnClosed(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        NotifyOverlaySettingsChanged();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        NotifyOverlaySettingsChanged();
    }

    private void NotifyOverlaySettingsChanged()
    {
        if (!allowSettingsChanged || applyingOverlaySettings)
        {
            return;
        }

        OverlaySettingsChanged?.Invoke(this, CaptureOverlaySettings());
    }

    private void AdjustHeightToContent()
    {
        if (!currentSettings.AutoHeight || autoHeightPending)
        {
            return;
        }

        autoHeightPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            autoHeightPending = false;
            if (!currentSettings.AutoHeight)
            {
                return;
            }

            var width = Math.Max(MinWidth, ActualWidth > 0 ? ActualWidth : Width);
            OverlayFrame.Measure(new Size(width, double.PositiveInfinity));
            var desiredHeight = OverlayFrame.DesiredSize.Height + OverlayFrame.Margin.Top + OverlayFrame.Margin.Bottom;
            var nextHeight = Math.Clamp(Math.Ceiling(desiredHeight), MinHeight, SystemParameters.WorkArea.Height);
            var currentHeight = ActualHeight > 0 ? ActualHeight : Height;
            if (Math.Abs(nextHeight - currentHeight) < 1)
            {
                return;
            }

            var bottom = Top + currentHeight;
            if (double.IsNaN(bottom))
            {
                Height = nextHeight;
                return;
            }

            Height = nextHeight;
            Top = bottom - nextHeight;
        }), DispatcherPriority.Background);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
