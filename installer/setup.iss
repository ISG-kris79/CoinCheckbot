; Inno Setup 설치 스크립트
; 빌드 방법:
;   1) publish.ps1 실행 -> publish\TradingCheckBot.exe 생성
;   2) Inno Setup(https://jrsoftware.org/isdl.php) 설치
;   3) 이 파일을 Inno Setup Compiler 로 열고 Build -> Compile
;   결과: installer\Output\CoinFF-TradingCheckBot-Setup.exe

#define MyAppName "코인선물 상하방 전략 체크봇"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "CoinFF"
#define MyAppExeName "TradingCheckBot.exe"

[Setup]
AppId={{A4F1C2E7-9B3D-4E6A-8C12-CF0A1B2D3E45}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CoinFF\TradingCheckBot
DefaultGroupName=CoinFF
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=CoinFF-TradingCheckBot-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면 바로가기 만들기"; GroupDescription: "추가 아이콘:"

[Files]
; publish 폴더의 단일 exe 를 포함한다.
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "지금 실행"; Flags: nowait postinstall skipifsilent
