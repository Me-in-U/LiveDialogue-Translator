param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $repo ".dotnet-sdk\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$publishDir = Join-Path $repo "publish\$Runtime"
$resolvedRepo = [System.IO.Path]::GetFullPath($repo)
$resolvedPublishDir = [System.IO.Path]::GetFullPath($publishDir)
if (-not $resolvedPublishDir.StartsWith($resolvedRepo, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean publish directory outside repository: $resolvedPublishDir"
}
if (Test-Path $resolvedPublishDir) {
    Remove-Item -LiteralPath $resolvedPublishDir -Recurse -Force
}
& $dotnet publish (Join-Path $repo "src\LiveDialogueTranslator.App\LiveDialogueTranslator.App.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --output $publishDir `
    --self-contained false

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $knownIsccPaths = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $knownIsccPaths) {
        if (Test-Path $candidate) {
            $iscc = Get-Item $candidate
            break
        }
    }
}
if (-not $iscc) {
    Write-Host "Published app to $publishDir"
    Write-Host "Install Inno Setup and run ISCC.exe installer\LiveDialogueTranslator.iss to build LiveDialogueTranslatorSetup-x64.exe."
    exit 0
}

& $iscc.FullName (Join-Path $repo "installer\LiveDialogueTranslator.iss")
