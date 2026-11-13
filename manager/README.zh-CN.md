# Weims Manager Preview 1.1.0-pre.1

## Windows 快速开始 · 1.1.0-pre.1

> **预发布版：虚拟机可能卡死，包括调整分辨率时。这是已知问题，将在后续更新中解决。请备份重要数据。** 交互软件仍在开发中。本机已验证自动准备并进入 Tahoe 恢复界面，尚未验证从全新安装到桌面的完整流程。

要求：Windows 10 2004+/11 x64、BIOS/UEFI 开启虚拟化、开启 **Windows 虚拟机监控程序平台（WHPX）** 并重启，以及支持 `VK_EXT_external_memory_host` 的 Vulkan 显卡与驱动。在“启用或关闭 Windows 功能”中启用 WHPX。软件不会自动修改 BIOS 或启用 Windows 功能。使用安装包**无需 WSL2、MSYS2、Rust 或 .NET SDK**。需要访问 GitHub、Apple 和上游下载服务器。

### 安装并创建虚拟机

1. 从[预发布页面](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1)下载 `Weims-Manager-1.1.0-pre.1-Setup-x64.exe`，或解压便携 ZIP。安装包与应用均带有 Weims 图标。Preview 与稳定版独立安装；默认英文，可在设置中切换中文。
2. 打开 **Weims Manager Preview → 创建虚拟机**，选择新目录、名称和容量（默认 **200 GiB**）。保留从 Apple 自动下载 Tahoe 恢复镜像，也可选择已有的兼容 `BaseSystem.dmg` / `.img`。不支持完整 ISO 或 ARM macOS。
3. 点击**创建**：自动准备 PowerShell、QEMU/Reims，下载并验证 Apple 恢复镜像，生成具有独立机器身份的 OpenCore，准备 OVMF、配套显卡驱动和 ROM，转换恢复镜像并创建空白磁盘。无需手动操作 WSL、编辑 EFI、复制固件或输入 SMC 密钥。已有目标目录会被拒绝；稀疏磁盘容量不代表已预留实际空间。
4. 启动虚拟机，在 OpenCore 中选择恢复项（隐藏时按**空格**）。进入**磁盘工具 → 显示所有设备**，按容量辨认并仅抹掉**新建的空白系统磁盘**，格式选择 **GUID 分区图 / APFS**。不要抹掉恢复盘或 OpenCore 盘。
5. 退出磁盘工具，选择**重新安装 macOS Tahoe**和新卷，等待下载和多次重启，按安装进度进入安装器或系统。进入桌面后正常关机，停止残留 QEMU 进程，在软件中取消挂载恢复镜像，保存后再次启动。

新建虚拟机默认开启**拖拽自适应分辨率**和自由鼠标。登录后拖动独立 Reims 窗口即可。已有虚拟机需先关机，在**显示与输入**选择自动适配客户机分辨率，关闭无边框模式并保存。启动分辨率范围 **800–4096 × 600–2160**；**2560×1440 仅在本机验证通过**。固定模式缩放完整画面，兼容模式保留旧行为。若调整失去响应，请保留日志，并尽可能正常重启。本版不含剪贴板或文件共享。

### 命令行（安装包和便携版均包含）

在应用安装或解压目录打开 Windows PowerShell。`create` 自动准备同样的组件；省略 `--recovery` 即从 Apple 下载。

```powershell
.\Weims.Cli.exe create --destination D:\VMs\macOS-Tahoe --disk-gib 200
.\Weims.Cli.exe start --config D:\VMs\macOS-Tahoe\state\config.json --display-mode auto
.\Weims.Cli.exe status --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe shutdown --config D:\VMs\macOS-Tahoe\state\config.json
# macOS 已关机后，退出剩余 QEMU 进程：
.\Weims.Cli.exe stop --config D:\VMs\macOS-Tahoe\state\config.json
```

已有 200 GiB 虚拟机直接将其实际 `state/config.json` 传给 `start`，不要重新创建磁盘。Preview 尚无运行环境时先执行 `setup`。GUI 与 CLI 共用 `%LOCALAPPDATA%\Weims\Manager-Preview`，不要重复启动同一磁盘。`stop` 仅允许退出 QMP 确认为已关机的虚拟机；关机请求无响应时请使用 macOS 苹果菜单。

[预发布说明](https://github.com/H0yeR404/weims-vgpu/blob/v1.1.0-pre.1/docs/prerelease-1.1.0-pre.1.zh-CN.md) · [稳定版 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)。稳定版不含自动引导准备或自适应分辨率。
