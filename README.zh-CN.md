# weims-vgpu

[下载 Weims Manager 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)：Windows 安装包，默认英文、可切换中文，支持检测并自动准备运行环境。

新增 Windows [Weims Manager 1.0.1 配置器](manager/README.zh-CN.md)：图形化配置、恢复镜像准备与独立虚拟机显示窗口。

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

在 Windows 上使用 WHPX 和 Vulkan 运行 Reims 新图形通道。本项目沿用新通道的客户机驱动和协议，通过 Windows section 共享客户机 RAM，解决此前 Windows 移植中观察到的严重回写卡顿。

![Windows 上运行 macOS Tahoe](docs/images/macos-tahoe.png)

截图由仓库所有者提供，展示 macOS Tahoe、8 个 vCPU、32 GiB 内存及约 200 GiB 的 APFS 卷。画面中的 180 Hz 是显示模式信息，并不表示实测达到 180 FPS。

## 构建与启动

[完整复现指南](docs/reproduce.md) 提供工具安装、固定源码版本、补丁应用、编译、客户机驱动注入和启动步骤。仓库发布源码补丁、脚本与带对应源码的宿主运行包，不提供 macOS 系统盘。需要自行准备可启动的 macOS 虚拟机及 OpenCore、OVMF 文件。

```powershell
git clone https://github.com/H0yeR404/weims-vgpu
cd weims-vgpu
$env:WEIMS_MSYS2 = 'C:\msys64'
# 先按 docs/reproduce.md 安装工具链。
pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
```

编译后，在原虚拟机关机的情况下复制系统盘；将新通道的 Graft、ReimsGraphics、RgfxGate 注入 OpenCore 副本，再运行 `scripts/init-vm.ps1` 生成配置，使用 `scripts/start.ps1` 启动。完整参数见复现指南。默认 QMP 4444 和 SSH 2222 仅绑定本机，避免与已有虚拟机冲突。新旧实例的独立磁盘不会自动同步。

## 本机测试环境

> **兼容性备注：** 本项目目前仅在下述本机配置上完成测试并成功运行。其他设备理论上支持，但兼容性及能否正常运行仍需实际测试确认。

| 项目 | 配置 |
| --- | --- |
| 笔记本 | 联想拯救者 Legion Y9000P IRX8H（82WQ） |
| 宿主系统 | Windows 11 专业工作站版，x86_64 |
| Windows 构建 | 10.0.26300.9550（Fastfetch 标识为 26H2） |
| CPU | Intel Core i9-13900HX，32 个逻辑处理器 |
| GPU | NVIDIA GeForce RTX 4090 Laptop GPU，报告显存 15.67 GiB |
| 宿主内存 | 报告可用总量 95.81 GiB |
| 宿主显示器 | CU34G4 外接显示器，3440 × 1440，180 Hz |
| 宿主 Shell | PowerShell 7.6.9 |
| 虚拟化 / 图形 | WHPX / Vulkan，客户机内存共享图形通道 |
| 测试客户机 | macOS Tahoe，8 vCPU，32 GiB 内存，200 GiB 虚拟系统盘 |
| 客户机分辨率 | 1920 × 1080 |

以上是已测试设备，并非最低或推荐配置。宿主显示器刷新率不等于客户机渲染帧率。宿主已有的 PowerShell 版本也不同于自动准备功能在需要时下载的固定 PowerShell 7.7.0 版本。

实现通过 22 项 C API 测试；一次 10 秒拖窗测试记录 54.17 个不同画面/秒，最大帧间隔 211 ms。这不是严格同场景的新旧对照，也不代表长期稳定性或所有显卡表现。共享内存仍包含宿主 device cache 和必要复制，并非全程零拷贝。

## 上游与许可证

基于 [Reims vGPU](https://github.com/steelbrain/reims-vgpu)、[metal2vulkan](https://github.com/steelbrain/metal2vulkan)、[QEMU Reims](https://github.com/steelbrain/qemu-reims-vgpu) 以及 [tqmane 的 Windows 移植](https://github.com/tqmane/reims-vgpu)。本项目不是上游官方发行版；修改使用了 AI 辅助。

Reims 和 metal2vulkan 保留 `GPL-2.0-or-later OR LGPL-3.0-or-later` 双许可证；QEMU 保留各文件原有许可证。集成脚本和文档采用 GPL-2.0-or-later。详见 [LICENSE](LICENSE)、[署名与来源](NOTICE.md) 和 [完整许可证文本](licenses/)。截图中的 Apple 软件、商标及第三方内容不由本仓库授予许可。
