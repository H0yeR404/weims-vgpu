; SPDX-License-Identifier: GPL-2.0-or-later
#ifndef PublishDir
  #define PublishDir "artifacts\Weims-Manager-1.0"
#endif
[Setup]
AppId={{BB2BC22A-61BD-4C46-9013-0492F18F722D}
AppName=Weims Manager
AppVersion=1.0
AppVerName=Weims Manager 1.0
AppPublisher=Weims contributors
AppPublisherURL=https://github.com/H0yeR404/weims-vgpu
AppSupportURL=https://github.com/H0yeR404/weims-vgpu/issues
DefaultDirName={localappdata}\Programs\Weims Manager
DefaultGroupName=Weims Manager
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir=artifacts\release
OutputBaseFilename=Weims-Manager-1.0-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\Weims.Manager.exe
CloseApplications=yes
RestartApplications=no
LanguageDetectionMethod=none
UsePreviousLanguage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{group}\Weims Manager"; Filename: "{app}\Weims.Manager.exe"
Name: "{autodesktop}\Weims Manager"; Filename: "{app}\Weims.Manager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Weims.Manager.exe"; Description: "{cm:LaunchProgram,Weims Manager}"; Flags: nowait postinstall skipifsilent

; User preferences, downloaded runtimes and VM files are intentionally not owned by uninstall.
