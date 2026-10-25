using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using CmlLib.Core.Auth;

namespace Cylt.Core;

public enum AccountKind { Offline, Microsoft, ThirdParty }

public sealed record LauncherAccount
{
    public AccountKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public string ClientToken { get; init; } = "";
    public string Xuid { get; init; } = "";
    public string Server { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }

    public static LauncherAccount Offline(string name)
    {
        name = name.Trim();
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9_]{3,16}$"))
            throw new UserFacingException("离线名称需为 3–16 个英文字母、数字或下划线。");
        // Match Java UUID.nameUUIDFromBytes("OfflinePlayer:" + name); MD5 is used only for this protocol identifier.
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new() { Kind = AccountKind.Offline, Name = name, Uuid = Convert.ToHexString(bytes).ToLowerInvariant(), AccessToken = "0" };
    }

    public MSession ToSession() => new() { Username = Name, UUID = Uuid, AccessToken = AccessToken,
        ClientToken = ClientToken, Xuid = Xuid, UserType = Kind switch { AccountKind.Microsoft => "msa", AccountKind.Offline => "legacy", _ => "mojang" } };
}

public sealed class LauncherSettings
{
    public string GameDirectory { get; set; } = Path.Combine(AppPaths.Root, "game");
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public string SelectedVersion { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "bffa5bf0-c390-4fb8-ba0c-df80b88d5d1c";
    public bool IncludeSnapshots { get; set; }
}

public static class AppPaths
{
    public static string Root { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("CYLT_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CYLTLauncher"));
}

public sealed class UserFacingException(string message) : Exception(message);
public sealed record GameVersion(string Id, string Type, bool Installed)
{
    public string Caption => $"{Id}   ·   {(Installed ? "已安装" : Type == "release" ? "正式版" : "快照版")}";
}
public sealed record WorkProgress(string Message, double? Fraction = null);
public sealed record DeviceCodeInfo(string Code, string VerificationUrl);
public sealed record ThirdPartyProfile(string Id, string Name);
public sealed record ThirdPartyResult(string AccessToken, string ClientToken, string Server, IReadOnlyList<ThirdPartyProfile> Profiles);
public sealed record ModResult(string Name, string Summary, string Url, string Author);
