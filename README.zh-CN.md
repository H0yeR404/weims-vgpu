# weims-vgpu

> **开发状态：** Weims Manager 交互软件目前尚不完善，正在持续开发；部分安装准备仍需手动完成，通过 Manager 从恢复镜像安装到桌面的完整流程尚未经过端到端验证。主要准备与安装步骤已包含在本页。

[下载 Weims Manager 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)：Windows 安装包，默认英文、可切换中文，支持检测并自动准备运行环境。

新增 Windows [Weims Manager 1.0.1 配置器](manager/README.zh-CN.md)：图形化配置、恢复镜像准备与独立虚拟机显示窗口。

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

在 Windows 上使用 WHPX 和 Vulkan 运行 Reims 新图形通道。本项目沿用新通道的客户机驱动和协议，通过 Windows section 共享客户机 RAM，解决此前 Windows 移植中观察到的严重回写卡顿。

![Windows 上运行 macOS Tahoe](docs/images/macos-tahoe.png)

截图由仓库所有者提供，展示 macOS Tahoe、8 个 vCPU、32 GiB 内存及约 200 GiB 的 APFS 卷。画面中的 180 Hz 是显示模式信息，并不表示实测达到 180 FPS。

## Windows 快速开始

Weims vGPU 使用 WHPX 和 Vulkan 在 Windows 上原生运行，运行虚拟机**不需要 WSL2**。

### 方案 A：安装 Weims Manager（推荐）

多数用户可直接使用预编译应用。

1. 从[发布页](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)下载 **Weims Manager 1.0.1**。
2. 运行 `Weims-Manager-1.0.1-Setup-x64.exe`。
3. 打开 **Runtime → Check environment（运行环境 → 检测环境）**。
4. 点击 **Prepare / Retry（一键准备 / 重试）**，自动下载并配置 Windows QEMU/Reims 运行环境。

**运行要求：**

- Windows 10 2004 或更新版本 / Windows 11，x64
- BIOS/UEFI 已启用 CPU 虚拟化
- 已启用 Windows Hypervisor Platform（Windows 虚拟机监控程序平台，WHPX）
- Vulkan 显卡及驱动支持 `VK_EXT_external_memory_host`

Manager 自动准备的是 Windows 运行环境；macOS 恢复镜像、兼容的 OpenCore、OVMF 固件和 Reims 图形 ROM 仍需自行提供。界面默认英文，可切换中文。继续按本页“准备引导文件并安装 macOS”操作。

### 方案 B：从源码构建（进阶用户）

适合需要修改 Weims、编译 Windows 图形后端或从源码复现的用户。

**步骤 1 — 安装前置工具**

