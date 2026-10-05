#ifndef AppVersion
  #define AppVersion "0.6.4.2"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif

[Setup]
AppId={{FB4BE68E-6B62-4DAB-A7F7-532F3A09F145}
AppName=Steamy
AppVersion={#AppVersion}
AppPublisher=ZazaJr24
AppPublisherURL=https://github.com/ZazaJr24/Steamy
AppSupportURL=https://github.com/ZazaJr24/Steamy/issues
DefaultDirName={localappdata}\Programs\Steamy
DefaultGroupName=Steamy
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern dynamic includetitlebar
SetupIconFile=..\src\Steamy\Resources\Brand\steamy.ico
UninstallDisplayIcon={app}\Steamy.exe
LicenseFile=..\LICENSE
OutputBaseFilename=Steamy-v{#AppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Steamy"; Filename: "{app}\Steamy.exe"
Name: "{autodesktop}\Steamy"; Filename: "{app}\Steamy.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Steamy.exe"; Description: "Launch Steamy"; Flags: postinstall nowait skipifsilent
