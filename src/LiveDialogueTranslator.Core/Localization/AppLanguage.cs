using System.Globalization;

namespace LiveDialogueTranslator.Core.Localization;

public enum AppLanguage
{
    Auto,
    English,
    Korean
}

public enum ResolvedAppLanguage
{
    English,
    Korean
}

public static class AppLanguageResolver
{
    public static ResolvedAppLanguage Resolve(AppLanguage language)
    {
        return Resolve(language, CultureInfo.CurrentUICulture.Name);
    }

    public static ResolvedAppLanguage Resolve(AppLanguage language, string? cultureName)
    {
        return language switch
        {
            AppLanguage.Korean => ResolvedAppLanguage.Korean,
            AppLanguage.English => ResolvedAppLanguage.English,
            _ => IsKoreanCulture(cultureName) ? ResolvedAppLanguage.Korean : ResolvedAppLanguage.English
        };
    }

    private static bool IsKoreanCulture(string? cultureName)
    {
        return !string.IsNullOrWhiteSpace(cultureName) &&
               cultureName.StartsWith("ko", StringComparison.OrdinalIgnoreCase);
    }
}
