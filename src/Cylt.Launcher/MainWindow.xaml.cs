using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cylt.Core;
using Microsoft.Win32;

namespace Cylt.Launcher;

public partial class MainWindow : Window
{
    private readonly LocalStore store = new(AppPaths.Root);
    private readonly HttpClient authHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly HttpClient downloadHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly AuthenticationService auth;
    private readonly GameService game;
    private readonly ResourceService resources;
    private LauncherSettings settings = new();
    private LauncherAccount? account;
    private ThirdPartyResult? thirdPartyLogin;
    private CancellationTokenSource? operation;
    private Process? gameProcess;
    private string microsoftUrl = "";
    private bool ready;

    public MainWindow()
    {
        InitializeComponent();
        auth = new(authHttp);
        game = new(downloadHttp);
        resources = new(authHttp);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { settings = store.LoadSettings(); }
        catch (Exception ex) when (ex is IOException or JsonException)
        { MessageBox.Show(this, "已有设置无法读取，本次使用默认设置。原文件保留，保存新设置后才会覆盖。", "CYLT Launcher"); }
        GamePathBox.Text = settings.GameDirectory;
        JavaPathBox.Text = settings.JavaPath;
        MemoryBox.Text = settings.MemoryMb.ToString();
        ClientIdBox.Text = settings.MicrosoftClientId;
        SnapshotCheck.IsChecked = settings.IncludeSnapshots;
        try
        {
            var saved = store.LoadSecret("account");
            if (saved is not null) { account = JsonSerializer.Deserialize<LauncherAccount>(saved); RememberAccountCheck.IsChecked = true; }
            UpdateKeyStatus();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException)
        { StatusText.Text = "已保存的凭据无法解密，请重新登录或配置。"; }
        RefreshAccountDisplay();
        ready = true;
        await RunAsync("正在读取游戏版本…", async ct => await LoadVersionsAsync(ct));
    }

