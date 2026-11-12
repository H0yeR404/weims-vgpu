# weims-vgpu

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

在 Windows 上使用 WHPX 和 Vulkan 运行 Reims 新图形通道。本项目沿用新通道的客户机驱动和协议，通过 Windows section 共享客户机 RAM，解决此前 Windows 移植中观察到的严重回写卡顿。

![Windows 上运行 macOS Tahoe](docs/images/macos-tahoe.png)

截图由仓库所有者提供，展示 macOS Tahoe、8 个 vCPU、32 GiB 内存及约 200 GiB 的 APFS 卷。画面中的 180 Hz 是显示模式信息，并不表示实测达到 180 FPS。

## 构建与启动

[完整复现指南](docs/reproduce.md) 提供工具安装、固定源码版本、补丁应用、编译、客户机驱动注入和启动步骤。仓库发布源码补丁和脚本，不提供 macOS 系统盘或宿主预编译程序。需要自行准备可启动的 macOS 虚拟机及 OpenCore、OVMF 文件。

```powershell
git clone https://github.com/H0yeR404/weims-vgpu
cd weims-vgpu
$env:WEIMS_MSYS2 = 'C:\msys64'
# 先按 docs/reproduce.md 安装工具链。
pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
```

编译后，在原虚拟机关机的情况下复制系统盘；将新通道的 Graft、ReimsGraphics、RgfxGate 注入 OpenCore 副本，再运行 `scripts/init-vm.ps1` 生成配置，使用 `scripts/start.ps1` 启动。完整参数见复现指南。默认 QMP 4444 和 SSH 2222 仅绑定本机，避免与已有虚拟机冲突。新旧实例的独立磁盘不会自动同步。

已测试环境为 Windows x64、WHPX、RTX 4090 Laptop GPU、Vulkan、8 vCPU、32 GiB 内存和 1920×1080。实现通过 22 项 C API 测试；一次 10 秒拖窗测试记录 54.17 个不同画面/秒，最大帧间隔 211 ms。这不是严格同场景的新旧对照，也不代表长期稳定性或所有显卡表现。共享内存仍包含宿主 device cache 和必要复制，并非全程零拷贝。

## 上游与许可证

基于 [Reims vGPU](https://github.com/steelbrain/reims-vgpu)、[metal2vulkan](https://github.com/steelbrain/metal2vulkan)、[QEMU Reims](https://github.com/steelbrain/qemu-reims-vgpu) 以及 [tqmane 的 Windows 移植](https://github.com/tqmane/reims-vgpu)。本项目不是上游官方发行版；修改使用了 AI 辅助。

Reims 和 metal2vulkan 保留 `GPL-2.0-or-later OR LGPL-3.0-or-later` 双许可证；QEMU 保留各文件原有许可证。集成脚本和文档采用 GPL-2.0-or-later。详见 [LICENSE](LICENSE)、[署名与来源](NOTICE.md) 和 [完整许可证文本](licenses/)。截图中的 Apple 软件、商标及第三方内容不由本仓库授予许可。
