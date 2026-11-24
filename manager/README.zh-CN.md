# Weims Manager 1.2.0-pre.1

## Windows 快速开始 · 1.2.0-pre.1

> **已知问题：虚拟机可能卡死，包括调整分辨率时。这是已知问题，将在后续更新中解决。请备份重要数据。** 交互软件仍在开发中。本机已验证自动准备并进入 Tahoe 恢复界面，尚未验证从全新安装到桌面的完整流程。

要求：Windows 10 2004+/11 x64、BIOS/UEFI 开启虚拟化、开启 **Windows 虚拟机监控程序平台（WHPX）** 并重启，以及支持 Vulkan 1.3 的显卡与驱动。在“启用或关闭 Windows 功能”中启用 WHPX。软件不会自动修改 BIOS 或启用 Windows 功能。使用安装包**无需 WSL2、MSYS2、Rust 或 .NET SDK**。需要访问 GitHub、Apple 和上游下载服务器。

### 安装并创建虚拟机

1. 从[预发布页面](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.2.0-pre.1)下载 `Weims-Manager-1.2.0-pre.1-Setup-x64.exe`，或解压便携 ZIP。安装包与应用均带有 Weims 图标。预览版以 Weims Manager Preview 的名字安装，与正式版并存。默认英文和浅色，侧栏可即时切换语言与外观。
2. 打开 **Weims Manager → 创建虚拟机**，选择新目录、名称和容量（默认 **200 GiB**）。保留从 Apple 自动下载 Tahoe 恢复镜像，也可选择已有的兼容 `BaseSystem.dmg` / `.img`。不支持完整 ISO 或 ARM macOS。
3. 点击**创建**：自动准备 PowerShell、QEMU/Reims，下载并验证 Apple 恢复镜像，生成具有独立机器身份的 OpenCore，准备 OVMF、配套显卡驱动和 ROM，转换恢复镜像并创建空白磁盘。无需手动操作 WSL、编辑 EFI、复制固件或输入 SMC 密钥。已有目标目录会被拒绝；稀疏磁盘容量不代表已预留实际空间。
4. 启动虚拟机，在 OpenCore 中选择恢复项（隐藏时按**空格**）。进入**磁盘工具 → 显示所有设备**，按容量辨认并仅抹掉**新建的空白系统磁盘**，格式选择 **GUID 分区图 / APFS**。不要抹掉恢复盘或 OpenCore 盘。
5. 退出磁盘工具，选择**重新安装 macOS Tahoe**和新卷，等待下载和多次重启，按安装进度进入安装器或系统。进入桌面后正常关机（虚拟机窗口会自动关闭），在软件中取消挂载恢复镜像，保存后再次启动。

macOS 的分辨率跟随虚拟机窗口：拖动窗口边缘，画面随之填满新的大小。窗口按**显示与输入**里设置的启动分辨率打开（**800–4096 × 600–2160**）。新建虚拟机默认开启自由鼠标：鼠标可直接移进移出窗口，Windows 快捷键照常可用。关闭后，点击窗口会捕获键盘和鼠标，按 **Ctrl+Alt+Win** 释放；需要相对鼠标的游戏使用这种模式。macOS 有声音输出。剪贴板共享需要 Guest Tools（见下文）；不含文件共享。

### 命令行（安装包和便携版均包含）

在应用安装或解压目录打开 Windows PowerShell。`create` 自动准备同样的组件；省略 `--recovery` 即从 Apple 下载。

```powershell
.\Weims.Cli.exe create --destination D:\VMs\macOS-Tahoe --disk-gib 200
.\Weims.Cli.exe start --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe status --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe shutdown --config D:\VMs\macOS-Tahoe\state\config.json
```

已有 200 GiB 虚拟机直接将其实际 `state/config.json` 传给 `start`，不要重新创建磁盘。用早先版本创建的虚拟机，第一次启动时会重新制作引导镜像，换上当前运行组件的图形驱动，Mac 身份保持不变；原镜像保留在旁边，名为 `OpenCore.img.previous`。当前设置尚无运行环境时先执行 `setup`。GUI 与 CLI 共用设置目录：优先使用更新器保留的自定义目录；否则复用已有的 `%LOCALAPPDATA%\Weims\Manager-Preview`，全新正式版使用 `%LOCALAPPDATA%\Weims\Manager`。可通过 `--data-dir` 指定其他目录。不要重复启动同一磁盘。`shutdown` 请求 macOS 退出所有应用并关机，有未保存的文档时 macOS 会先询问；客户机关机后 QEMU 自动退出。`stop` 只用于 1.1.0 及更早版本启动、关机后仍残留的 QEMU。