    private void Navigate(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private void ShowPage(string page)
    {
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = page == "Accounts" ? Visibility.Visible : Visibility.Collapsed;
        ResourcesPage.Visibility = page == "Resources" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "About" ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "Accounts" => ("账号管理", "三种方式，由你选择。"),
            "Resources" => ("发现资源", "为你的世界，添一点新鲜。"),
            "Settings" => ("启动设置", "只调整真正需要的选项。"),
            "About" => ("关于", "一个独立、简洁的启动器。"),
            _ => ("开始游戏", "选好版本，继续你的世界。")
        };
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        operation = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Foreground = (Brush)FindResource("Ink");
        StatusText.Text = message;
        try { await action(operation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "操作已取消；已下载的文件会保留供下次继续。"; }
        catch (UserFacingException ex) { ShowError(ex.Message); }
        catch (HttpRequestException) { ShowError("网络请求未完成。请检查网络连接后重试；本地游戏文件仍然保留。"); }
        catch (UnauthorizedAccessException) { ShowError("没有文件夹写入权限，请在设置中选择可写入的游戏目录。"); }
        catch (IOException) { ShowError("文件操作失败。请检查磁盘空间，以及文件是否正被其他程序使用。"); }
        catch (Exception ex) { ShowError($"操作未完成（{ex.GetType().Name}）。请检查游戏版本和 Java 设置后重试。"); }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        foreach (var control in new Control[] { RefreshButton, InstallButton, VersionBox, SnapshotCheck, MicrosoftLoginButton,
            ThirdPartyLoginButton, OfflineButton, SaveSettingsButton, SearchButton, RememberAccountCheck }) control.IsEnabled = !busy;
        LaunchButton.IsEnabled = !busy && (gameProcess is null || gameProcess.HasExited);
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TaskProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TaskProgress.IsIndeterminate = busy;
    }

    private void ShowError(string text) { StatusText.Foreground = new SolidColorBrush(Color.FromRgb(166, 66, 44)); StatusText.Text = text; }
    private IProgress<WorkProgress> Progress() => new Progress<WorkProgress>(p =>
    {
        if (operation is null) return;
        StatusText.Text = p.Message;
        TaskProgress.IsIndeterminate = p.Fraction is null;
        if (p.Fraction is not null) TaskProgress.Value = Math.Clamp(p.Fraction.Value, 0, 1);
    });
    private void CancelTask(object sender, RoutedEventArgs e) => operation?.Cancel();

    private async Task LoadVersionsAsync(CancellationToken ct)
    {
        IReadOnlyList<GameVersion> versions;
        try { versions = await game.GetVersionsAsync(settings, ct); }
        catch (OperationCanceledException) { throw; }
        catch
        {
            versions = GameService.GetLocalVersions(settings);
            if (versions.Count == 0) throw;
            StatusText.Text = "在线列表暂不可用，已显示本地版本。";
        }
        VersionBox.ItemsSource = versions;
        VersionBox.SelectedItem = versions.FirstOrDefault(v => v.Id == settings.SelectedVersion)
            ?? versions.FirstOrDefault(v => v.Installed) ?? versions.FirstOrDefault();
        StatusText.Text = $"共找到 {versions.Count} 个版本。";
    }

    private async void RefreshVersions(object sender, RoutedEventArgs e) => await RunAsync("正在刷新版本列表…", LoadVersionsAsync);
    private async void SnapshotChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings.IncludeSnapshots = SnapshotCheck.IsChecked == true;
        await RunAsync("正在刷新版本列表…", LoadVersionsAsync);
    }
    private void VersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionBox.SelectedItem is not GameVersion version || VersionHint is null) return;
        settings.SelectedVersion = version.Id;
        VersionHint.Text = version.Installed ? "本地版本已就绪" : "首次启动将安装此版本";
    }

    private async void InstallGame(object sender, RoutedEventArgs e)
    {
        if (VersionBox.SelectedItem is not GameVersion version) { ShowError("请先选择游戏版本。"); return; }
        await RunAsync("正在准备安装…", async ct =>
        {
            var progress = Progress();
            await Task.Run(() => game.InstallAsync(settings, version.Id, progress, ct), ct);
            store.SaveSettings(settings);
            await LoadVersionsAsync(ct);
            StatusText.Text = $"{version.Id} 安装完成，可以开始游戏。";
        });
    }

    private async void LaunchGame(object sender, RoutedEventArgs e)
    {
        if (VersionBox.SelectedItem is not GameVersion version) { ShowError("请先选择游戏版本。"); return; }
        if (account is null) { ShowPage("Accounts"); StatusText.Text = "请选择一个账号，然后返回开始游戏。"; return; }
        await RunAsync("正在准备启动…", async ct =>
        {
            var activeAccount = account;
            if (activeAccount.Kind == AccountKind.Microsoft && activeAccount.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
                activeAccount = await auth.RefreshMicrosoftAsync(activeAccount, settings.MicrosoftClientId, Progress(), ct);
            else if (activeAccount.Kind == AccountKind.ThirdParty)
                activeAccount = await auth.RefreshThirdPartyAsync(activeAccount, ct);
            SetAccount(activeAccount);
            var progress = Progress();
            var process = await Task.Run(() => game.BuildProcessAsync(settings, version.Id, activeAccount,
                !version.Installed, progress, ct), ct);
            ct.ThrowIfCancellationRequested();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                int code = process.ExitCode;
                if (ReferenceEquals(gameProcess, process)) gameProcess = null;
                LaunchButton.IsEnabled = operation is null;
                StatusText.Text = code == 0 ? "游戏已退出。下次见！" : $"游戏已退出（代码 {code}）。可尝试安装 / 修复，或检查游戏目录下的 logs。";
                process.Dispose();
            });
            gameProcess = process;
            try { if (!process.Start()) throw new UserFacingException("游戏进程未能启动。"); }
            catch { gameProcess = null; process.Dispose(); throw; }
            store.SaveSettings(settings);
            StatusText.Text = $"{version.Id} 正在运行。关闭启动器不会结束游戏。";
        });
    }

    private void UseOffline(object sender, RoutedEventArgs e)
    {
        try { SetAccount(LauncherAccount.Offline(OfflineNameBox.Text)); ShowPage("Home"); StatusText.Text = "离线账号已选择。"; }
        catch (UserFacingException ex) { ShowError(ex.Message); }
    }
    private void SetAccount(LauncherAccount value) { account = value; PersistAccount(); RefreshAccountDisplay(); }
    private void PersistAccount()
    {
        if (RememberAccountCheck.IsChecked == true && account is not null) store.SaveSecret("account", JsonSerializer.Serialize(account));
        else store.ForgetSecret("account");
    }
    private void RefreshAccountDisplay()
    {
        string type = account?.Kind switch { AccountKind.Microsoft => "微软正版", AccountKind.ThirdParty => "第三方", _ => "离线" };
        AccountBadge.Text = account is null ? "尚未选择账号" : $"{account.Name} · {type}";
        CurrentAccountText.Text = account is null ? "当前账号：未选择" : $"当前账号：{account.Name}（{type}）";
    }
    private void RememberChanged(object sender, RoutedEventArgs e) { try { PersistAccount(); } catch { ShowError("凭据保存失败，请检查本地数据目录权限。"); } }
    private void ForgetAccount(object sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        account = null; thirdPartyLogin = null; store.ForgetSecret("account"); RefreshAccountDisplay(); StatusText.Text = "已退出账号并清除本机保存的登录信息。";
    }
    private async void MicrosoftLogin(object sender, RoutedEventArgs e) => await RunAsync("正在获取微软登录代码…", async ct =>
    {
        DeviceCodeBox.Visibility = MicrosoftWebButton.Visibility = Visibility.Collapsed;
        var prompt = new Progress<DeviceCodeInfo>(code =>
        {
            DeviceCodeBox.Text = code.Code; microsoftUrl = code.VerificationUrl;
            DeviceCodeBox.Visibility = MicrosoftWebButton.Visibility = Visibility.Visible;
            StatusText.Text = "请打开微软登录网页，输入上方代码并完成授权。";
        });
        try { SetAccount(await auth.MicrosoftLoginAsync(settings.MicrosoftClientId, prompt, Progress(), ct)); StatusText.Text = "正版账号验证和游戏所有权检查已完成。"; }
        finally { DeviceCodeBox.Visibility = MicrosoftWebButton.Visibility = Visibility.Collapsed; DeviceCodeBox.Clear(); microsoftUrl = ""; }
    });
    private void OpenMicrosoftLogin(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(microsoftUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            (uri.Host == "microsoft.com" || uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "login.microsoftonline.com")) OpenUrl(uri.AbsoluteUri);
        else ShowError("微软登录网址无效，请重新获取登录代码。");
    }
    private async void ThirdPartyLogin(object sender, RoutedEventArgs e) => await RunAsync("正在连接认证服务器…", async ct =>
    {
        string password = ThirdPartyPasswordBox.Password;
        ThirdPartyPasswordBox.Clear();
        thirdPartyLogin = await auth.ThirdPartyLoginAsync(ServerBox.Text, ThirdPartyUserBox.Text, password, ct);
        ProfileBox.ItemsSource = thirdPartyLogin.Profiles; ProfileBox.SelectedIndex = 0; ProfilePanel.Visibility = Visibility.Visible;
        StatusText.Text = "认证完成，请选择要使用的游戏角色。";
    });
    private async void UseThirdPartyProfile(object sender, RoutedEventArgs e)
    {
        if (thirdPartyLogin is null || ProfileBox.SelectedItem is not ThirdPartyProfile profile) return;
        await RunAsync("正在选择角色…", async ct =>
        {
            SetAccount(await auth.SelectThirdPartyAsync(thirdPartyLogin, profile, ct));
            thirdPartyLogin = null; ProfilePanel.Visibility = Visibility.Collapsed; StatusText.Text = "第三方账号已选择。"; ShowPage("Home");
        });
    }
    private async void SearchResources(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) await SearchAsync(); }
    private async Task SearchAsync() => await RunAsync("正在搜索资源…", async ct =>
    {
        var results = await resources.SearchAsync(SearchBox.Text.Trim(), ResourceSourceBox.SelectedIndex == 1, store.LoadSecret("curseforge"), ct);
        ResourceItems.ItemsSource = results;
        StatusText.Text = results.Count == 0 ? "没有找到匹配的模组。" : $"找到 {results.Count} 个项目。";
    });
    private void OpenResource(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string link || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        if (uri.Host is "modrinth.com" or "www.curseforge.com" or "curseforge.com") OpenUrl(uri.AbsoluteUri);
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(MemoryBox.Text, out int memory) || memory is < 1024 or > 65536) throw new UserFacingException("内存需为 1024–65536 之间的整数（MB）。");
            if (!Path.IsPathFullyQualified(GamePathBox.Text.Trim())) throw new UserFacingException("请选择游戏文件夹的完整路径。");
            string client = ClientIdBox.Text.Trim();
            if (client.Length > 0 && !Guid.TryParse(client, out _)) throw new UserFacingException("微软 Client ID 应为有效的 GUID。");
            settings.GameDirectory = Path.GetFullPath(GamePathBox.Text.Trim()); settings.JavaPath = JavaPathBox.Text.Trim();
            settings.MemoryMb = memory; settings.MicrosoftClientId = client;
            store.SaveSettings(settings);
            if (!string.IsNullOrWhiteSpace(CurseForgeKeyBox.Password)) store.SaveSecret("curseforge", CurseForgeKeyBox.Password.Trim());
            CurseForgeKeyBox.Clear(); UpdateKeyStatus();
            VersionBox.ItemsSource = null;
            StatusText.Text = "设置已保存。游戏目录改变后，请返回首页刷新版本列表。";
        }
        catch (UserFacingException ex) { ShowError(ex.Message); }
        catch { ShowError("保存设置失败，请检查路径和文件夹权限。"); }
    }
    private void UpdateKeyStatus() => KeyStatus.Text = string.IsNullOrWhiteSpace(store.LoadSecret("curseforge"))
        ? "尚未配置；需填写获批的项目 API Key。" : "本机已加密保存 Key；留空保存将保留它，有效性以实际请求为准。";
    private void ForgetApiKey(object sender, RoutedEventArgs e) { if (operation is not null) return; store.ForgetSecret("curseforge"); CurseForgeKeyBox.Clear(); UpdateKeyStatus(); StatusText.Text = "已清除本机保存的 CurseForge Key。"; }
    private void BrowseGameDirectory(object sender, RoutedEventArgs e)
    { if (operation is not null) return; var dialog = new OpenFolderDialog { Title = "选择游戏文件夹" }; if (dialog.ShowDialog(this) == true) GamePathBox.Text = dialog.FolderName; }
    private void BrowseJava(object sender, RoutedEventArgs e)
    { if (operation is not null) return; var dialog = new OpenFileDialog { Title = "选择 Java", Filter = "Java 程序|java.exe;javaw.exe" }; if (dialog.ShowDialog(this) == true) JavaPathBox.Text = dialog.FileName; }
    private void OpenGameFolder(object sender, RoutedEventArgs e)
    { try { Directory.CreateDirectory(settings.GameDirectory); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { settings.GameDirectory }, UseShellExecute = false }); } catch { ShowError("无法打开游戏文件夹。"); } }
    private void ShowLicenses(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show(this, "CYLT Launcher：MIT\nCmlLib.Core / CmlLib.Core.Commons：MIT\nSharpZipLib / LZMA-SDK NuGet：MIT\nauthlib-injector：AGPL-3.0（含上游例外；运行时按需下载）", "开源许可");
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void OnClosing(object? sender, CancelEventArgs e) => operation?.Cancel();
}
