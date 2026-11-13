; SPDX-License-Identifier: GPL-2.0-or-later
#ifndef PublishDir
  #define PublishDir "artifacts\Weims-Manager-1.1.0-pre.1"
#endif
[Setup]
AppId={{E8E74513-80F4-453E-8183-5CD70BC29070}
AppName=Weims Manager Preview
AppVersion=1.1.0-pre.1
AppVerName=Weims Manager 1.1.0-pre.1
AppPublisher=Weims contributors
AppPublisherURL=https://github.com/H0yeR404/weims-vgpu
AppSupportURL=https://github.com/H0yeR404/weims-vgpu/issues
DefaultDirName={localappdata}\Programs\Weims Manager Preview
DefaultGroupName=Weims Manager Preview
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputDir=artifacts\release
OutputBaseFilename=Weims-Manager-1.1.0-pre.1-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Weims.Manager\Assets\weims.ico
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
Name: "{group}\Weims Manager Preview"; Filename: "{app}\Weims.Manager.exe"; IconFilename: "{app}\Weims.Manager.exe"
Name: "{autodesktop}\Weims Manager Preview"; Filename: "{app}\Weims.Manager.exe"; IconFilename: "{app}\Weims.Manager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Weims.Manager.exe"; Description: "{cm:LaunchProgram,Weims Manager}"; Flags: nowait postinstall skipifsilent

; User preferences, downloaded runtimes and VM files are intentionally not owned by uninstall.
