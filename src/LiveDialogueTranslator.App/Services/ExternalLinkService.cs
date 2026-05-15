using System.Diagnostics;

namespace LiveDialogueTranslator.App.Services;

public static class ExternalLinkService
{
    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}
