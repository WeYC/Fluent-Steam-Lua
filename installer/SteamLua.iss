; ============================================================================
;  SteamLua 安装程序脚本（Inno Setup 6）
;
;  编译：ISCC.exe /DMyAppVersion=0.0.2 installer\SteamLua.iss
;  产物：installer\Output\SteamLua-<版本>-setup.exe
;
;  设计要点：
;  - 程序本体装到 Program Files，对普通用户只读；运行期数据一律写
;    %LocalAppData%\SteamLua（见 Services/AppDataPaths.cs），二者分离。
;  - 程序本身以 asInvoker 运行，安装器只在安装/卸载阶段请求管理员权限。
;  - 以下参数由 CI 或本地命令行通过 /D 传入，缺省值仅供手动编译调试：
;      MyAppVersion、MySourceDir、MyOutputDir
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef MySourceDir
  #define MySourceDir "..\publish\loose"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "Output"
#endif

#define MyAppName "SteamLua"
#define MyAppPublisher "SteamLua"
#define MyAppExeName "SteamLua.exe"
#define MyAppUrl "https://github.com/WeYC/SteamLua"
#define MyAppId "{{7A3C2E70-9B4D-4C1E-8F6A-2D5B8E1C4A93}"

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

; 允许用户在 ARM64 上安装
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
const
  // .NET 8 Desktop Runtime（Windows Desktop）最低要求
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';

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

function InitializeSetup(): Boolean;
var
  ErrCode: Integer;
begin
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
