using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CmlLib.Core;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.VersionLoader;

namespace Cylt.Core;

public sealed class GameService(HttpClient http)
{
    public async Task<IReadOnlyList<GameVersion>> GetVersionsAsync(LauncherSettings settings, CancellationToken ct)
    {
        var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(new MinecraftPath(settings.GameDirectory), http));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        CmlLib.Core.VersionMetadata.VersionMetadataCollection versions;
        try { versions = await launcher.GetAllVersionsAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var local = GetLocalVersions(settings);
            if (local.Count > 0) return local;
            throw new UserFacingException("读取官方版本列表超时，请检查网络后刷新。");
        }
        return versions.Where(v => settings.IncludeSnapshots || v.Type == "release" || IsInstalled(settings, v.Name))
            .Select(v => new GameVersion(v.Name, v.Type ?? "custom", IsInstalled(settings, v.Name))).ToArray();
    }

    public static bool IsInstalled(LauncherSettings settings, string version) =>
        File.Exists(Path.Combine(settings.GameDirectory, "versions", version, version + ".json")) &&
        File.Exists(Path.Combine(settings.GameDirectory, "versions", version, version + ".jar")) &&
        !File.Exists(Path.Combine(settings.GameDirectory, "versions", version, ".cylt-installing"));

    public static IReadOnlyList<GameVersion> GetLocalVersions(LauncherSettings settings)
    {
        var root = Path.Combine(settings.GameDirectory, "versions");
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root).Select(Path.GetFileName).Where(v => v is not null)
            .Where(v => File.Exists(Path.Combine(root, v!, v + ".json")))
            .Select(v => new GameVersion(v!, "local", IsInstalled(settings, v!))).ToArray();
    }

    public async Task InstallAsync(LauncherSettings settings, string version, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        ValidateVersion(version);
        var launcher = CreateLauncher(settings, progress);
        string marker = Path.Combine(settings.GameDirectory, "versions", version, ".cylt-installing");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "Installation is incomplete until this file is removed.", ct);
        await launcher.InstallAsync(version, ct);
        File.Delete(marker);
    }

    public async Task<Process> BuildProcessAsync(LauncherSettings settings, string version, LauncherAccount account,
        bool repair, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        if (settings.MemoryMb is < 1024 or > 65536) throw new UserFacingException("内存需在 1024–65536 MB 之间。");
        ValidateVersion(version);
        if (repair) await InstallAsync(settings, version, progress, ct);
        var launcher = CreateLauncher(settings, progress, localOnly: true);
        var option = new MLaunchOption
        {
            Session = account.ToSession(), MaximumRamMb = settings.MemoryMb, MinimumRamMb = Math.Min(1024, settings.MemoryMb),
            JavaPath = string.IsNullOrWhiteSpace(settings.JavaPath) ? null : settings.JavaPath,
            GameLauncherName = "CYLTLauncher", GameLauncherVersion = "0.1.0", ScreenWidth = 1280, ScreenHeight = 720
        };
        if (option.JavaPath is not null && !File.Exists(option.JavaPath)) throw new UserFacingException("指定的 Java 路径不存在，请在设置中重新选择。");
        if (account.Kind == AccountKind.ThirdParty)
        {
            var server = AuthenticationService.ValidateServer(account.Server);
            var injector = await EnsureInjectorAsync(ct);
            option.ExtraJvmArguments = option.ExtraJvmArguments.Concat([new MArgument($"-javaagent:{injector}={server.AbsoluteUri}")]);
        }
        progress.Report(new("正在准备启动参数…"));
        var process = await launcher.BuildProcessAsync(version, option, ct);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        return process;
    }

    private MinecraftLauncher CreateLauncher(LauncherSettings settings, IProgress<WorkProgress> progress, bool localOnly = false)
    {
        Directory.CreateDirectory(settings.GameDirectory);
        var parameters = MinecraftLauncherParameters.CreateDefault(new MinecraftPath(settings.GameDirectory), http);
        if (localOnly) parameters.VersionLoader = new LocalJsonVersionLoader(parameters.MinecraftPath!);
        var launcher = new MinecraftLauncher(parameters);
        long last = 0;
        launcher.FileProgressChanged += (_, e) =>
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref last) < 120 && e.ProgressedTasks < e.TotalTasks) return;
            Interlocked.Exchange(ref last, now);
            progress.Report(new($"正在准备游戏文件  {e.ProgressedTasks:N0} / {e.TotalTasks:N0}",
                e.TotalTasks > 0 ? (double)e.ProgressedTasks / e.TotalTasks : null));
        };
        return launcher;
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version is "." or ".." || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UserFacingException("游戏版本标识无效。");
    }

    private async Task<string> EnsureInjectorAsync(CancellationToken ct)
    {
        var metadata = JsonNode.Parse(await http.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json", ct))!;
        var uri = new Uri(metadata["download_url"]!.GetValue<string>());
        if (uri.Scheme != "https" || uri.Host != "authlib-injector.yushi.moe")
            throw new UserFacingException("第三方认证组件下载地址异常。");
        string hash = metadata["checksums"]?["sha256"]?.GetValue<string>() ?? "";
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new UserFacingException("第三方认证组件缺少有效校验值。");
        var directory = Path.Combine(AppPaths.Root, "components");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "authlib-injector-" + hash[..12] + ".jar");
        if (File.Exists(path) && Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, ct))).Equals(hash, StringComparison.OrdinalIgnoreCase))
            return path;
        var bytes = await http.GetByteArrayAsync(uri, ct);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException("第三方认证组件校验失败，已停止使用。");
        await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
        File.Move(path + ".tmp", path, true);
        return path;
    }
}
