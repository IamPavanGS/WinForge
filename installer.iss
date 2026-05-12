[Setup]
AppName=ALE ISO Creator
AppVersion=1.0
AppPublisher=AL-Enterprise
DefaultDirName={autopf}\ALE ISO Creator
DefaultGroupName=ALE ISO Creator
OutputDir=C:\Users\pgs6718\Downloads\ALE ISO Creator\Installer
OutputBaseFilename=ALE-ISO-Creator-Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
DisableProgramGroupPage=yes
UninstallDisplayName=ALE ISO Creator

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "C:\Users\pgs6718\Downloads\ALE ISO Creator\Publish\GoldenISOBuilder.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\pgs6718\Downloads\ALE ISO Creator\Publish\GIBFirstBoot.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\pgs6718\Downloads\ALE ISO Creator\Publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ALE ISO Creator"; Filename: "{app}\GoldenISOBuilder.exe"
Name: "{commondesktop}\ALE ISO Creator"; Filename: "{app}\GoldenISOBuilder.exe"

[Run]
Filename: "{app}\GoldenISOBuilder.exe"; Description: "Launch ALE ISO Creator"; Flags: nowait postinstall skipifsilent
