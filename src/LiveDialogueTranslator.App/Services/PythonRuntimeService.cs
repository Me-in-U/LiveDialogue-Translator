using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using LiveDialogueTranslator.Core.Runtime;

namespace LiveDialogueTranslator.App.Services;

public sealed class PythonRuntimeService
{
    private readonly AppPaths paths;
    private readonly Localizer localizer;

    public PythonRuntimeService(AppPaths paths, Localizer localizer)
    {
        this.paths = paths;
        this.localizer = localizer;
    }

    public async Task<string> EnsureAsync(
        CancellationToken token = default,
        Action<string, string, double?>? report = null)
    {
        if (File.Exists(paths.PythonExecutablePath) && await HasPipAsync(token))
        {
            return paths.PythonExecutablePath;
        }

        Directory.CreateDirectory(paths.RuntimeDirectory);
        report?.Invoke(L("PreparingPythonRuntime"), L("CreatingAppPythonEnvironment"), 0.08);

        if (!File.Exists(paths.PythonExecutablePath))
        {
            var archivePath = await DownloadRuntimeArchiveAsync(token, report);
            ExtractRuntimeArchive(archivePath, report);
        }

        if (!File.Exists(paths.PythonExecutablePath))
        {
            throw new InvalidOperationException(L("PythonRuntimeCreateFailed"));
        }

        if (!await HasPipAsync(token))
        {
            await InstallPipAsync(token, report);
        }

        report?.Invoke(L("PreparingPythonRuntime"), L("UpgradingPip"), 0.12);
        var pipResult = await RunAsync(
            paths.PythonExecutablePath,
            PythonPipCommands.UpgradePipArguments(),
            token);
        if (pipResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"{L("PipUpgradeFailed")}{Environment.NewLine}{pipResult.StdErr}{Environment.NewLine}{pipResult.StdOut}");
        }

        return paths.PythonExecutablePath;
    }

    private async Task<string> DownloadRuntimeArchiveAsync(
        CancellationToken token,
        Action<string, string, double?>? report)
    {
        var archivePath = PythonRuntimeLayout.RuntimeArchivePath(paths.RuntimeDirectory);
        if (File.Exists(archivePath))
        {
            return archivePath;
        }

        try
        {
            await DownloadFileAsync(
                PythonRuntimeLayout.DownloadUrl,
                archivePath,
                L("DownloadingPythonRuntime"),
                localizer.Format("DownloadingPythonRuntimeDetail", PythonRuntimeLayout.Version),
                0.09,
                0.17,
                report,
                token);
            return archivePath;
        }
        catch (Exception ex)
        {
            File.Delete(archivePath);
            throw new InvalidOperationException($"{L("PythonRuntimeDownloadFailed")}{Environment.NewLine}{ex.Message}", ex);
        }
    }

    private void ExtractRuntimeArchive(
        string archivePath,
        Action<string, string, double?>? report)
    {
        report?.Invoke(
            L("ExtractingPythonRuntime"),
            localizer.Format("InstallingPythonRuntimeDetail", paths.PythonDirectory),
            0.18);

        DeleteIncompletePythonDirectory();
        Directory.CreateDirectory(paths.PythonDirectory);
        ZipFile.ExtractToDirectory(archivePath, paths.PythonDirectory, overwriteFiles: true);
        EnableSitePackages();
    }

    private async Task InstallPipAsync(
        CancellationToken token,
        Action<string, string, double?>? report)
    {
        var getPipPath = PythonRuntimeLayout.GetPipPath(paths.RuntimeDirectory);
        try
        {
            if (!File.Exists(getPipPath))
            {
                await DownloadFileAsync(
                    PythonRuntimeLayout.GetPipUrl,
                    getPipPath,
                    L("InstallingPip"),
                    L("DownloadingPipBootstrap"),
                    0.2,
                    0.24,
                    report,
                    token);
            }

            report?.Invoke(L("InstallingPip"), L("InstallingPipBootstrap"), 0.25);
            var result = await RunAsync(
                paths.PythonExecutablePath,
                PythonPipCommands.BootstrapPipArguments(getPipPath),
                token);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"{result.StdErr}{Environment.NewLine}{result.StdOut}");
            }
        }
        catch (Exception ex)
        {
            File.Delete(getPipPath);
            throw new InvalidOperationException($"{L("PipBootstrapFailed")}{Environment.NewLine}{ex.Message}", ex);
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string title,
        string detail,
        double startPercent,
        double endPercent,
        Action<string, string, double?>? report,
        CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        report?.Invoke(title, detail, startPercent);

        using var http = new HttpClient();
        using var response = await http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = File.Create(destinationPath);
        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), token);
            downloaded += read;
            if (total is > 0)
            {
                var filePercent = downloaded / (double)total.Value;
                var percent = startPercent + filePercent * (endPercent - startPercent);
                report?.Invoke(title, $"{Math.Round(filePercent * 100)}%", percent);
            }
        }
    }

    private void EnableSitePackages()
    {
        var pthPath = Path.Combine(paths.PythonDirectory, "python311._pth");
        if (!File.Exists(pthPath))
        {
            return;
        }

        var content = File.ReadAllText(pthPath);
        if (content.Contains("#import site", StringComparison.Ordinal))
        {
            content = content.Replace("#import site", "import site", StringComparison.Ordinal);
            File.WriteAllText(pthPath, content, Encoding.ASCII);
        }
    }

    private async Task<bool> HasPipAsync(CancellationToken token)
    {
        try
        {
            var result = await RunAsync(paths.PythonExecutablePath, "-m pip --version", token);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void DeleteIncompletePythonDirectory()
    {
        if (!Directory.Exists(paths.PythonDirectory))
        {
            return;
        }

        var runtimeRoot = Path.GetFullPath(paths.RuntimeDirectory);
        var pythonDirectory = Path.GetFullPath(paths.PythonDirectory);
        if (!pythonDirectory.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clean Python directory outside runtime root: {pythonDirectory}");
        }

        Directory.Delete(pythonDirectory, recursive: true);
    }

    private static async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        PythonProcessEnvironment.Apply(psi.Environment);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private string L(string key)
    {
        return localizer.Text(key);
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
