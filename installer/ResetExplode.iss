; Instalador do RESET EXPLODE (PC de CS2). Gerado pelo CI (.github/workflows/compilar.yml) com:
;   ISCC.exe /DAppVersion=0.17.N /DDistDir=<pasta com o ResetExplode.exe> installer\ResetExplode.iss
; Instala só para o usuário atual (sem pedir administrador para instalar), numa pasta onde o app consegue se atualizar sozinho.

#ifndef AppVersion
  #define AppVersion "0.17.0"
#endif
#ifndef DistDir
  #define DistDir "..\dist"
#endif

[Setup]
AppId={{8F1C2B7E-5D3A-4C61-9B0E-2A7F4E6D1C35}
AppName=RESET EXPLODE (PC de CS2)
AppVersion={#AppVersion}
AppPublisher=Guilherme Cordeiro
AppPublisherURL=https://github.com/ghcordeiro/resetexplode
DefaultDirName={localappdata}\Programs\ResetExplode
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir={#DistDir}
OutputBaseFilename=ResetExplode-Setup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\ResetExplode.exe
UninstallDisplayName=RESET EXPLODE (PC de CS2)
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
CloseApplications=yes

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "{#DistDir}\ResetExplode.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\RESET EXPLODE"; Filename: "{app}\ResetExplode.exe"
Name: "{autodesktop}\RESET EXPLODE"; Filename: "{app}\ResetExplode.exe"; Tasks: desktopicon

[Run]
; shellexec: o app pede administrador (manifesto), então precisa abrir pelo shell para aparecer o pedido do Windows.
Filename: "{app}\ResetExplode.exe"; Description: "Abrir o RESET EXPLODE"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
Type: files; Name: "{app}\ResetExplode.exe.old"
Type: files; Name: "{app}\ResetExplode.new.exe"
