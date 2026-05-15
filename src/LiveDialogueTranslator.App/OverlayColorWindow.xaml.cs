using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveDialogueTranslator.App.Models;
using LiveDialogueTranslator.App.Services;

namespace LiveDialogueTranslator.App;

public partial class OverlayColorWindow : Window
{
    private readonly Localizer localizer;

    public OverlayColorWindow(OverlayWindowSettings settings, Localizer localizer)
    {
        InitializeComponent();
        this.localizer = localizer;
        Settings = CopySettings(settings);
        ApplyLocalization();
        LoadSettings(Settings);
        UpdateSwatches();
    }

    public OverlayWindowSettings Settings { get; private set; }

    private void ApplyLocalization()
    {
        Title = localizer.Text("OverlayColors");
        TitleText.Text = localizer.Text("OverlayColors");
        RoleHeaderText.Text = localizer.Text("OverlayColorRole");
        ValueHeaderText.Text = localizer.Text("OverlayColorValue");
        PreviewHeaderText.Text = localizer.Text("OverlayColorPreview");
        ActiveSpeakerLabel.Text = localizer.Text("ActiveSpeakerColor");
        InactiveSpeakerLabel.Text = localizer.Text("InactiveSpeakerColor");
        ActiveOriginalLabel.Text = localizer.Text("ActiveOriginalColor");
        ActiveTranslationLabel.Text = localizer.Text("ActiveTranslationColor");
        InactiveOriginalLabel.Text = localizer.Text("InactiveOriginalColor");
        InactiveTranslationLabel.Text = localizer.Text("InactiveTranslationColor");
        ResetDefaultsButton.Content = localizer.Text("ResetDefaults");
        CancelButton.Content = localizer.Text("Cancel");
        SaveButton.Content = localizer.Text("Save");
    }

    private void LoadSettings(OverlayWindowSettings settings)
    {
        ActiveSpeakerColorBox.Text = settings.ActiveSpeakerColor;
        InactiveSpeakerColorBox.Text = settings.InactiveSpeakerColor;
        ActiveOriginalColorBox.Text = settings.ActiveOriginalColor;
        ActiveTranslationColorBox.Text = settings.ActiveTranslationColor;
        InactiveOriginalColorBox.Text = settings.InactiveOriginalColor;
        InactiveTranslationColorBox.Text = settings.InactiveTranslationColor;
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSwatches();
    }

    private void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = OverlayWindowSettings.Default();
        ActiveSpeakerColorBox.Text = defaults.ActiveSpeakerColor;
        InactiveSpeakerColorBox.Text = defaults.InactiveSpeakerColor;
        ActiveOriginalColorBox.Text = defaults.ActiveOriginalColor;
        ActiveTranslationColorBox.Text = defaults.ActiveTranslationColor;
        InactiveOriginalColorBox.Text = defaults.InactiveOriginalColor;
        InactiveTranslationColorBox.Text = defaults.InactiveTranslationColor;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadColor(ActiveSpeakerColorBox, out var activeSpeakerColor) ||
            !TryReadColor(InactiveSpeakerColorBox, out var inactiveSpeakerColor) ||
            !TryReadColor(ActiveOriginalColorBox, out var activeOriginalColor) ||
            !TryReadColor(ActiveTranslationColorBox, out var activeTranslationColor) ||
            !TryReadColor(InactiveOriginalColorBox, out var inactiveOriginalColor) ||
            !TryReadColor(InactiveTranslationColorBox, out var inactiveTranslationColor))
        {
            MessageBox.Show(
                this,
                localizer.Text("InvalidColor"),
                localizer.Text("OverlayColors"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Settings.ActiveSpeakerColor = activeSpeakerColor;
        Settings.InactiveSpeakerColor = inactiveSpeakerColor;
        Settings.ActiveOriginalColor = activeOriginalColor;
        Settings.ActiveTranslationColor = activeTranslationColor;
        Settings.InactiveOriginalColor = inactiveOriginalColor;
        Settings.InactiveTranslationColor = inactiveTranslationColor;
        DialogResult = true;
    }

    private void UpdateSwatches()
    {
        UpdateSwatch(ActiveSpeakerColorBox, ActiveSpeakerSwatch);
        UpdateSwatch(InactiveSpeakerColorBox, InactiveSpeakerSwatch);
        UpdateSwatch(ActiveOriginalColorBox, ActiveOriginalSwatch);
        UpdateSwatch(ActiveTranslationColorBox, ActiveTranslationSwatch);
        UpdateSwatch(InactiveOriginalColorBox, InactiveOriginalSwatch);
        UpdateSwatch(InactiveTranslationColorBox, InactiveTranslationSwatch);
    }

    private static void UpdateSwatch(TextBox colorBox, Border swatch)
    {
        swatch.Background = TryReadColor(colorBox, out var color)
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
            : Brushes.Transparent;
    }

    private static bool TryReadColor(TextBox colorBox, out string color)
    {
        var text = colorBox.Text.Trim();
        if (!text.StartsWith('#') && (text.Length == 6 || text.Length == 8))
        {
            text = "#" + text;
        }

        try
        {
            color = ((Color)ColorConverter.ConvertFromString(text)).ToString();
            return true;
        }
        catch (FormatException)
        {
            color = "";
            return false;
        }
        catch (NotSupportedException)
        {
            color = "";
            return false;
        }
    }

    private static OverlayWindowSettings CopySettings(OverlayWindowSettings settings)
    {
        return new OverlayWindowSettings
        {
            Left = settings.Left,
            Top = settings.Top,
            Width = settings.Width,
            Height = settings.Height,
            FontSize = settings.FontSize,
            Opacity = settings.Opacity,
            ClickThrough = settings.ClickThrough,
            ShowBorder = settings.ShowBorder,
            AutoHeight = settings.AutoHeight,
            ActiveSpeakerColor = settings.ActiveSpeakerColor,
            InactiveSpeakerColor = settings.InactiveSpeakerColor,
            ActiveOriginalColor = settings.ActiveOriginalColor,
            ActiveTranslationColor = settings.ActiveTranslationColor,
            InactiveOriginalColor = settings.InactiveOriginalColor,
            InactiveTranslationColor = settings.InactiveTranslationColor
        };
    }
}
