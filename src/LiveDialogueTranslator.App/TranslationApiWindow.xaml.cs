using System.Windows;
using LiveDialogueTranslator.App.Services;

namespace LiveDialogueTranslator.App;

public partial class TranslationApiWindow : Window
{
    private const string GoogleCloudTranslationApiUrl =
        "https://console.cloud.google.com/apis/library/translate.googleapis.com";
    private readonly Localizer localizer;

    public TranslationApiWindow(string? googleApiKey, Localizer localizer)
    {
        InitializeComponent();
        this.localizer = localizer;
        ApiKeyBox.Password = googleApiKey ?? "";
        ApplyLocalization();
    }

    public string? GoogleApiKey { get; private set; }

    private void ApplyLocalization()
    {
        Title = localizer.Text("TranslationApiTitle");
        TitleText.Text = localizer.Text("TranslationApiTitle");
        DescriptionText.Text = localizer.Text("TranslationApiDescription");
        ApiKeyLabel.Text = localizer.Text("GoogleApiKey");
        OpenGoogleCloudButton.Content = localizer.Text("OpenGoogleCloud");
        CancelButton.Content = localizer.Text("Cancel");
        SaveButton.Content = localizer.Text("Save");
    }

    private void OpenGoogleCloudButton_Click(object sender, RoutedEventArgs e)
    {
        ExternalLinkService.OpenUrl(GoogleCloudTranslationApiUrl);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        GoogleApiKey = string.IsNullOrWhiteSpace(ApiKeyBox.Password)
            ? null
            : ApiKeyBox.Password.Trim();
        DialogResult = true;
    }
}
