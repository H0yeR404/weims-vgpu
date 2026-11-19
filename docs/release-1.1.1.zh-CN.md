# Weims Manager 1.1.1

1.1.0 的维护更新，标记为 Latest，包含 Windows GUI 与 CLI。修复一个安全问题，以及 [Issue #3](https://github.com/H0yeR404/weims-vgpu/issues/3) 报告的关机问题。

## 更新内容

- **安全：虚拟机里的程序能连到 QEMU 的控制通道。** 1.0 到 1.1.0 启动 QEMU 时，把它的控制接口（QMP）开在本机的一个 TCP 端口上，没有认证。虚拟机里的程序可以经虚拟机自己的网络连到这个端口，控制宿主上的 QEMU。现在 QMP 改用 `%USERPROFILE%\.weims\q` 下的 Unix 域套接字，虚拟机的网络够不着。详见 [security-qmp-socket.md](security-qmp-socket.md)。
- **管理器的“正常关机”现在有效**（Issue #3）。这个按钮和 `Weims.Cli shutdown` 以前发的是一次只持续几毫秒的电源键按压，而 macOS 会忽略短于 0.35 秒的按压。现在改为发送 macOS 用于“退出所有应用并关机”的组合键（Control-Option-Command-电源键）。有未保存的文档时 macOS 会先询问，和从苹果菜单关机一样。
- **macOS 关机后 QEMU 自动退出**（Issue #3）。以前客户机关机后，QEMU 会留下一个黑窗口：虚拟机一直处于锁定状态，转发端口也一直被占用，直到手动关掉那个窗口。
- 管理器和安装向导里移除了 QMP 端口设置。已有虚拟机配置保留，1.1.0 仍能打开。

## 安装与升级

下载 `Weims-Manager-1.1.1-Setup-x64.exe`，或解压 `Weims-Manager-1.1.1-win-x64.zip`。两者均包含 GUI 与 CLI，无需另装 .NET。

1.1.0 和 pre.2 用户可选择 **软件更新 → 检查更新 → 一键更新**。安装前请关闭虚拟机及其他 Manager 窗口。1.0.1 和 pre.1 需手动下载安装一次。设置目录、虚拟机磁盘和运行库都不会改动。

这些修复对 1.1.1 启动的虚拟机生效。由旧版本启动、仍在运行的虚拟机在重启前保持旧行为；1.1.1 仍能管理它，客户机关机后仍可用 `Weims.Cli stop` 退出它的 QEMU。

如果你有自己的脚本连接 QMP 的 TCP 端口，需要改用套接字路径，即虚拟机 `state\runtime.json` 里的 `qmp_socket`。

## 运行库与已知问题

QEMU/Reims 图形运行库没有变化，仍固定为 [v1.1.0-pre.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1)，对应运行库 ZIP 和源码归档仍在该页面。更新管理器不会替换已有有效运行库。

**虚拟机仍可能卡死，包括调整分辨率时，目前仍在排查。** 本次更新没有修复这些问题。[Issue #2](https://github.com/H0yeR404/weims-vgpu/issues/2) 报告了 Intel Arc 140T 的图形格式错误及 macOS 初始化卡死，Intel Arc 兼容性尚未充分验证。不包含剪贴板共享、文件共享或跨系统文件拖拽。

管理器的“正常关机”需要 macOS 已登录并能响应键盘。在登录界面、macOS 安装器或恢复环境里没有验证过，请用屏幕上的关机按钮。macOS 卡死时，最后的办法仍是“强制停止”。请备份重要虚拟机数据。

## 验证

自动检查：Core 86 项、集成 20 项（真实 QEMU 进程和 QMP，不含客户机）、WPF 46 项、更新选择 22 项、运行环境 4 项，另有 6 项安装交接检查（进程和安装边界为模拟）。

带真实客户机（macOS 26.7.2，一台 Windows 11 宿主）：用命令行经套接字启动和查询状态；在客户机里已连不上原来的 QMP 端口；确认旧的电源键请求被忽略、新的组合键能让 macOS 关机；从管理器窗口启动并关机，之后 QEMU 自行退出并释放端口。

发布附件包含 SHA256 校验清单。
