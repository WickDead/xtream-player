; Inno Setup script for Xtream Player.
;
; Build the payload first:
;     pwsh packaging/build-release.ps1
; then compile this with Inno Setup 6:
;     iscc packaging/XtreamPlayer.iss
;
; Inno Setup rather than MSI or a framework with an update service: this is a
; single-user desktop app with no services, no COM registration and no drivers.
; A per-user install needs no elevation at all, which means no UAC prompt.

#define AppName        "Xtream Player"
#define AppVersion     "0.2.0"
#define AppPublisher   "XtreamPlayer"
#define AppExeName     "XtreamPlayer.exe"
#define SourceDir      "..\dist\XtreamPlayer"

[Setup]
AppId={{8E4C1F2A-9D3B-4A6E-B17C-5F0E2A8D6C41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user: no elevation, no UAC prompt, and the app only ever writes to the
; user's own AppData anyway.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no

OutputDir=..\dist
OutputBaseFilename=XtreamPlayer-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes

; The payload is self-contained .NET plus libvlc, so it is 64-bit only.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

WizardStyle=modern

; The setup wizard's own icon, and the one Add/Remove Programs shows. The
; uninstall entry points at the executable, which carries the icon already.
SetupIconFile=..\src\XtreamPlayer.App\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; Recursive: libvlc needs its plugins tree intact beside the executable, so the
; whole publish folder ships as-is.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only the poster cache and the catalog cache, both of which are rebuilt on
; demand. Never the database: it holds providers, favorites and watch history,
; and silently destroying that on uninstall is unforgivable.
Type: filesandordirs; Name: "{localappdata}\XtreamPlayer\poster-cache"
Type: filesandordirs; Name: "{localappdata}\XtreamPlayer\cache"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('Your providers, favorites and watch history have been kept in:'#13#10#13#10
         + ExpandConstant('{localappdata}\XtreamPlayer') + #13#10#13#10
         + 'Delete that folder by hand if you want them gone.',
           mbInformation, MB_OK);
end;
