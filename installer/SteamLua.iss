; ============================================================================
;  SteamLua 安装程序脚本（Inno Setup 6）
;
;  编译：ISCC.exe /DMyAppVersion=0.0.9 installer\SteamLua.iss
;  产物：<MyOutputDir>\SteamLua-<版本>-setup.exe
;
;  设计要点：
;  - 程序本体装到 Program Files，对普通用户只读；运行期数据一律写
;    %LocalAppData%\SteamLua（见 Services/AppDataPaths.cs），二者分离。
;  - 程序本身以 asInvoker 运行，安装器只在安装/卸载阶段请求管理员权限。
;  - 仅提供 x64 版本；架构设置直接写死，不再由参数切换。
;  - 以下参数由 CI 或本地命令行通过 /D 传入，缺省值仅供手动编译调试：
;      MyAppVersion、MySourceDir、MyOutputDir
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\publish\portable-x64"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "Output"
#endif


#define MyAppName "SteamLua"
#define MyAppPublisher "SteamLua"
#define MyAppExeName "SteamLua.exe"
#define MyAppUrl "https://github.com/WeYC/SteamLua"
; 单一架构，AppId 固定。改动此值会让已安装用户无法升级，只能重装。
#define MyAppId "{{B5D81F42-6E3A-4A87-9C21-3F7E8D6B1A05}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}

; 默认装到 Program Files\SteamLua；用 {autopf} 让 32/64 位系统各自落到正确目录
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; 程序是 asInvoker，安装器按管理员安装（写 Program Files 需要）
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; 仅 64 位系统；{autopf} 会落到 Program Files
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#MyOutputDir}
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; 最低 Windows 10
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; 桌面快捷方式默认不勾选，避免装完桌面一团
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 主程序与全部依赖必须同目录（deps.json 未声明 probing path，移入子目录会启动失败）
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 运行期数据不打包（settings.json / cache / 日志由程序自己在 LocalAppData 生成）

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 安装完成后可选启动；用当前登录会话而非提权身份，避免程序以管理员权限跑
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; 安装目录里程序会写的只剩可能残留的空目录，用户数据在 LocalAppData 不动
Type: dirifempty; Name: "{app}"

[Code]

// 结束进程需要的 Win32 API（Inno 未内置这些声明）
const
  PROCESS_TERMINATE = $0001;

