; Compile with the version and release paths provided by build.ps1.
[Setup]
AppId={code:ApplicationId}
AppName=磁盘空间管理器
AppPublisher=peterwangze
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName=Windows Disk Manager
VersionInfoDescription=磁盘空间管理器安装程序
DefaultDirName={code:DefaultInstallDirectory}
AppendDefaultDirName=no
UsePreviousAppDir=not HasLegacyVersionDirectory
DisableDirPage=no
UsePreviousGroup=no
UsePreviousLanguage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupMutex=Global\WindowsDiskManager.Setup-{username}
CloseApplications=no
RestartApplications=no
SetupIconFile=..\src\app.ico
UninstallDisplayIcon={app}\WindowsDiskManager.exe
UninstallFilesDir={app}\uninstall
CreateUninstallRegKey=not Isolated
OutputDir={#DistDir}
OutputBaseFilename=WindowsDiskManager-{#AppVersion}-win-x64-Setup
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SetupLogging=yes
ShowLanguageDialog=no

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "{#ProductDir}\WindowsDiskManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProductDir}\WindowsDiskManager.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProductDir}\DiskScanner.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProductDir}\scanner-runtime\*"; DestDir: "{app}\scanner-runtime"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ProductDir}\licenses\Python.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "{#ProductDir}\licenses\PyInstaller.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "ChineseSimplified-LICENSE.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{code:MenuDirectory}\磁盘空间管理器"; Filename: "{app}\WindowsDiskManager.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\WindowsDiskManager.exe"; WorkingDir: "{app}"; Description: "启动磁盘空间管理器"; Flags: postinstall nowait skipifsilent; Check: InteractiveLaunch
Filename: "{app}\WindowsDiskManager.exe"; WorkingDir: "{app}"; Flags: nowait; Check: UpdateLaunch

[Code]
const
  SynchronizeAccess = $00100000;
  InvalidParameter = 87;
  WaitTimeout = 258;

var
  PreviousProcess: THandle;

function OpenProcess(Access: Cardinal; Inherit: Boolean; Id: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function CreateFileForRead(Name: String; Access, Share: Cardinal; Security: Integer;
  Disposition, Flags: Cardinal; TemplateFile: THandle): THandle;
  external 'CreateFileW@kernel32.dll stdcall';

function HasOption(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then exit;
  Result := False;
end;

function Isolated: Boolean;
begin
  Result := HasOption('/ISOLATED');
end;

function ApplicationId(Param: String): String;
begin
  Result := 'WindowsDiskManager';
  if Isolated then Result := Result + '.Isolated';
end;

function LegacyVersionDirectory: String;
var
  Key, Path, VersionText: String;
  Version: Int64;
begin
  Result := '';
  if Isolated then exit;
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + ApplicationId('') + '_is1';
  if not RegQueryStringValue(HKCU, Key, 'Inno Setup: App Path', Path) then exit;
  if not RegQueryStringValue(HKCU, Key, 'DisplayVersion', VersionText) then exit;
  { The old installer offered the version as the default folder name, including
    when selecting another drive. Migrate that layout once during upgrade. }
  if not StrToVersion(VersionText + '.0', Version) then exit;
  if ComparePackedVersion(Version, PackVersionComponents(1, 4, 0, 0)) > 0 then exit;
  Path := RemoveBackslashUnlessRoot(Path);
  if CompareText(ExtractFileName(Path), VersionText) = 0 then Result := Path;
end;

function HasLegacyVersionDirectory: Boolean;
begin
  Result := LegacyVersionDirectory <> '';
end;

function DefaultInstallDirectory(Param: String): String;
var
  Legacy, Parent: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\WindowsDiskManager');
  Legacy := LegacyVersionDirectory;
  if Legacy = '' then exit;
  Parent := ExtractFileDir(Legacy);
  if CompareText(ExtractFileName(Parent), 'WindowsDiskManager') = 0 then
    Result := Parent
  else
    Result := AddBackslash(Parent) + 'WindowsDiskManager';
end;

function MenuDirectory(Param: String): String;
begin
  if Isolated then begin
    Result := ExpandConstant('{param:MENUDIR|}');
    if Result = '' then Result := ExpandConstant('{app}\StartMenu');
  end else
    Result := ExpandConstant('{userprograms}');
end;

function UpdateLaunch: Boolean;
begin
  Result := HasOption('/RELAUNCH') and not HasOption('/NOLAUNCH');
end;

function InteractiveLaunch: Boolean;
begin
  Result := not HasOption('/RELAUNCH') and not HasOption('/NOLAUNCH');
end;

function InitializeSetup: Boolean;
var
  Text: String;
  ProcessId: Integer;
  ErrorCode: Integer;
begin
  Result := False;
  if Isolated and (ExpandConstant('{param:DIR|}') = '') then begin
    Log('An isolated installation requires /DIR=<absolute installation directory>.');
    exit;
  end;
  Text := ExpandConstant('{param:UPDATEPID|}');
  if Text <> '' then begin
    ProcessId := StrToIntDef(Text, -1);
    if ProcessId <= 0 then begin
      Log('Invalid /UPDATEPID.');
      exit;
    end;
    PreviousProcess := OpenProcess(SynchronizeAccess, False, ProcessId);
    if PreviousProcess = 0 then begin
      ErrorCode := DLLGetLastError;
      if ErrorCode <> InvalidParameter then begin
        Log(Format('Cannot open the previous process for synchronization (%d).', [ErrorCode]));
        exit;
      end;
    end;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Status: Cardinal;
  DataDir: String;
  LockFile: THandle;
  LockError: Integer;
begin
  Result := '';
  if PreviousProcess <> 0 then begin
    Log('Waiting for the selected process to finish.');
    Status := WaitForSingleObject(PreviousProcess, 120000);
    if Status = WaitTimeout then
      Result := '旧版磁盘空间管理器仍在运行。请关闭后重试。'
    else if Status <> 0 then
      Result := '无法确认旧版磁盘空间管理器已退出，请关闭后重试。';
    if Result <> '' then exit;
  end;
  DataDir := GetEnv('WDM_DATA_DIR');
  if DataDir = '' then begin
    if Isolated then exit;
    DataDir := ExpandConstant('{localappdata}\WindowsDiskManager');
  end;
  { The existing manager holds read/write access with read sharing. This read-only
    OPEN_EXISTING probe detects it without modifying the user's lock or data. }
  LockFile := CreateFileForRead(AddBackslash(DataDir) + 'manager.lock', $80000000,
    1, 0, 3, $80, 0);
  if LockFile <> THandle(-1) then begin
    CloseHandle(LockFile);
    exit;
  end;
  LockError := DLLGetLastError;
  if (LockError = 32) or (LockError = 33) then
    Result := '磁盘空间管理器正在运行。请关闭它，然后返回安装程序重试。'
  else if (LockError <> 2) and (LockError <> 3) then
    Result := '无法检查已有程序的运行状态。请确认当前用户可以读取数据目录后重试。';
end;

procedure DeinitializeSetup;
begin
  if PreviousProcess <> 0 then CloseHandle(PreviousProcess);
end;
