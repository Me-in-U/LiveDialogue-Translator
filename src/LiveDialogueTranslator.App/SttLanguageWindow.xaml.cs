using System.Windows;
using System.Windows.Controls;
using LiveDialogueTranslator.App.Services;

namespace LiveDialogueTranslator.App;

public partial class SttLanguageWindow : Window
{
    private static readonly (string Code, string English, string Korean)[] Languages =
    [
        ("ko", "Korean", "한국어"),
        ("en", "English", "영어"),
        ("ja", "Japanese", "일본어"),
        ("zh", "Chinese", "중국어"),
        ("fr", "French", "프랑스어"),
        ("es", "Spanish", "스페인어"),
        ("de", "German", "독일어"),
        ("it", "Italian", "이탈리아어"),
        ("pt", "Portuguese", "포르투갈어"),
        ("ru", "Russian", "러시아어"),
        ("th", "Thai", "태국어"),
        ("vi", "Vietnamese", "베트남어")
    ];

    private readonly Localizer localizer;
    private readonly Dictionary<string, CheckBox> checkBoxes = new(StringComparer.OrdinalIgnoreCase);

    public SttLanguageWindow(IEnumerable<string> selectedLanguages, Localizer localizer)
    {
        InitializeComponent();
        this.localizer = localizer;
        ApplyLocalization();

        var selected = selectedLanguages
            .Select(NormalizeLanguage)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var language in Languages)
        {
            var checkBox = new CheckBox
            {
                Content = DisplayName(language.Code, localizer),
                IsChecked = selected.Contains(language.Code),
                Margin = new Thickness(0, 0, 0, 8),
                Tag = language.Code
            };
            LanguageRows.Children.Add(checkBox);
            checkBoxes[language.Code] = checkBox;
        }
    }

    public List<string> SelectedLanguages { get; private set; } = [];

    public static string Summary(IEnumerable<string> languages, Localizer localizer)
    {
        var normalized = languages
            .Select(NormalizeLanguage)
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count switch
        {
            0 => localizer.Text("SttLanguageAuto"),
            1 => DisplayName(normalized[0], localizer),
            _ => localizer.Format("SttLanguageMulti", normalized.Count)
        };
    }

    private void ApplyLocalization()
    {
        Title = localizer.Text("SttLanguagesTitle");
        HelpText.Text = localizer.Text("SttLanguageHelp");
        CancelButton.Content = localizer.Text("Cancel");
        SaveButton.Content = localizer.Text("Save");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SelectedLanguages = checkBoxes
            .Where(item => item.Value.IsChecked == true)
            .Select(item => item.Key)
            .ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string DisplayName(string code, Localizer localizer)
    {
        var language = Languages.FirstOrDefault(item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(language.Code))
        {
            return code;
        }

        var name = localizer.Language == LiveDialogueTranslator.Core.Localization.ResolvedAppLanguage.Korean
            ? language.Korean
            : language.English;
        return $"{name} ({language.Code})";
    }

    private static string NormalizeLanguage(string language)
    {
        return language.Trim().ToLowerInvariant();
    }
}
