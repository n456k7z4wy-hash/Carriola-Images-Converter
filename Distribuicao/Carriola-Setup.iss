; Compile pelo Gerar-Instalador.cmd. As pastas e a versão são fornecidas por ele.
#if Ver < 0x06060000
  #error O compilador Inno Setup selecionado precisa ser 6.6 ou mais recente. Confira a versao e o caminho exibidos acima.
#endif

#ifdef CarriolaProbe
; Valida a instalação e o tema antes de publicar o aplicativo, sem gerar Setup.exe.
[Setup]
AppName=Verificacao do Carriola Images Converter
AppVersion=1.0.0
CreateAppDir=no
Uninstallable=no
Output=no
WizardStyle=modern dark

#else
#ifndef AppSourceDir
  #error Execute Gerar-Instalador.cmd para publicar o aplicativo antes de montar o instalador.
#endif
#ifndef VCRedistFile
  #error O redistribuível da Microsoft precisa ser obtido e validado pelo gerador.
#endif
#define AppName "Carriola Images Converter"
#define AppPublisher "Caio de Freitas"
#define AppExe "CarriolaConverter.exe"
#define VCRedistVersion GetFileVersion(VCRedistFile)

[Setup]
AppId={{342F3508-FA2B-48A1-9672-5EF68F465C26}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppFileVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador do Carriola Images Converter
DefaultDirName={autopf}\Carriola Images Converter
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#SetupOutputDir}
OutputBaseFilename=Carriola-Images-Converter-{#AppVersion}-x64-Setup
SetupIconFile={#AppSourceDir}\Assets\CarriolaConverter.ico
UninstallDisplayIcon={app}\Assets\CarriolaConverter.ico
UninstallDisplayName={#AppName}
WizardStyle=modern dark
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.pfx,*.p12,*.snk,*.key,*.appxsym"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VCRedistFile}"; DestDir: "{tmp}"; DestName: "vc_redist.x64.exe"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\CarriolaConverter.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\CarriolaConverter.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanLaunchApp

[Code]
var
  PrerequisiteNeedsRestart: Boolean;

function VCRuntimeReady: Boolean;
var
  Installed: Cardinal;
  VersionText: String;
  InstalledVersion, RequiredVersion: Int64;
begin
  Result := False;
  if not RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) then Exit;
  if Installed <> 1 then Exit;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', VersionText) then Exit;
  if (Length(VersionText) > 0) and ((VersionText[1] = 'v') or (VersionText[1] = 'V')) then Delete(VersionText, 1, 1);
  if not StrToVersion(VersionText, InstalledVersion) then Exit;
  if not StrToVersion('{#VCRedistVersion}', RequiredVersion) then Exit;
  Result := ComparePackedVersion(InstalledVersion, RequiredVersion) >= 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if VCRuntimeReady then Exit;
  WizardForm.StatusLabel.Caption := 'Preparando os componentes da Microsoft...';
  ExtractTemporaryFile('vc_redist.x64.exe');
  if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Não foi possível iniciar o componente Microsoft Visual C++. Tente executar o instalador novamente.';
    Exit;
  end;
  if ExitCode = 3010 then
    PrerequisiteNeedsRestart := True
  else if (ExitCode <> 0) and not ((ExitCode = 1638) and VCRuntimeReady) then
    Result := 'A instalação do Microsoft Visual C++ não foi concluída. Código: ' + IntToStr(ExitCode) + '. Reinicie o Windows e tente novamente.';
end;

function NeedRestart: Boolean;
begin
  Result := PrerequisiteNeedsRestart;
end;

function CanLaunchApp: Boolean;
begin
  Result := not PrerequisiteNeedsRestart;
end;

function InitializeSetup: Boolean;
var
  PreviousVersionText: String;
  PreviousVersion, NewVersion: Int64;
begin
  Result := True;
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{342F3508-FA2B-48A1-9672-5EF68F465C26}_is1', 'DisplayVersion', PreviousVersionText) then
    if StrToVersion(PreviousVersionText, PreviousVersion) and StrToVersion('{#AppVersion}', NewVersion) then
      if ComparePackedVersion(PreviousVersion, NewVersion) > 0 then
      begin
        MsgBox('Já existe uma versão mais recente do Carriola Images Converter instalada.', mbInformation, MB_OK);
        Result := False;
      end;
end;

// Os arquivos pessoais, imagens e preferências não são removidos pelo desinstalador.
// Não há importação de certificado nem alteração de políticas de segurança no Setup.exe.
#endif
