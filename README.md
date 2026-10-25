# CYLT Launcher

An independent Windows launcher for Minecraft Java Edition, with a minimal interface and Microsoft, offline, and third-party account support.

独立开发的 Windows 启动器，采用简约界面。此项目不使用 PCL 或 HMCL 的源码、界面或品牌资产。游戏安装和启动使用 MIT 许可的 CmlLib.Core，第三方账号采用 Yggdrasil / authlib-injector 协议。

**开发中，版本 0.1.0。** 微软登录需要获准访问 Minecraft 服务的应用 ID；当前自有应用仍被服务方拒绝接入。CurseForge 需要项目专属 API Key，尚未取得。保留三个账号入口不代表对应外部服务已经获批。

已在 Windows 上验证原版 Minecraft 26.3 的下载、匹配 Java 的安装和实际启动。第三方登录目前通过模拟服务测试，尚未使用真实第三方账号验证。当前资源页提供搜索和项目网页入口，暂不包含模组加载器或整合包的一键安装。

## 功能

- 原版版本列表、安装和启动，自动下载对应 Java 运行时。
- 已安装版本管理，支持选择已有游戏目录。
- 正版、离线、第三方账号入口；正版账号必须完成游戏所有权验证。
- 内存与 Java 路径设置，可取消下载任务。
- Modrinth 资源浏览；CurseForge 配置和资源浏览入口。
- 账号令牌和 API Key 使用 Windows DPAPI 加密；不会打包进源码或发行文件。

## 构建

需要 Windows 和 .NET SDK 10：

```powershell
dotnet build src/Cylt.Launcher/Cylt.Launcher.csproj -c Release
dotnet test tests/Cylt.Core.Tests/Cylt.Core.Tests.csproj
```

生成包含 .NET 运行时的 Windows x64 安装包：

```powershell
powershell -ExecutionPolicy Bypass -File packaging/build.ps1
```

构建脚本使用 WiX 6.0.2，首次运行会从 NuGet 安装工具。结果位于 `release/CYLT-Launcher-0.1.0-preview-x64.msi`，校验值位于 `release/SHA256SUMS.txt`。这是一份开发预览版。

默认数据目录为 `%LOCALAPPDATA%\CYLTLauncher`。游戏文件由各自服务提供，安装包不包含 Minecraft、用户账号、世界或模组。

## 服务与版权

本项目不是 Mojang、Microsoft、CurseForge 或其他启动器的官方产品。Microsoft 和 CurseForge 的接入申请需由服务方审核；修改程序名称不会改变其接入条件。第三方账号凭据只发送到使用者填写并确认的认证服务器。

独立代码使用 MIT 许可。依赖与对应许可见 `THIRD-PARTY-NOTICES.txt`。