- [Git for Windows](https://git-scm.com/downloads/win)
- [PowerShell 7](https://github.com/PowerShell/PowerShell/releases)
- [Rustup](https://rustup.rs/)
- [MSYS2](https://www.msys2.org/)

将 MSYS2 安装到 `C:\msys64`，或相应修改下文路径。

**步骤 2 — 配置 MSYS2**

这里使用的是 **Windows 上的 MSYS2 UCRT64，不是 WSL2**。MSYS2 虽然使用 `pacman`，但 `mingw-w64-ucrt-x86_64-*` 是 Windows 构建工具包，不是 Arch Linux 软件包。Debian/Ubuntu 的 `apt`、Arch 的 `pacman` 和 Fedora 的 `dnf` 均不能替代这一步的 Windows 原生构建环境。

从 **Windows 开始菜单 → MSYS2 UCRT64** 打开终端，或在 **Windows PowerShell** 中运行：

```powershell
& 'C:\msys64\msys2_shell.cmd' -defterm -here -no-start -ucrt64
```

在新打开的终端中执行 `echo "$MSYSTEM"`，确认输出为 `UCRT64`；否则请先打开正确的终端。下面的命令必须在 **UCRT64 终端内**执行，不要直接粘贴到 PowerShell、命令提示符、Git Bash 或 WSL 终端：

```sh
pacman -Syu
```

若提示重启终端，重启后再次运行更新。然后安装构建工具：

```sh
pacman -S --needed git make diffutils bison flex mtools \
  mingw-w64-ucrt-x86_64-{clang,llvm,llvm-tools,lld,cmake,meson,ninja,python,glib2,pixman,pkgconf,libslirp,libusb,SDL2,vulkan-headers,vulkan-loader,vulkan-validation-layers,spirv-tools}
```

**步骤 3 — 下载并构建 Weims**

返回 **Windows PowerShell 7** 执行以下命令。`scripts/build.ps1` 会按需调用 MSYS2，目标是 Windows 可执行程序，并非 Linux 构建脚本：

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu

$env:WEIMS_MSYS2 = "C:\msys64"

rustup toolchain install nightly-2026-11-03-x86_64-pc-windows-gnu --profile minimal

pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
```

脚本会下载固定版本的 Reims、QEMU 和 metal2vulkan，校验并应用 Windows 兼容补丁，构建原生 Windows QEMU/Reims 组件。

构建输出：

```text
build/port-qemu/
build/port-dist/
```

对应的图形 ROM：

```text
sources/reims/firmware/prebuilt/ReimsGraphics.rom
```

**步骤 4 — 准备并启动虚拟机**

构建运行环境不会自动创建 macOS 虚拟机。仍需兼容的 OpenCore、OVMF，以及 macOS 恢复镜像或已有的可启动系统盘。Manager 不支持导入完整 ISO。继续按下文准备引导文件，然后使用 Manager 创建新虚拟机，或通过命令行启动已有系统盘。

### 准备引导文件并安装 macOS

准备顺序为 **BaseSystem.dmg → OpenCore.img → OVMF → ReimsGraphics.rom → Weims Manager → 安装 macOS**。这些是独立输入，并非同一镜像的连续转换。需要兼容的 Intel/x86-64 macOS 版本；此方案不转译 Apple Silicon/ARM macOS。

**步骤 1 — 获取 BaseSystem.dmg**

获取 Apple 提供的目标 macOS 版本恢复镜像。[OpenCorePkg 的 macrecovery 工具](https://github.com/acidanthera/OpenCorePkg/tree/master/Utilities/macrecovery)可从 Apple 下载，请按照所选 OpenCore 发行版附带的说明操作。该工具需要 Python，请求中的机型/主板标识会影响实际返回的恢复版本。

保留 `BaseSystem.dmg` 及附带的校验文件。Manager 会将 DMG 转换为原始 `BaseSystem.img`，也接受已转换的 raw `.img`；修改 DMG 或 ISO 扩展名不等于转换。恢复系统需联网从 Apple 下载安装文件。Manager 只检查基本镜像结构，不验证附带的 Apple chunklist，也不保证所有恢复版本兼容。

**步骤 2 — 准备 OpenCore 和客户机驱动**

先准备适用于 x86-64 QEMU 的 OpenCore 引导镜像；测试基线为 **OpenCore 1.0.8**。从 [OpenCorePkg 发布页](https://github.com/acidanthera/OpenCorePkg/releases)获取程序及配置文档。其 ZIP 不是可直接启动的 Weims 磁盘。虚拟机基础配置和机器身份仍需手动准备，Manager 不会生成这些内容。

下列操作要求镜像为 **raw FAT 文件系统镜像**，包含 `EFI/BOOT`、`EFI/OC` 及可用的 `EFI/OC/config.plist`。请使用已关机虚拟机的副本。新通道需要配套的 **Graft、ReimsGraphics 和 RgfxGate** 客户机驱动。

方案 B 已获取这些文件。Manager 用户也可安装前文所列 Git、PowerShell 7 和 MSYS2，然后在 **PowerShell 7** 中执行以下命令，无需编译宿主运行环境：

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu
$env:WEIMS_MSYS2 = 'C:\msys64'
pwsh -File scripts/fetch.ps1
```

如果方案 B 已填充 `sources/`，请跳过重复获取；脚本会拒绝已有源码目录。客户机文件必须与所选宿主运行包匹配，不要混用无关的上游预编译版本。

在 **MSYS2 UCRT64** 中按前文更新软件包，再安装准备工具：

```sh
pacman -S --needed git diffutils mtools mingw-w64-ucrt-x86_64-python
```

在该终端进入仓库根目录，替换示例输入路径后创建并修改副本：

```sh
mkdir -p vm/boot-work
cp /c/VM-inputs/OpenCore.img vm/boot-work/OpenCore.img
mcopy -s -i vm/boot-work/OpenCore.img ::/EFI vm/boot-work/
/ucrt64/bin/python.exe scripts/prepare-opencore.py vm/boot-work/EFI
```

使用 OpenCore 1.0.8 的 `ocvalidate` 验证 `vm/boot-work/EFI/OC/config.plist`。例如，在仓库根目录的 **PowerShell 7** 中执行，并按实际解压位置修改工具路径：

```powershell
& 'C:\Tools\OpenCore-1.0.8-RELEASE\Utilities\ocvalidate\ocvalidate.exe' `
  'vm/boot-work/EFI/OC/config.plist'
```

验证通过后，返回 **MSYS2 UCRT64**，将 EFI 目录写回：

```sh
mcopy -o -s -i vm/boot-work/OpenCore.img vm/boot-work/EFI ::/
```

辅助脚本会按测试配置禁用 Apple Secure Boot，并拒绝预期之外的已启用 kext。遇到拒绝时应检查依赖，不要为了通过检查而删除无关条目。上述 mtools 命令不适用于带分区表的 EFI 磁盘，需要提取或挂载 EFI 分区。QCOW2 引导盘应先转换副本：`qemu-img convert -f qcow2 -O raw source.qcow2 OpenCore.img`；转换不会移除分区表。

**步骤 3 — 提供 OVMF 和 ReimsGraphics.rom**

准备来自同一固件构建、适用于 x86-64 QEMU 的 `OVMF_CODE.fd` 和 `OVMF_VARS.fd`，也可使用已关机的兼容虚拟机副本。[TianoCore EDK II / OvmfPkg](https://github.com/tianocore/edk2/tree/master/OvmfPkg)提供上游源码及构建说明。CODE 包含固件，VARS 保存可写 UEFI 设置。不要混用不同固件布局，也不要让运行中的虚拟机共享可写 VARS。

使用与客户机驱动及宿主运行环境匹配的 ROM：

```text
sources/reims/firmware/prebuilt/ReimsGraphics.rom
```

ROM 不能替代 OpenCore 中的客户机驱动。请保存在固定路径：Manager 会复制 OpenCore 和 OVMF，但**直接引用 ROM 原路径**。

**步骤 4 — 在 Manager 中创建新虚拟机**

完成方案 A 的环境检查后，打开恢复安装创建向导，填写：

| 字段 | 内容 |
| --- | --- |
| 虚拟机名称 | 例如 `macOS-Tahoe`；1–48 个 ASCII 字母、数字、下划线或短横线，以字母或数字开头 |
| 恢复镜像 | `BaseSystem.dmg` 或 raw `BaseSystem.img` |
| 存储父目录 | 已存在的本地目录；向导会在其中创建以虚拟机命名的子目录 |
| 系统盘容量 | 例如 `200` GiB；支持 64–2048 GiB |
| OpenCore | 准备好的 `vm/boot-work/OpenCore.img` |
| 固件 CODE / VARS | 配套的 `OVMF_CODE.fd` 和 `OVMF_VARS.fd` |
| 图形 ROM | 配套 `ReimsGraphics.rom` 的固定路径 |
| QMP / SSH 端口 | 不同且未被占用的本机端口；向导默认 `4455` / `2233` |

最终虚拟机目录不能已经存在。点击 **Create（创建）**，等待镜像转换和磁盘创建。稀疏系统盘会随 macOS 写入逐渐增长，需为安装预留足够的宿主物理存储空间。失败或取消后会保留临时准备目录供检查。

准备完成后仅注册虚拟机，不自动启动。检查 CPU 和内存：恢复向导默认 8 vCPU、8 GiB 内存和 `1920x1080`。按宿主能力调整资源，首次启动保留 `1920x1080`，并保持恢复镜像挂载。Manager 中不需要输入 SMC 密钥。

**步骤 5 — 在虚拟机中安装 macOS**

1. 启动虚拟机，QEMU/Reims 会打开独立显示窗口。
2. 在 OpenCore 中选择恢复系统；辅助启动项隐藏时按**空格**显示。
3. 打开**磁盘工具 → 显示 → 显示所有设备**，按容量识别新建的空白系统盘，**仅抹掉该盘**，选择 **GUID 分区图**和 **APFS**，例如命名为 `Macintosh HD`。不要抹掉 OpenCore 或恢复介质。
4. 退出磁盘工具，选择安装 macOS 和新建系统卷。等待下载及重启完成；重启后继续进入安装器启动项，随后进入已安装系统，不要每次重启都重新开始恢复安装。
5. 进入桌面并完成设置助理后，从 Apple 菜单正常关机。QEMU 停止后，在 Manager 中关闭恢复镜像挂载、保存并再次启动，确认系统可独立启动。保留 OpenCore、OVMF 和 ROM。

如果启动失败，先保存虚拟机 `logs` 目录并记录错误，再修改输入文件。优先检查格式及驱动/运行环境版本是否匹配。[首次安装指南（英文）](docs/first-install.md)提供补充解释和排错表；主要流程已包含在本页。

### 从命令行启动已有虚拟机

完成方案 B 和上述引导文件准备后，关闭原虚拟机，创建独立系统盘副本。在**仓库根目录的 PowerShell 7** 中执行，并替换示例输入路径：

```powershell
$env:WEIMS_MSYS2 = 'C:\msys64'
$env:PATH = "$env:WEIMS_MSYS2\ucrt64\bin;$env:PATH"
New-Item -ItemType Directory -Force vm/disks
./build/port-qemu/qemu-img.exe convert -p -f qcow2 -O qcow2 `
  C:/VMs/original/current.qcow2 vm/disks/current.qcow2
pwsh -File scripts/init-vm.ps1 -Disk ./vm/disks/current.qcow2 `
  -OpenCore ./vm/boot-work/OpenCore.img `
  -FirmwareCode C:/VMs/original/OVMF_CODE.fd `
  -FirmwareVars C:/VMs/original/OVMF_VARS.fd
$env:WEIMS_OSK = Read-Host 'SMC key from your own authorized installation' -MaskInput
pwsh -File scripts/start.ps1 -DryRun
pwsh -File scripts/start.ps1
```

目标磁盘必须使用新路径，不要覆盖需要保留的已有磁盘。此例要求源盘为 QCOW2，转换会合并其 backing chain 并保持逻辑容量。`init-vm.ps1` 会直接写入所选系统盘，并拒绝已有的 `vm/default` 目录。启动前检查 `vm/default/state/config.json`：命令行默认 8 vCPU、32 GiB 内存、`1920x1080`、QMP `4444`、SSH `2222`。调整资源并避免端口冲突。与 Manager 不同，命令行要求设置 `WEIMS_OSK`；保存的命令日志会隐藏该值，但 QEMU 的进程参数仍会接收它。

按 **Ctrl+Alt+Win** 或 **Ctrl+Alt+Home** 释放捕获的输入。先在 macOS 内正常关机；若之后 QEMU 仍未退出，可执行 `pwsh -File scripts/start.ps1 -Exit` 关闭模拟器，这不能代替正常关机。不要让两个实例同时打开同一可写磁盘。

### 可选：使用 WSL2 工具检查或准备 EFI 文件

按前文 Windows 流程操作时可跳过本节。WSL2 可提供检查文件、处理 OpenCore raw FAT 镜像副本的 Linux 工具；下面的软件包**不能替代 Windows QEMU/Reims 构建环境**，也不会启用 WHPX 或安装 Windows Vulkan 驱动。

如果已有 WSL2，在 **Windows PowerShell** 中列出已安装发行版并进入其中一个。将 `Ubuntu` 替换为列表中的准确名称，例如 `Debian`、`Arch` 或实际显示的 Fedora 名称：

```powershell
wsl --list --verbose
wsl -d Ubuntu
```

确认列表中目标发行版的 WSL 版本为 `2`。进入 Linux 终端后，用 `cat /etc/os-release` 确认发行版，**仅执行与该发行版对应的一组命令**。以下假设用户具有 `sudo` 权限；若当前是 root，可省略 `sudo`。

**Debian / Ubuntu — 在 WSL2 内执行：**

```sh
sudo apt update
sudo apt install git python3 mtools
```

**Arch Linux — 在 WSL2 内执行：**

```sh
sudo pacman -Syu --needed git python mtools
```

**Fedora — 在 WSL2 内执行：**

```sh
sudo dnf install git python3 mtools
```

EFI 辅助脚本因使用 `tomllib` 而要求 **Python 3.11 或更新版本**。执行 `python3 --version` 和 `python3 -c 'import tomllib'` 检查；若不满足，使用提供 Python 3.11+ 的发行版版本，或改用前文 MSYS2 准备流程。

不同终端中的路径也不同：Windows 的 `C:\VM-inputs\OpenCore.img` 在 MSYS2 中是 `/c/VM-inputs/OpenCore.img`，在 WSL2 中是 `/mnt/c/VM-inputs/OpenCore.img`。使用 WSL2 时先进入实际仓库路径，例如 `cd /mnt/d/Coding/weims-vgpu`，将 EFI 辅助命令改为 `python3 scripts/prepare-opencore.py vm/boot-work/EFI`，不要使用 `/ucrt64/bin/python.exe ...`。此方式仅用于替代镜像副本的 EFI 准备步骤，并要求源码已获取；不要对同一副本重复执行两套准备流程。Windows `ocvalidate`、构建及虚拟机启动仍在 Windows PowerShell 中执行。输入 `exit` 退出 WSL2。

### 备注

- WSL2 为可选组件，不能替代本原生 Windows 构建所需的 MSYS2。
- Reims 客户机驱动必须与宿主运行版本匹配。源码构建的自由鼠标支持见[显示与自由鼠标说明（英文）](manager/README.md#display-and-free-pointer)。
- 其他显卡和 Windows 配置尚未经过广泛验证。
- 修改前备份已有虚拟机磁盘和固件。
- macOS 使用仍受 Apple 软件许可条款约束。

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

基于 [Reims vGPU](https://github.com/steelbrain/reims-vgpu)、[metal2vulkan](https://github.com/steelbrain/metal2vulkan)、[QEMU Reims](https://github.com/steelbrain/qemu-reims-vgpu) 以及 [tqmane 的 Windows 移植](https://github.com/tqmane/reims-vgpu)。本项目不是上游官方发行版；

Reims 和 metal2vulkan 保留 `GPL-2.0-or-later OR LGPL-3.0-or-later` 双许可证；QEMU 保留各文件原有许可证。集成脚本和文档采用 GPL-2.0-or-later。详见 [LICENSE](LICENSE)、[署名与来源](NOTICE.md) 和 [完整许可证文本](licenses/)。截图中的 Apple 软件、商标及第三方内容不由本仓库授予许可。
