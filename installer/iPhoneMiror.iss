; Inno Setup script for iPhoneMiror (per-user install, no admin rights required).
; Build: ISCC.exe /DArch=x64 /DSourceDir=<publish folder> installer\iPhoneMiror.iss

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6F1C2C1E-6A8B-4C1B-9E39-1F0F4B6C9D21}
AppName=iPhoneMiror
AppVersion={#AppVersion}
AppVerName=iPhoneMiror {#AppVersion}
AppPublisher=iPhoneMiror contributors
AppPublisherURL=https://github.com/aldiianshr-commits/iPhoneMiror
AppSupportURL=https://github.com/aldiianshr-commits/iPhoneMiror/issues
DefaultDirName={localappdata}\Programs\iPhoneMiror
DefaultGroupName=iPhoneMiror
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=iPhoneMiror-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\iPhoneMiror.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\iPhoneMiror.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\iPhoneMiror"; Filename: "{app}\iPhoneMiror.exe"
Name: "{group}\{cm:UninstallProgram,iPhoneMiror}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\iPhoneMiror"; Filename: "{app}\iPhoneMiror.exe"; Tasks: desktopicon

[Registry]
; iphonemiror:// protocol for the Sign in with Apple callback (per user).
Root: HKCU; Subkey: "Software\Classes\iphonemiror"; ValueType: string; ValueName: ""; ValueData: "URL:iPhoneMiror"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\iphonemiror"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\iphonemiror\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\iPhoneMiror.exe"",0"
Root: HKCU; Subkey: "Software\Classes\iphonemiror\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\iPhoneMiror.exe"" ""%1"""
; Remove the launch-at-sign-in entry on uninstall if the user enabled it in Settings.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "iPhoneMiror"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\iPhoneMiror.exe"; Description: "{cm:LaunchProgram,iPhoneMiror}"; Flags: nowait postinstall skipifsilent

[Code]
function IsAppleMobileDeviceServiceInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query "Apple Mobile Device Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not IsAppleMobileDeviceServiceInstalled()) then
    MsgBox('Apple Mobile Device Service was not found.' #13#10#13#10 +
      'iPhoneMiror needs it for USB and Wi-Fi connections. Install "Apple Devices" from the Microsoft Store (or iTunes for Windows), then restart iPhoneMiror.',
      mbInformation, MB_OK);
end;
