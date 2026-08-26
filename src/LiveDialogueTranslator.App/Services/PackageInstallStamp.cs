using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LiveDialogueTranslator.App.Services;

public static class PackageInstallStamp
{
    private const string StampFileName = ".live-dialogue-package.sha256";
    private const string LayoutVersion = "target-layout-v3";

    public static bool IsCurrent(string requirementsPath, string targetDirectory)
    {
        var stampPath = Path.Combine(targetDirectory, StampFileName);
        if (!Directory.Exists(targetDirectory) || !File.Exists(stampPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                File.ReadAllText(stampPath).Trim(),
                Compute(requirementsPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string CreateStagingDirectory(string targetDirectory)
    {
        var stagingDirectory = $"{targetDirectory}.installing-{Guid.NewGuid():N}";
        Directory.CreateDirectory(stagingDirectory);
        return stagingDirectory;
    }

    public static void MarkCurrent(string requirementsPath, string stagingDirectory)
    {
        File.WriteAllText(
            Path.Combine(stagingDirectory, StampFileName),
            Compute(requirementsPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static void CommitStagingDirectory(string stagingDirectory, string targetDirectory)
    {
        ValidateSiblingPath(stagingDirectory, targetDirectory, ".installing-");
        var backupDirectory = $"{targetDirectory}.backup-{Guid.NewGuid():N}";
        var targetMoved = false;
        try
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backupDirectory);
                targetMoved = true;
            }

            Directory.Move(stagingDirectory, targetDirectory);
            if (targetMoved)
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
        catch
        {
            if (!Directory.Exists(targetDirectory) && targetMoved && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, targetDirectory);
            }
            throw;
        }
    }

    public static void DeleteStagingDirectory(string stagingDirectory, string targetDirectory)
    {
        ValidateSiblingPath(stagingDirectory, targetDirectory, ".installing-");
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private static string Compute(string requirementsPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(LayoutVersion));
        hash.AppendData(File.ReadAllBytes(requirementsPath));

        var lockPath = Path.Combine(Path.GetDirectoryName(requirementsPath)!, "package-lock.json");
        if (File.Exists(lockPath))
        {
            hash.AppendData(File.ReadAllBytes(lockPath));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidateSiblingPath(string candidatePath, string targetDirectory, string marker)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var target = Path.GetFullPath(targetDirectory);
        var expectedPrefix = target + marker;
        if (!candidate.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(candidate), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsafe package staging path: {candidate}");
        }
    }
}
