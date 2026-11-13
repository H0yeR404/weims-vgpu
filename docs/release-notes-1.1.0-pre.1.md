# Weims Manager 1.1.0-pre.1 — adaptive display and guided installation

GUI + CLI: adaptive resolution and automatic runtime / Apple recovery / OpenCore / OVMF / graphics preparation.

软件与命令行：拖拽自适应分辨率，自动准备运行库、Apple 恢复镜像、OpenCore、OVMF 和显卡组件。

GUI と CLI：可変解像度、実行環境・Apple 復旧イメージ・OpenCore・OVMF・グラフィックスの自動準備。

## English

> **PRE-RELEASE: the VM may freeze, including during resolution changes. This is a known issue and will be addressed in a future update. Back up important data.** The manager is still under development. Automatic preparation has reached the Tahoe recovery menu on the test host; a complete fresh installation to the desktop has not been validated.

Requirements: Windows 10 2004+/11 x64, BIOS/UEFI virtualization, **Windows Hypervisor Platform (WHPX)** enabled (restart Windows after enabling it), and a Vulkan GPU/driver supporting `VK_EXT_external_memory_host`. Enable WHPX in **Turn Windows features on or off**. The app does not enable firmware virtualization or Windows features for you. WSL2, MSYS2, Rust and the .NET SDK are **not needed for the packaged app**. Internet access to GitHub, Apple and upstream download servers is required.


## 简体中文

> **预发布版：虚拟机可能卡死，包括调整分辨率时。这是已知问题，将在后续更新中解决。请备份重要数据。** 交互软件仍在开发中。本机已验证自动准备并进入 Tahoe 恢复界面，尚未验证从全新安装到桌面的完整流程。

要求：Windows 10 2004+/11 x64、BIOS/UEFI 开启虚拟化、开启 **Windows 虚拟机监控程序平台（WHPX）** 并重启，以及支持 `VK_EXT_external_memory_host` 的 Vulkan 显卡与驱动。在“启用或关闭 Windows 功能”中启用 WHPX。软件不会自动修改 BIOS 或启用 Windows 功能。使用安装包**无需 WSL2、MSYS2、Rust 或 .NET SDK**。需要访问 GitHub、Apple 和上游下载服务器。


## 日本語

> **プレリリース：解像度変更時などに VM がフリーズする可能性があります。既知の問題として今後の更新で修正予定です。重要なデータをバックアップしてください。** 管理アプリは開発中です。自動準備から Tahoe 復旧画面までを本機で確認しましたが、新規インストールからデスクトップまでの全工程は未検証です。

必要環境：Windows 10 2004+/11 x64、BIOS/UEFI の仮想化、**Windows ハイパーバイザー プラットフォーム（WHPX）**、`VK_EXT_external_memory_host` 対応の Vulkan GPU とドライバー。「Windows の機能の有効化または無効化」で WHPX を有効にして再起動してください。アプリは BIOS や Windows 機能を自動変更しません。配布版には **WSL2、MSYS2、Rust、.NET SDK は不要**です。GitHub、Apple、上流のダウンロードサーバーへの接続が必要です。


## Validation / 验证 / 検証

63 Core checks, 17 bilingual WPF checks and 11 real-tool integration checks passed. Automatic preparation reached the Tahoe recovery menu. Existing-guest window resizing was manually confirmed. Full fresh installation and long-term stability remain unvalidated.

Installer and portable ZIP include GUI and CLI. Runtime, corresponding sources, three-language guides and SHA256SUMS are attached. Stable 1.0.1 remains available.
