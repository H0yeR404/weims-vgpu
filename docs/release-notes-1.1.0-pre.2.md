# Weims Manager 1.1.0-pre.2 — Updates, Live Language Switching & One-click Setup

## en


## Runtime setup refresh — 2026-11-14

This release's packages have been refreshed in place, keeping version 1.1.0-pre.2. If you installed the earlier pre.2 package, download and run the installer again; version-based update checks do not offer another build with the same version.

- First-time users land on the runtime setup page automatically.
- One-click setup downloads, verifies and saves all runtime paths. Valid existing runtimes are reused.
- Runtime components, virtualization and graphics readiness are shown clearly. Manual paths are hidden under Advanced.
- Starting an imported VM automatically prepares missing runtime components. BIOS settings, Windows features and GPU drivers may still require manual action.

- Check for updates from GitHub, read release notes, and download/install with SHA256 verification.
- Stable builds accept stable releases; previews also accept newer previews. Downgrades are blocked.
- Installation waits until all QEMU VMs and other Manager windows are closed. Downloads can be cancelled.
- Switch English/Chinese immediately without restarting, preserving the current page and unsaved configuration. English remains the default.
- Show the actual application version and preserve the settings directory across updates.

**PRE-RELEASE: VM freezes remain possible, including during resolution changes. This known issue is under investigation and will be addressed.** The manager is still under development. Full fresh macOS installation and long-term stability remain unvalidated.

## Install / upgrade

Download `Weims-Manager-1.1.0-pre.2-Setup-x64.exe`, close VMs and Manager, then run it; or extract `Weims-Manager-1.1.0-pre.2-win-x64.zip` and run `Weims.Manager.exe`. Versions 1.0.1 and 1.1.0-pre.1 do not have the new updater: install this release manually once. Future compatible releases can be installed from Software updates.

Updating a portable copy installs Manager into the per-user application folder. The updater preserves the selected settings directory. QEMU/Reims runtime and VM disks are not replaced by a Manager update.

The runtime is unchanged from [v1.1.0-pre.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1). Automatic preparation still uses that pinned runtime; its runtime ZIP and corresponding source archive remain available there. Both GUI and CLI are included.

## Validation

63 Core checks, 28 WPF checks, 21 updater checks (including live GitHub discovery), and 6 installer-handoff checks passed. Handoff tests mock installation/process launches; a real in-place installation upgrade has not been performed. SHA256SUMS is attached. Stable 1.0.1 remains Latest.


## zh-CN


## 运行环境准备优化 — 2026-11-14

此次原位更新安装包，版本号仍为 1.1.0-pre.2。已经安装较早 pre.2 包的用户，请重新下载安装包并运行；基于版本号的更新检查不会提示同版本的新构建。

- 首次使用直接进入运行环境准备页面。
- 一键下载、校验并自动保存全部路径，已有有效环境直接复用。
- 清晰显示运行组件、虚拟化和显卡的就绪状态，手动路径收进高级设置。
- 启动导入的虚拟机时会自动准备缺失组件。BIOS 设置、Windows 功能和显卡驱动仍可能需要手动处理。

- 从 GitHub 检查更新，显示更新说明，支持 SHA256 校验后下载并安装。
- 稳定版接收稳定版更新，预览版也接收更高版本的预览版，禁止降级。
- 所有 QEMU 虚拟机和其他管理器窗口关闭后才安装；下载支持取消。
- 中英文立即切换，无需重启，保留当前页面和未保存的配置，默认仍为英文。
- 显示实际版本号，更新后保留设置目录。

**预发布：虚拟机仍可能卡死，包括调整分辨率时。这是正在排查、计划修复的已知问题。** 管理器仍在开发中，完整的全新 macOS 安装及长期稳定性尚未验证。

## 安装与升级

下载 `Weims-Manager-1.1.0-pre.2-Setup-x64.exe`，关闭虚拟机和管理器后运行；或解压 `Weims-Manager-1.1.0-pre.2-win-x64.zip` 并运行 `Weims.Manager.exe`。1.0.1 和 1.1.0-pre.1 尚无新更新功能，需要先手动安装此次版本一次。以后可通过“软件更新”安装兼容的新版。

免安装版使用更新功能后会安装到当前用户的应用目录，保留选中的设置目录。管理器更新不会替换 QEMU/Reims 运行环境或虚拟机磁盘。

本次运行库与 [v1.1.0-pre.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1) 相同，自动准备继续使用该固定版本；运行库 ZIP 和对应源码包仍在旧发布页提供。此次软件包同时包含 GUI 和 CLI。

## 验证

通过 63 项核心检查、28 项 WPF 检查、21 项更新检查（含真实 GitHub 查询）和 6 项安装交接检查。交接测试模拟安装及进程启动，尚未执行真实覆盖安装。附 SHA256SUMS 校验文件。稳定版 1.0.1 仍保留 Latest 标记。


## ja


## 実行環境セットアップの改善 — 2026-11-14

バージョン番号 1.1.0-pre.2 のまま配布パッケージを差し替えました。旧 pre.2 をインストール済みの場合は、インストーラーを再ダウンロードして実行してください。バージョン番号による更新確認では同じバージョンの新ビルドは通知されません。

- 初回起動時は実行環境の準備画面を表示します。
- ワンクリックでダウンロード、検証、パスの保存を行い、有効な既存環境は再利用します。
- 実行コンポーネント、仮想化、GPU の状態を表示し、手動パス設定は Advanced にまとめました。
- インポートした VM の起動時も不足コンポーネントを自動準備します。BIOS、Windows の機能、GPU ドライバーは手動対応が必要な場合があります。

- GitHub の更新を確認し、リリースノートを表示。SHA256 検証後にダウンロード・インストールできます。
- 安定版は安定版のみ、プレビュー版は新しいプレビュー版も対象です。ダウングレードしません。
- すべての QEMU VM とほかの Manager ウィンドウを閉じるまでインストールを延期します。ダウンロードはキャンセルできます。
- 再起動せず英語と中国語を切り替え、現在のページと未保存の設定を保持します。既定は英語です。
- 実際のバージョンを表示し、更新後も設定ディレクトリを引き継ぎます。

**プレリリース：解像度変更時などに VM がフリーズする可能性があります。調査中の既知の問題で、今後修正予定です。** 管理アプリは開発中です。macOS の完全な新規インストールと長期安定性は未検証です。

## インストール・更新

VM と Manager を閉じ、`Weims-Manager-1.1.0-pre.2-Setup-x64.exe` を実行するか、`Weims-Manager-1.1.0-pre.2-win-x64.zip` を展開して `Weims.Manager.exe` を実行してください。1.0.1 と 1.1.0-pre.1 には新しい更新機能がないため、今回のみ手動でインストールしてください。以降は Software updates から対応する新版に更新できます。

ポータブル版の更新はユーザー用アプリフォルダーへのインストールになります。選択した設定ディレクトリを保持します。Manager の更新は QEMU/Reims ランタイムと VM ディスクを置き換えません。

ランタイムは [v1.1.0-pre.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1) と同一です。自動準備もこの固定版を使用します。ランタイム ZIP と対応するソースは旧リリースにあります。GUI と CLI を同梱します。

## 検証

Core 63 件、WPF 28 件、更新 21 件（実際の GitHub 確認を含む）、インストール引き継ぎ 6 件の検証に合格しました。引き継ぎテストはインストールとプロセス起動を模擬し、実際の上書き更新は未検証です。SHA256SUMS を添付します。安定版 1.0.1 の Latest 表示を維持します。
