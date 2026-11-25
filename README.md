# iScreenX

iScreenX 是 Windows x64 上的 AirPlay 镜像接收端，可以在同一局域网内显示 iPhone 画面并播放手机声音。手机通过系统控制中心发起投屏，无需安装 App。已在 iPhone 6 Plus（iOS 10.3.3）上验证。

## 开始投屏

1. 将电脑和 iPhone 接入同一局域网，在电脑上启动 `dist\iScreenX.exe`。
2. 在 iPhone 控制中心打开「屏幕镜像」。iOS 10 中的入口叫「AirPlay 镜像」。
3. 选择电脑等待页显示的 `iScreenX-电脑名`，手机的画面和声音就会传到电脑。

投屏设置支持中等或高清画质、30 或 60 fps。修改后重新连接镜像即可按新参数协商。窗口支持等比缩放、全屏、置顶，也可以在左上角显示实际分辨率和帧率。

屏幕录制目前处于界面预览阶段。越狱设备已接入 SSH 连接，点击和滑动反控仍在开发中。

## 从源码构建

在 Windows x64 上准备 Git 和 Zig 0.13.0，在仓库根目录打开 PowerShell。如果 Zig 已加入 PATH，运行：

```powershell
git submodule update --init
.\build.ps1
```

也可以通过 `-Zig C:\Tools\zig\zig.exe` 指定编译器路径。脚本会优先使用项目内已有的 `tmp/toolchain/ziglang/zig.exe`，使用 .NET Framework 4.x C# 编译器构建主程序，并从 NuGet 获取固定版本的 WebView2 SDK、校验包摘要。默认输出到 `dist`，可通过 `-OutputDirectory` 指定其他目录。

运行时需要 WebView2 Runtime、Bonjour 服务和 Windows OpenSSH 提供的加密库。复制到另一台电脑时，请一并复制输出目录中的 DLL。

## 第三方源码

| 子模块 | iScreenX 使用的部分 |
| --- | --- |
| [3rdparty/UxPlay](3rdparty/UxPlay) | `lib/playfair`，构建为 `iscreenx-fp.dll` |
| [3rdparty/fdk-aac](3rdparty/fdk-aac) | AAC 解码器及其依赖，构建为 `iscreenx-aac.dll` |

主仓库通过 Git 子模块固定这两个组件的版本。构建脚本会将对应的许可证和声明复制到输出目录。