[1.2.0-pre.1 发布说明](https://github.com/H0yeR404/weims-vgpu/blob/v1.2.0-pre.1/docs/prerelease-1.2.0-pre.1.zh-CN.md)。本版配套运行组件 1.2.0-pre.1，虚拟机关闭后 QEMU 立即退出。

## 外观、语言与软件更新

在侧栏选择 **外观 → 浅色 / 深色**，当前窗口与已打开的安装向导立即生效，选择会保存，无需重启，也不会丢失未保存的配置。Windows 高对比度设置优先。默认语言仍为英文，中英文也可即时切换。

打开 **软件更新 → 检查更新**，查看更新说明后点击 **一键更新**。软件下载官方安装包并校验 SHA256。安装前需关闭所有 QEMU 虚拟机和其他 Manager 窗口；只有软件升级需要重启管理器。预览版会收到更新的预览版和正式版。正式版只接收正式版，所以 1.1.1 用户需手动安装预览版。

更新器会保留所选设置目录，以后 GUI 和 CLI 均继续使用。便携版更新后安装到当前用户应用目录，不替换图形运行库或虚拟机磁盘。旧 Preview 应用不会自动卸载，升级后请使用正式版 Weims Manager 快捷方式。

## 一键准备运行环境

首次使用直接进入运行环境页面。**一键准备**下载、校验并保存全部路径，已有有效环境直接复用。如果由管理器安装的运行组件比当前版本配套的旧，运行环境页面会提示，再点一次**一键准备**即可换成新的；自行指定的运行环境不会被替换。手动路径仍在**高级设置**中。启动导入的虚拟机时自动准备缺失组件；BIOS 虚拟化、Windows 功能和显卡驱动仍可能需要手动处理。

## Guest Tools：剪贴板和共享文件夹

虚拟机窗口在前台时，Guest Tools 在 Windows 和 macOS 之间共享剪贴板文本；它还把这台电脑上的文件夹挂载到 macOS 里。

1. 启动虚拟机并登录 macOS。
2. 在管理器里打开**为此虚拟机开启 Guest Tools 集成**，点击**安装 Guest Tools…**。macOS 里会出现一张安装盘。
3. 在 macOS 里打开安装盘，双击 **Install Weims Guest Tools**。它为当前用户安装，与这台虚拟机配对，然后弹出安装盘。不需要输入任何东西。

在一边复制，到另一边粘贴。文本上限 4 MiB；密码管理器标记为敏感的内容，到另一边后不会进入剪贴板历史。安装了更新的管理器后，macOS 里的 Guest Tools 会自动更新。要移除它，运行安装盘里的 **Uninstall Weims Guest Tools.command**。命令行有同样的功能：`tools-enable`、`tools-install`、`tools-status`。

### 共享文件夹

在 **Guest Tools → 共享文件夹**里点击**共享文件夹…**，选择这台电脑上的一个文件夹。虚拟机运行期间，Guest Tools 会把它挂载到 macOS 的 `/Volumes` 下，并在 macOS 个人文件夹里的 **Weims Shared Folders** 中放一个指向它的链接；菜单栏里 Guest Tools 的菜单也能打开它。共享之后大约一秒，文件夹就出现在那里。共享文件夹不显示在访达的侧栏里：侧栏只会显示它们的服务器地址，点进去并没有这些文件夹。勾选**只读**后，macOS 不能修改这个文件夹。虚拟机运行时可以修改这个列表，文件夹会短暂断开再回来。

共享文件夹用来交换文档、安装包和源文件。它不是 macOS 磁盘：不支持符号链接，不要把应用或 Xcode 工程放在里面。访达浏览过的文件夹里会留下 `.DS_Store` 文件。共享文件夹里指向文件夹之外的链接，macOS 打不开。macOS 里的应用第一次打开共享文件夹时，系统会询问是否允许它访问网络卷上的文件。

这项功能不改动 Windows：不创建 Windows 共享，不需要管理员权限，不动防火墙。文件夹由管理器自带的组件在本机回环地址上提供，用户名和密码每次重新生成。

**上传限速**是 macOS 每秒最多往共享文件夹写入的数据量。默认是 0，即不限；如果 macOS 写大文件时虚拟机画面卡顿，可以设一个上限。命令行：`tools-share --folder D:\Work [--name Work] [--access read]`、`tools-unshare --name Work`、`tools-upload-limit --mib 12`。
