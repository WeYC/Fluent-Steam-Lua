using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using SteamLua.Services;
using SteamLua.ViewModels;

namespace SteamLua.Views;

public partial class MainWindow : Window
{
    private readonly string[] _navOrder = ["Home", "ScriptDownload", "Manifest", "Extraction", "Authorization", "Trainer", "Achievement", "CloudSave", "OnlineFix", "CrackTool", "Pattern", "Steam", "Settings", "About"];
    private string _prevTag = "Home";

    /// <summary>当前页面 tag，供全局操作日志标注上下文。</summary>
    public static string? CurrentPage { get; private set; }

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly ISteamPathService _steamPathService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ScriptDownloadViewModel _scriptDownloadViewModel;
    private readonly ExtractionViewModel _extractionViewModel;
    private readonly TrainerViewModel _trainerViewModel;
    private readonly AchievementViewModel _achievementViewModel;
    private readonly AuthorizationViewModel _authorizationViewModel;
    private readonly ManifestViewModel _manifestViewModel;
    private readonly CloudSaveViewModel _cloudSaveViewModel;
    private readonly OnlineFixViewModel _onlineFixViewModel;
    private readonly CrackToolViewModel _crackToolViewModel;
    private readonly HomeView _homeView;
    private readonly SettingsView _settingsView;
    private readonly ScriptDownloadView _scriptDownloadView;
    private readonly ExtractionView _extractionView;
    private readonly TrainerView _trainerView;
    private readonly AchievementView _achievementView;
    private readonly AuthorizationView _authorizationView;
    private readonly ManifestView _manifestView;
    private readonly CloudSaveView _cloudSaveView;
    private readonly OnlineFixView _onlineFixView;
    private readonly CrackToolView _crackToolView;
    private readonly PatternView _patternView;
    private readonly SteamView _steamView;
    private readonly AboutView _aboutView;
    private readonly IOpenSteamToolService _openSteamToolService;
    private TrayIconManager? _trayIcon;
    private bool _exitRequested;

    // 侧栏最小展开宽度（紧凑模式下的中文基线），以及图标+间距+左右内边距+右侧留白占用宽度
    private const double MinPaneLength = 105;
    private const double PaneChromeWidth = 80;