function OpenProcess(dwDesiredAccess: DWORD; bInheritHandle: BOOL;
  dwProcessId: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';

function TerminateProcess(hProcess: THandle; uExitCode: DWORD): BOOL;
  external 'TerminateProcess@kernel32.dll stdcall';

function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
const
  // .NET 8 Desktop Runtime（Windows Desktop）最低要求
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';

  // 需要检测的运行中程序。主程序会拦截关闭并最小化到托盘，
  // 因此发 WM_CLOSE 无效，必须直接结束进程才能替换文件。
  MainExeName = 'SteamLua.exe';
  // 修改器监视服务部署在安装目录下的 SvcMonitor\，会锁定文件
  MonitorExeName = 'SvcMonitor.exe';
  // 商店页入库桥接进程常驻在 LocalAppData，不影响安装，但一并提示更稳妥
  BridgeExeName = 'SteamLuaBridge.exe';

// 从共享框架目录里找主版本号；目录名形如 8.0.15 / 10.0.15
function AnyVersionAtLeast8(Path: String): Boolean;
var
  FindRec: TFindRec;
  MajorText: String;
begin
  Result := False;
  if not DirExists(Path) then Exit;
  // FindFirst 只列一层，这里够用：shared\<framework>\<版本>
  if FindFirst(AddBackslash(Path) + '*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        MajorText := FindRec.Name;
        if Pos('.', MajorText) > 1 then
          MajorText := Copy(MajorText, 1, Pos('.', MajorText) - 1);
        if StrToIntDef(MajorText, 0) >= 8 then
        begin
          Result := True;
          Exit;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function IsDotNetDesktopInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  MajorText: String;
begin
  // .NET 有滚动升级：net8.0 目标可由 8.x 及更高主版本承载（实测 .NET 10 可正常运行），
  // 因此判断条件是"存在主版本 >= 8 的 WindowsDesktop 运行时"，而不是死等 8.x。
  // 优先看磁盘：某些安装方式（如仅装更高版本）不会在 sharedfx 下登记版本子键，
  // 只读注册表会把已满足环境的机器误判成缺运行时。
  if AnyVersionAtLeast8(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App')) then
  begin
    Result := True;
    Exit;
  end;

  // 兜底：非标准安装位置时再看注册表
  Result := False;
  if RegGetSubkeyNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 To GetArrayLength(Names) - 1 do
    begin
      MajorText := Names[I];
      if Pos('.', MajorText) > 1 then
        MajorText := Copy(MajorText, 1, Pos('.', MajorText) - 1);
      if StrToIntDef(MajorText, 0) >= 8 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// ---- 运行中进程检测 ----
// 用 CreateToolhelp32Snapshot 枚举进程：纯 Win32 API，不依赖 WMI/COM。
// 之前用 WMI 的 Win32_Process 查询在 Inno 的 Pascal Script 里静默失败
// （日志显示根本查不到进程），改为快照方式后可靠。

type
  TProcessEntry32 = record
    dwSize: DWORD;
    cntUsage: DWORD;
    th32ProcessID: DWORD;
    th32DefaultHeapID: DWORD;
    th32ModuleID: DWORD;
    cntThreads: DWORD;
    th32ParentProcessID: DWORD;
    pcPriClassBase: LongInt;
    dwFlags: DWORD;
    szExeFile: array[0..259] of AnsiChar;
  end;

const
  TH32CS_SNAPPROCESS = $00000002;
  // 快照创建失败时返回 (HANDLE)-1，Pascal Script 不支持类型转换，用等值常量比较

function CreateToolhelp32Snapshot(dwFlags, th32ProcessID: DWORD): THandle;
  external 'CreateToolhelp32Snapshot@kernel32.dll stdcall';
function Process32First(hSnapshot: THandle; var lppe: TProcessEntry32): BOOL;
  external 'Process32First@kernel32.dll stdcall';
function Process32Next(hSnapshot: THandle; var lppe: TProcessEntry32): BOOL;
  external 'Process32Next@kernel32.dll stdcall';

// Pascal Script 不支持 String(AnsiCharArray)，手动拼接
function AnsiCharArrayToString(const Chars: array of AnsiChar): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to High(Chars) do
  begin
    if Chars[I] = #0 then Break;
    Result := Result + Char(Chars[I]);
  end;
end;

// 收集所有匹配名字的进程 PID（名字不区分大小写，需带 .exe）
function CollectProcessIds(const ExeName: String; var Pids: TArrayOfInteger): Integer;
var
  Snap: THandle;
  Entry: TProcessEntry32;
  Name: String;
  Found: Integer;
begin
  Found := 0;
  SetArrayLength(Pids, 0);
  Snap := CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
  if Snap = THandle($FFFFFFFF) then
  begin
    Result := 0;
    Exit;
  end;
  try
    Entry.dwSize := SizeOf(Entry);
    if Process32First(Snap, Entry) then
    begin
      repeat
        Name := AnsiCharArrayToString(Entry.szExeFile);
        if CompareText(Name, ExeName) = 0 then
        begin
          Found := Found + 1;
          SetArrayLength(Pids, Found);
          Pids[Found - 1] := Entry.th32ProcessID;
        end;
        Entry.dwSize := SizeOf(Entry);
      until not Process32Next(Snap, Entry);
    end;
  finally
    CloseHandle(Snap);
  end;
  Result := Found;
end;

function IsProcessRunning(const ExeName: String): Boolean;
var
  Pids: TArrayOfInteger;
begin
  Result := CollectProcessIds(ExeName, Pids) > 0;
end;

// 结束所有指定名字的进程
function TerminateProcesses(const ExeName: String): Integer;
var
  Pids: TArrayOfInteger;
  I: Integer;
  Proc: THandle;
begin
  Result := 0;
  CollectProcessIds(ExeName, Pids);
  for I := 0 to GetArrayLength(Pids) - 1 do
  begin
    Proc := OpenProcess(PROCESS_TERMINATE, False, Pids[I]);
    if Proc <> 0 then
    begin
      if TerminateProcess(Proc, 0) then Result := Result + 1;
      CloseHandle(Proc);
    end;
  end;
end;

// 等待进程退出，最多等待指定秒数
function WaitForExit(const ExeName: String; Seconds: Integer): Boolean;
var
  I: Integer;
begin
  for I := 1 to Seconds * 2 do
  begin
    if not IsProcessRunning(ExeName) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;
  Result := not IsProcessRunning(ExeName);
end;

// 安装前统一处理：主程序 / 监视服务 / 桥接进程若在运行，询问用户后结束
function StopRunningAppBeforeInstall(): Boolean;
var
  Prompt: String;
  Silent: Boolean;
begin
  Result := True;
  if not (IsProcessRunning(MainExeName) or
          IsProcessRunning(MonitorExeName) or
          IsProcessRunning(BridgeExeName)) then Exit;

  // 静默安装（/SILENT、/VERYSILENT）不能弹窗：此时直接结束进程继续安装，
  // 否则 MsgBox 在静默模式下自动返回，会导致"看似通过但进程仍在"的假成功。
  Silent := WizardSilent();

  if not Silent then
  begin
    Prompt := 'SteamLua 正在运行，安装程序需要先关闭它才能替换文件。' + #13#10 + #13#10;
    if IsProcessRunning(MainExeName) then
      Prompt := Prompt + '· 主程序 SteamLua.exe' + #13#10;
    if IsProcessRunning(MonitorExeName) then
      Prompt := Prompt + '· 修改器监视服务 SvcMonitor.exe' + #13#10;
    if IsProcessRunning(BridgeExeName) then
      Prompt := Prompt + '· 商店页桥接进程 SteamLuaBridge.exe' + #13#10;
    Prompt := Prompt + #13#10 + '是否现在自动关闭并继续安装？' + #13#10 +
              '选择“否”将退出安装，请手动关闭后重试。';

    if MsgBox(Prompt, mbConfirmation, MB_YESNO) <> IDYES then
    begin
      Result := False;
      Exit;
    end;
  end;

  // 先结束主程序（它会拉起/管理子进程），再收拾子进程
  if IsProcessRunning(MainExeName) then
    TerminateProcesses(MainExeName);
  WaitForExit(MainExeName, 8);

  if IsProcessRunning(MonitorExeName) then
    TerminateProcesses(MonitorExeName);
  WaitForExit(MonitorExeName, 5);

  if IsProcessRunning(BridgeExeName) then
    TerminateProcesses(BridgeExeName);
  WaitForExit(BridgeExeName, 5);

  // 仍有残留就明确告知，避免后续文件覆盖失败给出难懂的报错
  if IsProcessRunning(MainExeName) or IsProcessRunning(MonitorExeName) or
     IsProcessRunning(BridgeExeName) then
  begin
    if not Silent then
      MsgBox('无法自动关闭 SteamLua 的进程（可能权限不足）。' + #13#10 +
             '请手动退出程序后重新运行安装包。', mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrCode: Integer;
begin
  // 先处理运行中的程序：它拦截关闭并最小化到托盘，必须主动结束才能替换文件
  Result := StopRunningAppBeforeInstall();
  if not Result then Exit;

  Result := True;
  if not IsDotNetDesktopInstalled() then
  begin
    if MsgBox('SteamLua 需要 .NET 桌面运行时（.NET 8 或更高版本）才能运行。' + #13#10 + #13#10 +
              '当前系统未检测到。是否现在打开下载页面？' + #13#10 +
              '安装完成后重新运行本安装程序即可。',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
    end;
    // 不阻断安装流程：用户可能想先装好程序再补运行时
    Result := True;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 卸载后询问是否清除用户数据（配置与缓存都在 LocalAppData）。
    // 静默卸载（/SILENT、/VERYSILENT）下不能弹窗，必须默认保留：
    // 静默场景通常是升级或批量部署，误删用户登录凭据的代价太大。
    if UninstallSilent() then Exit;

    DataDir := ExpandConstant('{localappdata}\SteamLua');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除 SteamLua 的用户数据？' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                '包含设置、登录凭据与缓存。选择“否”将保留，重装后可继续使用。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
