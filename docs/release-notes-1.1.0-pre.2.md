# Weims Manager 1.1.0-pre.2 — updates and live language switching

## en


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

63 Core checks, 25 WPF checks, 21 updater checks (including live GitHub discovery), and 6 installer-handoff checks passed. Handoff tests mock installation/process launches; a real in-place installation upgrade has not been performed. SHA256SUMS is attached. Stable 1.0.1 remains Latest.


## zh-CN


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

通过 63 项核心检查、25 项 WPF 检查、21 项更新检查（含真实 GitHub 查询）和 6 项安装交接检查。交接测试模拟安装及进程启动，尚未执行真实覆盖安装。附 SHA256SUMS 校验文件。稳定版 1.0.1 仍保留 Latest 标记。


## ja


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

Core 63 件、WPF 25 件、更新 21 件（実際の GitHub 確認を含む）、インストール引き継ぎ 6 件の検証に合格しました。引き継ぎテストはインストールとプロセス起動を模擬し、実際の上書き更新は未検証です。SHA256SUMS を添付します。安定版 1.0.1 の Latest 表示を維持します。