    public MainWindow(MainViewModel viewModel, SettingsViewModel settingsViewModel, ScriptDownloadViewModel scriptDownloadViewModel, ExtractionViewModel extractionViewModel, TrainerViewModel trainerViewModel, AchievementViewModel achievementViewModel, AuthorizationViewModel authorizationViewModel, ManifestViewModel manifestViewModel, CloudSaveViewModel cloudSaveViewModel, OnlineFixViewModel onlineFixViewModel, CrackToolViewModel crackToolViewModel, ISettingsService settingsService, ISteamPathService steamPathService, IOpenSteamToolService openSteamToolService)
    {
        InitializeComponent();
        CurrentPage = "Home";
        _openSteamToolService = openSteamToolService;
        _dropHintHideTimer.Tick += (_, _) => { _dropHintHideTimer.Stop(); HideDropHint(DropHintGrid); HideDropHint(DropAuthHintGrid); };

        _viewModel = viewModel;
        _settingsViewModel = settingsViewModel;
        _scriptDownloadViewModel = scriptDownloadViewModel;
        _extractionViewModel = extractionViewModel;
        _trainerViewModel = trainerViewModel;
        _achievementViewModel = achievementViewModel;
        _authorizationViewModel = authorizationViewModel;
        _manifestViewModel = manifestViewModel;
        _cloudSaveViewModel = cloudSaveViewModel;
        _onlineFixViewModel = onlineFixViewModel;
        _crackToolViewModel = crackToolViewModel;
        _settingsService = settingsService;
        _steamPathService = steamPathService;
        DataContext = _viewModel;

        var iconUri = new Uri("pack://application:,,,/Assets/app.ico");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bestFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        Icon = bestFrame;

        _homeView = new HomeView { DataContext = _viewModel };
        _settingsView = new SettingsView { DataContext = settingsViewModel };
        _scriptDownloadView = new ScriptDownloadView { DataContext = scriptDownloadViewModel };
        _extractionView = new ExtractionView { DataContext = extractionViewModel };
        _trainerView = new TrainerView { DataContext = trainerViewModel };
        _achievementView = new AchievementView { DataContext = achievementViewModel };
        _authorizationView = new AuthorizationView { DataContext = authorizationViewModel };
        _manifestView = new ManifestView { DataContext = manifestViewModel };
        _cloudSaveView = new CloudSaveView { DataContext = cloudSaveViewModel };
        _onlineFixView = new OnlineFixView { DataContext = onlineFixViewModel };
        _crackToolView = new CrackToolView { DataContext = crackToolViewModel };
        _patternView = new PatternView(openSteamToolService, steamPathService);
        _steamView = new SteamView(steamPathService);
        _aboutView = new AboutView();
        ContentTransition.Content = _homeView;

        settingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
        Closed += MainWindow_Closed;
        // 标题只在窗口构建时求值一次，切语言需重新生成才不会残留旧语言
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        // 用户选择最小化到托盘：拦截关闭并隐藏到系统托盘
        if (!_exitRequested && _settingsService.Load().MinimizeToTray)
        {
            e.Cancel = true;
            EnsureTrayIcon();
            _trayIcon!.Visible = true;
            Hide();
            _trayIcon.ShowBalloonOnce(L.Get("Main_TrayTitle"), L.Get("Main_TrayMinimized"));
            return;
        }

        DisposeTrayIcon();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null) return;
        _trayIcon = new TrayIconManager();
        _trayIcon.RestoreRequested += RestoreFromTray;
        _trayIcon.OpenSettingsRequested += OpenSettingsFromTray;
        _trayIcon.ExitRequested += () =>
        {
            _exitRequested = true;
            DisposeTrayIcon();
            Application.Current.Shutdown();
        };
    }

    private void OpenSettingsFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        NavView.SelectedItem = SettingsItem;
    }

    /// <summary>供 Key 弹窗等外部调用：切到指定页面（仅支持已知 Tag）。</summary>
    public void NavigateTo(string tag)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                NavView.SelectedItem = tag switch
                {
                    "Home" => HomeItem,
                    "ScriptDownload" => ScriptDownloadItem,
                    "Manifest" => ManifestItem,
                    "Extraction" => ExtractionItem,
                    "Authorization" => AuthorizationItem,
                    "Trainer" => TrainerItem,
                    "Achievement" => AchievementItem,
                    "CloudSave" => CloudSaveItem,
                    "OnlineFix" => OnlineFixItem,
                    "CrackTool" => CrackToolItem,
                    "Pattern" => PatternItem,
                    "Steam" => SteamItem,
                    "Settings" => SettingsItem,
                    "About" => AboutItem,
                    _ => null
                };
            });
        }
        catch { }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null) return;
        _trayIcon.RestoreRequested -= RestoreFromTray;
        _trayIcon.OpenSettingsRequested -= OpenSettingsFromTray;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void SettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel svm) return;
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SelectedBackdrop):
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
            case nameof(SettingsViewModel.IsCardRefreshVisible):
                _viewModel.IsCardRefreshVisible = svm.IsCardRefreshVisible;
                break;
            case nameof(SettingsViewModel.SelectedTheme):
                UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        DisposeIfNeeded(_viewModel);
        DisposeIfNeeded(_settingsViewModel);
        DisposeIfNeeded(_scriptDownloadViewModel);
        DisposeIfNeeded(_extractionViewModel);
        DisposeIfNeeded(_trainerViewModel);
        DisposeIfNeeded(_achievementViewModel);
        DisposeIfNeeded(_authorizationViewModel);
        DisposeIfNeeded(_manifestViewModel);
        DisposeIfNeeded(_cloudSaveViewModel);
        DisposeIfNeeded(_onlineFixViewModel);
        DisposeIfNeeded(_crackToolViewModel);
    }

    private static void DisposeIfNeeded(object obj)
    {
        if (obj is IDisposable d) d.Dispose();
    }

    private void RefreshTitle()
    {
        var status = _steamPathService.DetectSteamToolType() switch
        {
            SteamToolType.OpenSteamTool => L.Get("Home_KernelOST"),
            SteamToolType.SteamTools => L.Get("Home_KernelMismatch", nameof(SteamToolType.SteamTools)),
            SteamToolType.GreenLuma => L.Get("Home_KernelMismatch", nameof(SteamToolType.GreenLuma)),
            _ => L.Get("Home_KernelMissing")
        };
        Title = L.Get("Main_TitleFormat", status);
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTitle();
        // 导航项宽度取自 Content（由 {l:Loc} 绑定填充），绑定刷新与本次回调的先后顺序 WPF 不保证，
        // 延后到本轮回调队列末尾再量宽，确保读到的是新语言文本
        Dispatcher.BeginInvoke(new Action(UpdatePaneLength), DispatcherPriority.Loaded);
    }

    // 侧栏展开宽度按当前语言最长标签测算：中文短、英文长，固定值不是挤出省略号就是留一大片空白
    private void UpdatePaneLength()
    {
        var maxLabel = 0.0;
        foreach (var obj in NavView.MenuItems.Cast<object>().Concat(NavView.FooterMenuItems.Cast<object>()))
        {
            if (obj is NavigationViewItem item)
                maxLabel = Math.Max(maxLabel, MeasureNavLabel(item.Content as string));
        }
        NavView.OpenPaneLength = Math.Max(MinPaneLength, Math.Ceiling(maxLabel + PaneChromeWidth));
    }

    // 用与菜单项同一套字体度量文本自然宽度，避免不同语言字形差异带来的估算偏差
    private double MeasureNavLabel(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = HomeItem.FontFamily,
            FontSize = HomeItem.FontSize,
            FontWeight = HomeItem.FontWeight,
            FontStyle = HomeItem.FontStyle,
            FontStretch = HomeItem.FontStretch
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadedCommand.CanExecute(null))
            await _viewModel.LoadedCommand.ExecuteAsync(null);

        RefreshTitle();
        UpdatePaneLength();

        var settings = _settingsService.Load();
        UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
        UpdateBackdrop(settings.SelectedBackdrop);
        _settingsViewModel.SelectedBackdrop = settings.SelectedBackdrop;

        // 引导用户去「模式」页处理内核时不回到主页，否则刚跳过去又被打回去
        var navigatedToPattern = false;

        var kernelStatus = _viewModel.OpenSteamToolStatus;
        if (kernelStatus == L.Get("Home_KernelMissing"))
        {
            await ShowModernDialogAsync(
                L.Get("Main_KernelMissingTitle"),
                L.Get("Main_KernelMissingBody"));
        }
        else if (kernelStatus == L.Get("Home_KernelMismatch", nameof(SteamToolType.SteamTools)) ||
                 kernelStatus == L.Get("Home_KernelMismatch", nameof(SteamToolType.GreenLuma)))
        {
            var foreignName = kernelStatus.Contains("GreenLuma") ? "GreenLuma" : "SteamTools";
            var goPurify = await ShowModernConfirmAsync(
                L.Get("Main_ForeignTitle", foreignName),
                L.Get("Main_ForeignBody", foreignName),
                L.Get("Main_Purify"));
            if (goPurify)
            {
                NavigateTo("Pattern");
                navigatedToPattern = true;
            }
        }

        // OST 已安装时检测逻辑优先返回，开机即便混装了第三方也不会报；
        // 这里单独扫一次，有残留就提示净化（不处理则每次启动都提示，直至清干净）
        if (_openSteamToolService.IsInstalled)
        {
            try
            {
                var foreign = await _openSteamToolService.ScanForeignKernelsAsync();
                if (foreign.Count > 0)
                {
                    var goPurifyMixed = await ShowModernConfirmAsync(
                        L.Get("Main_ConflictTitle"),
                        L.Get("Main_ConflictBody", string.Join("\n", foreign.Select(f => $"• {f.RelativePath}（{f.Owner}）"))),
                        L.Get("Main_Purify"));
                    if (goPurifyMixed)
                    {
                        NavigateTo("Pattern");
                        navigatedToPattern = true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Main_LogConflictScanFailed", ex.Message));
            }
        }

        if (!navigatedToPattern)
            HomeItem.IsSelected = true;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (_settingsViewModel.SelectedTheme != "System") return;

        Dispatcher.Invoke(() =>
        {
            var isLight = GetSystemIsLightTheme();
            ThemeManager.Current.ApplicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
            UpdateBackdropTheme(isLight);
        });
    }

    private static bool GetSystemIsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1;
        }
        catch { }
        return false;
    }

    private async Task ShowModernDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = L.Get("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowModernConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
    {
        primaryText ??= L.Get("Common_Confirm");
        closeText ??= L.Get("Common_Cancel");
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // 背景材质只影响美观，任何异常都回退纯色不断运行；
    // 材质在部分机器（远程桌面/基础显示驱动/异 DPI）首帧布局阶段翻车，
    // 之前是 XAML 硬编码导致崩在 Show 之前，连兜底都来不及
    public void UpdateBackdrop(string backdropTypeName)
    {
        if (!Enum.TryParse<BackdropType>(backdropTypeName, true, out var backdropType))
            backdropType = BackdropType.None;

        try
        {
            WindowHelper.SetSystemBackdropType(this, backdropType);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Backdrop"), L.Get("Main_LogBackdropFailed", backdropType, ex.Message));
            backdropType = BackdropType.None;
        }

        if (backdropType == BackdropType.None)
        {
            var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        }
        else
        {
            Background = null;
        }
    }

    private void UpdateBackdropTheme(bool isLight)
    {
        if (isLight)
        {
            BackdropHelper.RemoveDarkMode(this);
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xF0, 0xF5, 0xF5, 0xF5));
        }
        else
        {
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
            BackdropHelper.ApplyDarkMode(this);
        }
    }

    private void NavView_SelectionChanged(object sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        if (tag != "ScriptDownload")
        {
            // 先取消在飞的入库任务再清 UI，否则后台继续跑但进度无处显示
            _scriptDownloadViewModel.CancelDownload();
            _scriptDownloadViewModel.LogLines.Clear();
            _scriptDownloadViewModel.SearchResults.Clear();
            _scriptDownloadViewModel.ResetSearchState();
            _scriptDownloadViewModel.StatusMessage = "";
        }
        if (tag != "Extraction")
        {
            _extractionViewModel.LogLines.Clear();
            _extractionViewModel.StatusMessage = "";
        }
        if (tag != "OnlineFix")
        {
            _onlineFixViewModel.CancelDownload();
            _onlineFixViewModel.LogLines.Clear();
            _onlineFixViewModel.SearchResults.Clear();
            _onlineFixViewModel.ResetState();
            _onlineFixViewModel.StatusMessage = "";
        }
        if (tag != "CrackTool")
        {
            _crackToolViewModel.CancelCrack();
            _crackToolViewModel.LogLines.Clear();
            _crackToolViewModel.ResetState();
            _crackToolViewModel.StatusMessage = "";
        }
        if (tag != "Settings")
        {
            // 三个测速结果面板的可见性都绑在集合 Count 上，切出设置页统一清空收起
            _settingsViewModel.SpeedTestResults.Clear();
            _settingsViewModel.MirrorSpeedTestResults.Clear();
            _settingsViewModel.ManifestTestResults.Clear();
            _settingsViewModel.StatusMessage = "";
        }
        var prevIndex = Array.IndexOf(_navOrder, _prevTag);
        var newIndex = Array.IndexOf(_navOrder, tag);
        if (prevIndex >= 0 && newIndex >= 0)
        {
            if (newIndex > prevIndex)
            {
                ContentTransition.Transition = TransitionType.Down;
            }
            else if (newIndex < prevIndex)
            {
                ContentTransition.Transition = TransitionType.Up;
            }
        }
        _prevTag = tag;

        SwitchView(tag);
    }

    private void SwitchView(string tag)
    {
        UserControl? newView = tag switch
        {
            "Home" => _homeView,
            "Settings" => _settingsView,
            "ScriptDownload" => _scriptDownloadView,
            "Manifest" => _manifestView,
            "Extraction" => _extractionView,
            "Trainer" => _trainerView,
            "Achievement" => _achievementView,
            "Authorization" => _authorizationView,
            "CloudSave" => _cloudSaveView,
            "OnlineFix" => _onlineFixView,
            "CrackTool" => _crackToolView,
            "Pattern" => _patternView,
            "Steam" => _steamView,
            "About" => _aboutView,
            _ => null
        };

        if (newView is null || newView == ContentTransition.Content) return;
        ContentTransition.Content = newView;
        CurrentPage = tag;
        LogService.Info(L.Get("LogCat_Nav"), L.Get("Main_LogSwitched", tag));

        if (tag == "Trainer")
        {
            _ = _trainerViewModel.LoadSectionsCommand.ExecuteAsync(null);
        }
        else if (tag == "ScriptDownload")
        {
            _scriptDownloadViewModel.OnNavigatedTo();
        }
        else if (tag == "Achievement")
        {
            _ = _achievementViewModel.EnsureLoadedAsync();
        }
        else if (tag == "Manifest")
        {
            _manifestViewModel.OnNavigatedTo();
        }
        else if (tag == "CloudSave")
        {
            _cloudSaveViewModel.OnNavigatedTo();
        }
        else if (tag == "CrackTool")
        {
            _crackToolViewModel.OnNavigatedTo();
        }
        else if (tag == "Pattern")
        {
            // 每次进入都重新读磁盘状态：内核可能刚被外部装/卸过
            _patternView.RefreshState();
        }
    }

    private readonly DispatcherTimer _dropHintHideTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>判断文件是否为授权票据文件（tickets.txt，或内容含票据标记的 .txt）。</summary>
    private static bool IsTicketFile(string path)
    {
        try
        {
            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "tickets",
                    StringComparison.OrdinalIgnoreCase))
                return true;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[1024];
            var n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return false;
            var head = Encoding.UTF8.GetString(buf, 0, n);
            return head.Contains("appid:", StringComparison.OrdinalIgnoreCase) ||
                   head.Contains("appticket", StringComparison.OrdinalIgnoreCase) ||
                   head.Contains("eticket", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTicketDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        return data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsTicketFile);
    }

    // 拖拽遮罩显示：快速淡入（100ms EaseOut），连续 DragOver 不重启动画
    private static void ShowDropHint(Grid grid)
    {
        if (grid.Visibility == Visibility.Visible) return;
        grid.Opacity = 0;
        grid.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        grid.BeginAnimation(OpacityProperty, fade);
    }

    // 拖拽遮罩隐藏：即时收起并复位动画状态
    private static void HideDropHint(Grid grid)
    {
        grid.Visibility = Visibility.Collapsed;
        grid.BeginAnimation(OpacityProperty, null);
        grid.Opacity = 1;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var isTicket = IsTicketDrop(e.Data);
        if (isTicket && CurrentPage != "Authorization")
        {
            // 授权票据仅在授权页支持拖拽导入：其他页面不显示遮罩、不响应
            e.Effects = DragDropEffects.None;
            HideDropHint(DropAuthHintGrid);
            HideDropHint(DropHintGrid);
            _dropHintHideTimer.Stop();
            return;
        }
        e.Effects = DragDropEffects.Copy;
        if (isTicket)
        {
            HideDropHint(DropHintGrid);
            ShowDropHint(DropAuthHintGrid);
        }
        else
        {
            HideDropHint(DropAuthHintGrid);
            ShowDropHint(DropHintGrid);
        }
        _dropHintHideTimer.Stop();
        _dropHintHideTimer.Start();
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        _dropHintHideTimer.Stop();
        HideDropHint(DropHintGrid);
        HideDropHint(DropAuthHintGrid);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        _dropHintHideTimer.Stop();
        HideDropHint(DropHintGrid);
        HideDropHint(DropAuthHintGrid);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;

        var tickets = CurrentPage == "Authorization"
            ? files.Where(IsTicketFile).ToArray()
            : Array.Empty<string>();
        var others = files.Where(f => !IsTicketFile(f)).ToArray();

        if (tickets.Length > 0)
        {
            LogService.Info(L.Get("LogCat_Op"), L.Get("Main_LogDragTickets", tickets.Length, string.Join("; ", tickets)));
            foreach (var ticket in tickets)
                await _authorizationViewModel.ImportFileCommand.ExecuteAsync(ticket);
        }
        if (others.Length > 0)
        {
            LogService.Info(L.Get("LogCat_Op"), L.Get("Main_LogDragFiles", others.Length, string.Join("; ", others)));
            await _viewModel.HandleDropAsync(others);
        }
    }

}
