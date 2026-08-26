namespace LiveDialogueTranslator.Core.Runtime;

public static class PythonPipCommands
{
    private const string SuppressScriptLocationWarning = "--no-warn-script-location";
    private const string CudaTorchIndexUrl = "https://download.pytorch.org/whl/cu128";
    private const string CudaTorchVersion = "2.11.0+cu128";
    private const string DiartVersion = "0.9.2";

    public static string UpgradePipArguments()
    {
        return $"-m pip install {SuppressScriptLocationWarning} --upgrade pip";
    }

    public static string InstallRequirementsArguments(string requirementsPath)
    {
        return $"-m pip install {SuppressScriptLocationWarning} -r \"{requirementsPath}\"";
    }

    public static string InstallRequirementsToTargetArguments(
        string requirementsPath,
        string targetDirectory,
        bool includeCudaTorchIndex = false)
    {
        var extraIndex = includeCudaTorchIndex
            ? $" --extra-index-url {CudaTorchIndexUrl}"
            : "";
        return $"-m pip install {SuppressScriptLocationWarning} --upgrade --target \"{targetDirectory}\"{extraIndex} -r \"{requirementsPath}\"";
    }

    public static string InstallCudaTorchArguments()
    {
        return $"-m pip install {SuppressScriptLocationWarning} --upgrade --index-url {CudaTorchIndexUrl} torch=={CudaTorchVersion} torchaudio=={CudaTorchVersion}";
    }

    public static string InstallDiartArguments()
    {
        // Diart 0.9.2 declares numpy<2 while pyannote.audio 4 pulls pyannote-core 6
        // which declares numpy>=2. Install Diart itself without letting pip downgrade
        // the shared pyannote runtime.
        return $"-m pip install {SuppressScriptLocationWarning} --upgrade --no-deps diart=={DiartVersion}";
    }

    public static string BootstrapPipArguments(string getPipPath)
    {
        return $"\"{getPipPath}\" {SuppressScriptLocationWarning}";
    }
}
