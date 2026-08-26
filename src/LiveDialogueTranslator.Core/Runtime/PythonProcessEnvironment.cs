namespace LiveDialogueTranslator.Core.Runtime;

public static class PythonProcessEnvironment
{
    public static void Apply(IDictionary<string, string?> environment)
    {
        environment["PYTHONUTF8"] = "1";
        environment["PYTHONIOENCODING"] = "utf-8";
        environment["PYTHONNOUSERSITE"] = "1";
        environment["PIP_NO_COLOR"] = "1";
        environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
    }
}
