namespace LiveDialogueTranslator.Core.Runtime;

public static class WorkerStderrClassifier
{
    private static readonly string[] BenignFragments =
    [
        "triton not found; flop counting will not work for triton kernels",
        "ReproducibilityWarning: TensorFloat-32",
        "It can be re-enabled by calling",
        ">>> import torch",
        "torch.backends.cuda.matmul.allow_tf32",
        "torch.backends.cudnn.allow_tf32",
        "pyannote-audio/issues/1370",
        "warnings.warn(",
        "UserWarning: std(): degrees of freedom is <= 0",
        "RuntimeWarning: Mean of empty slice",
        "RuntimeWarning: invalid value encountered in divide",
        "numpy\\_core\\fromnumeric.py",
        "numpy\\_core\\_methods.py",
        "pyannote\\audio\\models\\blocks\\pooling.py",
        "std = sequences.std",
        "return _methods._mean",
        "ret = um.true_divide",
        "Lightning automatically upgraded your loaded checkpoint",
        "Redirecting import of pytorch_lightning",
        "You have multiple `ModelCheckpoint` callback states",
        "Model has been trained with a task-dependent loss function",
        "Found keys that are not in the model state dict but in the checkpoint"
    ];

    public static bool ShouldIgnore(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        return BenignFragments.Any(fragment =>
            line.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}
