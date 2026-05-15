#define MyAppName "LiveDialogue Translator"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "LiveDialogue Translator"
#define MyAppExeName "LiveDialogueTranslator.exe"

[Setup]
AppId={{92C6C770-B50A-4CB1-A390-15E7D35999B2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
LicenseFile=..\LICENSE
DefaultDirName={autopf}\LiveDialogue Translator
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=LiveDialogueTranslatorSetup-x64
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\LiveDialogueTranslator.App\Assets\LiveDialogueTranslator.ico

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\worker\requirements-nemo-sortformer.txt"
Type: files; Name: "{app}\worker\env\nemo-sortformer.env"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\LiveDialogueTranslator.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\LiveDialogueTranslator.ico"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로 가기 만들기"; GroupDescription: "추가 아이콘:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} 실행"; Flags: nowait postinstall skipifsilent
