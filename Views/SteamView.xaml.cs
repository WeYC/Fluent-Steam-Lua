using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

// 「Steam」页：启动 / 重启 / 切换账号三个操作，从主窗口浮动按钮菜单迁出，成为固定导航入口。
// 账号列表取自 Steam 的 loginusers.vdf，切换写 AutoLoginUser 注册表项后拉起 Steam。
public partial class SteamView : UserControl
{
    // 账号行：AvatarPath 为空时界面显示无头像占位
    public sealed class SteamAccount
    {
        public string SteamId { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string? AvatarPath { get; set; }
        public bool IsCurrent { get; set; }
    }

    private readonly ISteamPathService _steamPathService;
    private List<SteamAccount> _accounts = new();

    public SteamView(ISteamPathService steamPathService)
    {
        _steamPathService = steamPathService;
        InitializeComponent();
        Loaded += (_, _) => RefreshAccounts();
    }

    // ========== 启动 / 重启 ==========

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info(L.Get("LogCat_Op"), L.Get("Steam_LogAction", L.Get("Steam_Start")));
        var result = SteamProcess.LaunchSteam(_steamPathService);
        if (!result.Ok)
            await ShowDialogAsync(result.Title, result.Message);
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        LogService.Info(L.Get("LogCat_Op"), L.Get("Steam_LogAction", L.Get("Steam_Restart")));
        var result = SteamProcess.RestartSteam(_steamPathService);
        if (!result.Ok)
            await ShowDialogAsync(result.Title, result.Message);
    }

    // ========== 账号切换 ==========

    private void RefreshAccounts_Click(object sender, RoutedEventArgs e) => RefreshAccounts();

    private void RefreshAccounts()
    {
        var parsed = ParseLoginUsersVdf() ?? new List<SteamAccount>();

        // 头像走 Steam 的 avatarcache 目录，文件缺失就留空由界面兜底
        var steamPath = _steamPathService.GetCustomPath() ?? _steamPathService.DetectSteamPath();
        if (!string.IsNullOrEmpty(steamPath))
        {
            var current = GetCurrentAccountName();
            foreach (var acc in parsed)
            {
                var avatarPath = Path.Combine(steamPath, "config", "avatarcache", $"{acc.SteamId}.png");
                if (File.Exists(avatarPath))
                    acc.AvatarPath = avatarPath;
                acc.IsCurrent = !string.IsNullOrEmpty(current) &&
                                string.Equals(acc.AccountName, current, StringComparison.OrdinalIgnoreCase);
            }
        }

        _accounts = parsed;
        AccountList.ItemsSource = _accounts;
        AccountEmptyText.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AccountItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: SteamAccount acc }) return;
        await SwitchSteamAccountAsync(acc);
    }

    private void AccountItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.Background = (Brush)Application.Current.FindResource("SystemControlHighlightListMediumBrush");
    }

    private void AccountItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
    }

    private async Task SwitchSteamAccountAsync(SteamAccount target)
    {
        try
        {
            SteamProcess.KillSteamProcesses();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true))
            {
                if (key == null)
                {
                    await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_AccRegFailed"));
                    return;
                }
                key.SetValue("AutoLoginUser", target.AccountName, RegistryValueKind.String);
            }
            var launchResult = SteamProcess.LaunchSteam(_steamPathService);
            if (!launchResult.Ok)
                await ShowDialogAsync(launchResult.Title, launchResult.Message);
            else
                RefreshAccounts();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_AccSwitchFailed", ex.Message));
        }
    }

    // 当前自动登录账号：与切换时写入的是同一个键，用于列表高亮
    private static string? GetCurrentAccountName()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("AutoLoginUser") as string;
        }
        catch
        {
            return null;
        }
    }

    private List<SteamAccount>? ParseLoginUsersVdf()
    {
        try
        {
            var steamPath = _steamPathService.GetCustomPath() ?? _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return null;

            var vdfPath = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(vdfPath)) return null;

            var content = File.ReadAllText(vdfPath);
            var accounts = new List<SteamAccount>();

            foreach (Match blockMatch in Regex.Matches(content, "\\\"(\\d+)\\\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline))
            {
                var body = blockMatch.Groups["body"].Value;
                var accountName = GetVdfValue(body, "AccountName");
                var personaName = GetVdfValue(body, "PersonaName");
                if (string.IsNullOrEmpty(accountName))
                    continue;

                accounts.Add(new SteamAccount
                {
                    SteamId = blockMatch.Groups[1].Value,
                    AccountName = accountName,
                    PersonaName = string.IsNullOrEmpty(personaName) ? accountName : personaName,
                });
            }
            return accounts;
        }
        catch { return null; }
    }

    private static string GetVdfValue(string block, string key)
    {
        var match = Regex.Match(block, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]*)\\\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            CloseButtonText = L.Get("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }
}
