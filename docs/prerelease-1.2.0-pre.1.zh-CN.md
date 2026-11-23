# Weims Manager 1.2.0-pre.1

1.2 的预览版，包含 Windows 图形界面和命令行、macOS 端的 Guest Tools，以及新的 QEMU/Reims 运行组件。正式版 1.1.1 仍是 Latest。

## 新内容

- **Guest Tools：剪贴板共享。** 虚拟机窗口在前台时，在 Windows 复制的文本可以在 macOS 粘贴，反过来也一样。文本上限 4 MiB。密码管理器标记为敏感的内容，到另一边后不会进入剪贴板历史。
- **从管理器安装。** 打开**为此虚拟机开启 Guest Tools 集成**，点击**安装 Guest Tools…**，macOS 里会出现一张安装盘，双击其中的 **Install Weims Guest Tools**。它为当前 macOS 用户安装，与这台虚拟机配对，然后自动弹出安装盘。不需要输入任何东西，也不用核对数字。
- **Guest Tools 自动更新。** 新版管理器带有更新的 Guest Tools 时，macOS 里的 Guest Tools 通过它与主机的连接收到新版并重启。它只接受与自己签名身份相同的版本。
- **运行组件 1.2.0-pre.1：QEMU 立即退出。** 旧运行组件下，关闭窗口或 macOS 关机后，QEMU 可能残留几分钟，这段时间里虚拟机一直被锁住。原因是退出时运行组件在等待已经被 Windows 结束的图形线程，每次等 2 秒，每个登记的处理函数等一次。现在它能发现这些线程已经不在，不再等待。
- **管理器替换过旧的运行组件。** 由管理器安装的运行组件比当前版本配套的旧时，启动时和“运行环境”页面会提示，点**一键准备**即可在旁边装好新版并切换过去。自行指定的运行环境不会被替换。启动虚拟机时不会下载任何东西。
- 管理器、命令行和 Guest Tools 的主机端进程基于 .NET 10 构建。安装包自带运行时，不需要另外安装。

命令行有同样的功能：`tools-enable`、`tools-disable`、`tools-install`、`tools-status`、`tools-pair`、`tools-reset-pairing`。

## 安装与升级

下载 `Weims-Manager-1.2.0-pre.1-Setup-x64.exe`，或解压 `Weims-Manager-1.2.0-pre.1-win-x64.zip`。预览版以 **Weims Manager Preview** 的名字安装，与正式版并存，共用同一份设置和虚拟机。

正式版只接收正式版更新，所以 1.1.1 用户需要手动下载安装这个预览版。已安装的预览版可在**软件更新 → 检查更新**中找到它。

安装后打开**运行环境**，点**一键准备**获取运行组件 1.2.0-pre.1（95 MB）。已有虚拟机不需要改动。正在运行的虚拟机在重启之前仍使用启动时的运行组件。

要从 macOS 移除 Guest Tools，运行安装盘里的 **Uninstall Weims Guest Tools.command**。

## Guest Tools 如何连接

集成默认关闭，按虚拟机单独开关。开启后，主机上有一个小进程（`Weims.Broker`）只在本机回环地址上监听，macOS 通过虚拟机自己的网络连到它。连接使用 TLS，证书为这台虚拟机单独生成，Guest Tools 在配对时记住它。QEMU 的启动参数不增加任何内容，虚拟机也拿不到 QEMU 控制通道的访问权。

Guest Tools 用本项目自己的证书签名（`guest-tools/signing-certificate.pem`），不是 Apple 签发的证书。

## 已知问题

- 使用中虚拟机卡死（包括调整分辨率时）仍在排查。
- Intel 显卡上的渲染问题（[Issue #2](https://github.com/H0yeR404/weims-vgpu/issues/2)）在这个运行组件里没有修复。
- Guest Tools 只传文本，不含图片、文件和拖放。

## 验证

自动化：核心 97 项、Guest Tools 主机端进程 161 项、WPF 66 项、更新选择 22 项。

真实虚拟机（macOS 26.7.2，一台带 NVIDIA 显卡的 Windows 11 主机）：在 Gatekeeper 开启的情况下，从管理器按钮和命令行安装 Guest Tools；双向文本，包括中日韩文字、emoji 和 4 MiB 文本；结束 Guest Tools 或主机端进程后的恢复；从主机更新 Guest Tools。退出修复的验证方法是把一个图形线程冻结在显卡驱动内部再退出：新运行组件 4 秒退出，旧运行组件 20 分钟后仍未退出。

运行组件 ZIP 和对应的源码包随附，所有文件均附 SHA256 校验值。
