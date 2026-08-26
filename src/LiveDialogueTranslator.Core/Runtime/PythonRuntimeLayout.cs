namespace LiveDialogueTranslator.Core.Runtime;

public static class PythonRuntimeLayout
{
    public const string Version = "3.11.9";
    public const string RuntimeArchiveFileName = "python-3.11.9-embed-amd64.zip";
    public const string GetPipFileName = "get-pip.py";
    public const string DownloadUrl = "https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip";
    public const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";

    public static string PythonDirectory(string runtimeRoot)
    {
        return Path.Combine(runtimeRoot, $"python-{Version}");
    }

    public static string PythonExecutablePath(string runtimeRoot)
    {
        return Path.Combine(PythonDirectory(runtimeRoot), "python.exe");
    }

    public static string RuntimeArchivePath(string runtimeRoot)
    {
        return Path.Combine(runtimeRoot, "downloads", RuntimeArchiveFileName);
    }

    public static string GetPipPath(string runtimeRoot)
    {
        return Path.Combine(runtimeRoot, "downloads", GetPipFileName);
    }
}
