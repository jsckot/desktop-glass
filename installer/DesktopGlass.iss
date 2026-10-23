; Desktop Glass 0.3 - 用户级安装，文件恢复通过主程序无界面维护入口完成
#ifndef PackageDir
  #error PackageDir must point to the self-contained published folder
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
#ifdef SandboxData
AppId=DesktopGlass-Sandbox-03
CreateUninstallRegKey=no
#else
AppId=DesktopGlass
#endif
AppName=Desktop Glass
AppVersion=0.4.0
AppPublisher=Desktop Glass
DefaultDirName={code:DefaultLocation}
DisableDirPage=no
DefaultGroupName=Desktop Glass
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22621
OutputDir={#OutputDir}
#ifdef SandboxData
OutputBaseFilename=DesktopGlass-Setup-0.4.0-x64-sandbox
#else
OutputBaseFilename=DesktopGlass-Setup-0.4.0-x64
#endif
SetupIconFile=..\assets\desktop-glass.ico
UninstallDisplayIcon={app}\DesktopGlass.exe
#ifdef SandboxData
Compression=none
#else
Compression=lzma2
SolidCompression=yes
#endif
WizardStyle=modern
WizardSizePercent=110
CloseApplications=no
RestartApplications=no
SetupLogging=yes
DisableWelcomePage=no
DisableReadyPage=no
DirExistsWarning=no

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
#ifndef SandboxData
Name: "{userprograms}\Desktop Glass\Desktop Glass"; Filename: "{app}\DesktopGlass.exe"; WorkingDir: "{app}"
Name: "{userprograms}\Desktop Glass\卸载 Desktop Glass"; Filename: "{uninstallexe}"
#endif

[Run]
#ifndef SandboxData
Filename: "{app}\DesktopGlass.exe"; Description: "打开 Desktop Glass 管理面板"; Flags: nowait postinstall skipifsilent
#endif

[UninstallDelete]
Type: files; Name: "{app}\.maintenance"
Type: dirifempty; Name: "{app}"

[Code]
var
  Maintenance: THandle;
  Operation: THandle;
  PreviousRoot: String;
  HelperPath: String;

function CreateMutex(lpMutexAttributes: Integer; bInitialOwner: Boolean; lpName: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(hMutex: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function Quoted(Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function SandboxArguments: String;
begin
#ifdef SandboxData
  Result := ' --sandbox-data ' + Quoted('{#SandboxData}');
#else
  Result := '';
#endif
end;

function EnterOperation: Boolean;
var
  WaitResult: LongWord;
begin
  if Operation <> 0 then begin Result := True; exit; end;
  Operation := CreateMutex(0, False, 'Local\DesktopGlass-installer');
  if Operation = 0 then begin Result := False; exit; end;
  WaitResult := WaitForSingleObject(Operation, 0);
  Result := (WaitResult = 0) or (WaitResult = $80);
  if not Result then begin CloseHandle(Operation); Operation := 0; end;
end;

procedure ReleaseOperation;
begin
  if Operation <> 0 then begin
    ReleaseMutex(Operation); CloseHandle(Operation); Operation := 0;
  end;
end;

function PreviousArguments: String;
begin
  Result := '';
  if PreviousRoot <> '' then Result := ' --previous ' + Quoted(PreviousRoot);
end;

procedure ReleaseMaintenance;
begin
  if Maintenance <> 0 then begin
    ReleaseMutex(Maintenance);
    CloseHandle(Maintenance);
    Maintenance := 0;
  end;
end;

function RunBridge(Exe, Parameters: String): Boolean;
var
  Code: Integer;
  Report: String;
  ReportText: AnsiString;
begin
  Report := ExpandConstant('{tmp}\DesktopGlass-maintenance.json');
  Result := Exec(Exe, Parameters + SandboxArguments + ' --report ' + Quoted(Report),
    ExtractFilePath(Exe), SW_HIDE, ewWaitUntilTerminated, Code);
  Result := Result and (Code = 0);
  if (not Result) and LoadStringFromFile(Report, ReportText) then Log(String(ReportText));
end;

function DefaultLocation(Param: String): String;
begin
#ifdef SandboxData
  Result := '{#SandboxData}-install';
#else
  if not RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopGlass',
      'InstallLocation', Result) then
    Result := ExpandConstant('{localappdata}\Programs\DesktopGlass');
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  WaitResult: LongWord;
begin
  Result := '';
  if not EnterOperation then begin
    Result := '另一个安装或卸载操作正在进行，请稍后重试。'; exit;
  end;
#ifndef SandboxData
  if not RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopGlass_is1',
      'InstallLocation', PreviousRoot) then
    RegQueryStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopGlass',
      'InstallLocation', PreviousRoot);
#endif
  if HelperPath = '' then begin
    ExtractTemporaryFiles('{app}\*');
    HelperPath := ExpandConstant('{tmp}') + '\{app}\DesktopGlass.exe';
  end;
  if not RunBridge(HelperPath, '--setup-prepare --target ' + Quoted(WizardDirValue) + PreviousArguments) then begin
    Result := '安装前检查未通过。请确认所选目录可写、没有其他文件，并先完成 Desktop Glass 的项目恢复。详细原因见安装日志。';
    exit;
  end;
  Maintenance := CreateMutex(0, False, 'Local\DesktopGlass-maintenance');
  if Maintenance = 0 then begin Result := '无法取得安装维护锁，请稍后重试。'; exit; end;
  WaitResult := WaitForSingleObject(Maintenance, 0);
  if (WaitResult <> 0) and (WaitResult <> $80) then begin
    CloseHandle(Maintenance); Maintenance := 0;
    Result := '另一个安装、卸载或启动操作正在进行，请稍后重试。';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    ReleaseMaintenance;
    if not RunBridge(ExpandConstant('{app}\DesktopGlass.exe'), '--setup-complete' + PreviousArguments) then
      RaiseException('安装收尾未完成，请重新运行安装程序修复。');
  end;
end;

procedure DeinitializeSetup;
begin
  ReleaseMaintenance;
  ReleaseOperation;
end;

procedure DeinitializeUninstall;
begin
  ReleaseOperation;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then begin
    if not EnterOperation then
      RaiseException('另一个安装或卸载操作正在进行，已停止卸载。');
    if not RunBridge(ExpandConstant('{app}\DesktopGlass.exe'), '--setup-cleanup') then
      RaiseException('项目恢复或配置清理未完成，已停止卸载。请保留程序和恢复日志，完成恢复后重试。');
  end;
end;
