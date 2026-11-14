# weims-vgpu

> 最新プレビュー：**1.1.0-pre.2** — ソフトウェア更新、リリースノート、英語・中国語の即時切り替え。 [v1.1.0-pre.2](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.2)

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

![macOS Tahoe on Windows](docs/images/macos-tahoe.png)

## クイックスタート — Windows · 1.1.0-pre.1

> **プレリリース：解像度変更時などに VM がフリーズする可能性があります。既知の問題として今後の更新で修正予定です。重要なデータをバックアップしてください。** 管理アプリは開発中です。自動準備から Tahoe 復旧画面までを本機で確認しましたが、新規インストールからデスクトップまでの全工程は未検証です。

必要環境：Windows 10 2004+/11 x64、BIOS/UEFI の仮想化、**Windows ハイパーバイザー プラットフォーム（WHPX）**、`VK_EXT_external_memory_host` 対応の Vulkan GPU とドライバー。「Windows の機能の有効化または無効化」で WHPX を有効にして再起動してください。アプリは BIOS や Windows 機能を自動変更しません。配布版には **WSL2、MSYS2、Rust、.NET SDK は不要**です。GitHub、Apple、上流のダウンロードサーバーへの接続が必要です。

### インストールと VM 作成

1. [プレリリース](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1)から `Weims-Manager-1.1.0-pre.1-Setup-x64.exe` またはポータブル ZIP を取得します。インストーラーとアプリには Weims アイコンが含まれます。Preview は安定版とは別にインストールされます。UI は英語が既定で、中国語にも切り替えられます。
2. **Weims Manager Preview → Create VM** で新しい保存先、名前、容量（既定 **200 GiB**）を選びます。Apple からの Tahoe 復旧イメージ自動取得を有効のままにするか、互換性のある `BaseSystem.dmg` / `.img` を選択します。完全な ISO と ARM macOS には対応しません。
3. **Create** を押すと PowerShell、QEMU/Reims、検証付き Apple 復旧イメージの取得、固有のマシン識別情報を持つ OpenCore、OVMF、対応グラフィックスドライバーと ROM の準備、イメージ変換、空ディスク作成を行います。WSL 操作、EFI 編集、ファームウェアのコピー、SMC キー入力は不要です。既存の保存先は上書きしません。スパースディスクの容量は実空き容量の予約ではありません。
4. 起動し、OpenCore で復旧項目を選びます（非表示の場合は **Space**）。**ディスクユーティリティ → すべてのデバイスを表示**で容量を確認し、**新規の空システムディスクだけ**を **GUID パーティションマップ / APFS** で消去します。復旧ディスクや OpenCore ディスクを消去しないでください。
5. ディスクユーティリティを閉じ、**macOS Tahoe を再インストール**で新しいボリュームを選びます。ダウンロードと再起動を待ち、進行に応じてインストーラー／システムを選択します。デスクトップに到達したら正常終了し、残った QEMU を停止して復旧イメージの接続を解除し、保存後に再起動します。

新規 VM は**ウィンドウサイズに合わせたゲスト解像度変更**と自由なポインター移動が既定です。ログイン後に独立した Reims ウィンドウをドラッグします。既存 VM は停止後、**Display & Input → Auto fit guest resolution** を選択し、枠なし表示を無効にして保存します。起動解像度は **800–4096 × 600–2160**、**2560×1440 は本機のみで検証済み**です。Fixed は全画面を拡縮し、Compatibility は従来の動作を維持します。応答しなくなった場合はログを保存し、可能なら正常終了してください。クリップボードとファイル共有は含まれません。

### コマンドライン（インストーラー・ポータブル版に同梱）

アプリのディレクトリで Windows PowerShell を開きます。`create` は同じ構成を自動準備します。`--recovery` を省略すると Apple から取得します。

```powershell
.\Weims.Cli.exe create --destination D:\VMs\macOS-Tahoe --disk-gib 200
.\Weims.Cli.exe start --config D:\VMs\macOS-Tahoe\state\config.json --display-mode auto
.\Weims.Cli.exe status --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe shutdown --config D:\VMs\macOS-Tahoe\state\config.json
# macOS の終了後に QEMU を終了：
.\Weims.Cli.exe stop --config D:\VMs\macOS-Tahoe\state\config.json
```

既存 200 GiB VM は実際の `state/config.json` を `start` に渡します。ディスクを作り直さないでください。Preview に実行環境がない場合は先に `setup` を実行します。GUI と CLI は `%LOCALAPPDATA%\Weims\Manager-Preview` を共有します。同じディスクを二重起動しないでください。`stop` は QMP がシャットダウン済みと確認した場合のみ終了します。終了要求に応答しない場合は macOS の Apple メニューを使用します。

[プレリリースの説明](docs/prerelease-1.1.0-pre.1.ja.md) · [安定版 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)。安定版には自動ブート準備と可変解像度は含まれません。

### 方法 B：ソースからビルド（上級者向け）

Weims の変更、Windows グラフィックスバックエンドのビルド、ソースからの再現に使用します。

**手順 1 — 必要なツールをインストール**

- [Git for Windows](https://git-scm.com/downloads/win)
- [PowerShell 7](https://github.com/PowerShell/PowerShell/releases)
- [Rustup](https://rustup.rs/)
- [MSYS2](https://www.msys2.org/)

MSYS2 は `C:\msys64` にインストールするか、以下のパスを実際の場所に変更してください。

**手順 2 — MSYS2 を設定**

ここで使うのは **Windows の MSYS2 UCRT64 であり、WSL2 ではありません**。MSYS2 は `pacman` を使用しますが、`mingw-w64-ucrt-x86_64-*` は Windows ビルド用パッケージで、Arch Linux のパッケージではありません。Debian/Ubuntu の `apt`、Arch の `pacman`、Fedora の `dnf` で、この Windows ネイティブビルド環境を置き換えることはできません。

**Windows のスタートメニュー → MSYS2 UCRT64** を開くか、**Windows PowerShell** から起動します。

```powershell
& 'C:\msys64\msys2_shell.cmd' -defterm -here -no-start -ucrt64
```

開いた端末で `echo "$MSYSTEM"` を実行し、`UCRT64` と表示されることを確認します。違う場合は正しい端末を開き直してください。以下は **UCRT64 端末内**で実行し、PowerShell、コマンドプロンプト、Git Bash、WSL の端末には直接貼り付けないでください。

```sh
pacman -Syu
```

端末の再起動を求められた場合は、再起動して更新を再実行します。その後、ビルドツールをインストールします。

```sh
pacman -S --needed git make diffutils bison flex mtools \
  mingw-w64-ucrt-x86_64-{clang,llvm,llvm-tools,lld,cmake,meson,ninja,python,glib2,pixman,pkgconf,libslirp,libusb,SDL2,vulkan-headers,vulkan-loader,vulkan-validation-layers,spirv-tools}
```

**手順 3 — Weims を取得してビルド**

**Windows の PowerShell 7** に戻って実行します。`scripts/build.ps1` は必要に応じて MSYS2 を呼び出し、Windows 実行ファイルを生成します。Linux 用のビルドスクリプトではありません。

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu

$env:WEIMS_MSYS2 = "C:\msys64"

rustup toolchain install nightly-2026-11-03-x86_64-pc-windows-gnu --profile minimal

pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
```

スクリプトは固定した Reims、QEMU、metal2vulkan のソースを取得し、Windows 互換パッチを検証・適用して、Windows ネイティブの QEMU/Reims をビルドします。

出力先：

```text
build/port-qemu/
build/port-dist/
```

対応するグラフィックス ROM：

```text
sources/reims/firmware/prebuilt/ReimsGraphics.rom
```


Manager/CLI のビルドには .NET 8 SDK をインストールし、次を実行します：

```powershell
pwsh -File manager/build-display.ps1
pwsh -File manager/build-setup.ps1
pwsh -File manager/build.ps1 -SelfContained
```

### 任意：WSL2 のツールで EFI ファイルを確認・準備

前述の Windows 手順では、この節は不要です。WSL2 の Linux ツールはファイルの確認や OpenCore raw FAT イメージのコピーの準備に使えますが、以下のパッケージは **Windows QEMU/Reims のビルド環境の代わりにはなりません**。WHPX の有効化や Windows Vulkan ドライバーの導入も行いません。

WSL2 を既に利用している場合は、**Windows PowerShell** で導入済みディストリビューションを一覧表示して、そのうちの 1 つを開きます。`Ubuntu` は一覧の正確な名前（`Debian`、`Arch`、または表示された Fedora の名前など）に変更してください。

```powershell
wsl --list --verbose
wsl -d Ubuntu
```

一覧で対象の WSL バージョンが `2` であることを確認します。Linux の端末内で `cat /etc/os-release` を使ってディストリビューションを確認し、**該当するコマンドだけ**を実行してください。以下は `sudo` 権限のあるユーザーを想定しています。root の場合は `sudo` を省略できます。

**Debian / Ubuntu — WSL2 内で実行：**

```sh
sudo apt update
sudo apt install git python3 mtools
```

**Arch Linux — WSL2 内で実行：**

```sh
sudo pacman -Syu --needed git python mtools
```

**Fedora — WSL2 内で実行：**

```sh
sudo dnf install git python3 mtools
```

EFI 補助スクリプトは `tomllib` を使うため **Python 3.11 以降**が必要です。`python3 --version` と `python3 -c 'import tomllib'` で確認してください。利用できない場合は Python 3.11 以降を提供するディストリビューションのバージョンか、前述の MSYS2 手順を使用します。

パスも端末によって異なります。Windows の `C:\VM-inputs\OpenCore.img` は MSYS2 では `/c/VM-inputs/OpenCore.img`、WSL2 では `/mnt/c/VM-inputs/OpenCore.img` です。WSL2 では実際のリポジトリの場所（例：`cd /mnt/d/Coding/weims-vgpu`）に移動し、EFI 補助コマンドを `/ucrt64/bin/python.exe ...` ではなく `python3 scripts/prepare-opencore.py vm/boot-work/EFI` に変更します。これはソース取得済みの状態で、コピーしたイメージの EFI 準備だけを置き換える方法です。同じコピーに両方の準備手順を実行しないでください。Windows の `ocvalidate`、ビルド、VM 起動は Windows PowerShell に戻って実行します。`exit` で WSL2 を終了できます。

## 検証環境

> **互換性について：** 本プロジェクトは現在、以下の実機構成でのみテストし、正常動作を確認しています。他の機器も理論上は対応可能ですが、互換性と正常動作の確認には実機での検証が必要です。

| 項目 | 構成 |
| --- | --- |
| ノート PC | Lenovo Legion Y9000P IRX8H（82WQ） |
| ホスト OS | Windows 11 Pro for Workstations、x86_64 |
| Windows ビルド | 10.0.26300.9550（Fastfetch の表示は 26H2） |
| CPU | Intel Core i9-13900HX、32 論理プロセッサー |
| GPU | NVIDIA GeForce RTX 4090 Laptop GPU、報告 VRAM 15.67 GiB |
| ホストメモリ | 報告された使用可能な総容量 95.81 GiB |
| ホスト画面 | CU34G4 外部モニター、3440 × 1440、180 Hz |
| ホストシェル | PowerShell 7.6.9 |
| 仮想化 / グラフィックス | WHPX / Vulkan、ゲストメモリ共有経路 |
| 検証用ゲスト | macOS Tahoe、8 vCPU、32 GiB メモリ、200 GiB 仮想システムディスク |
| ゲスト解像度 | 1920 × 1080 |

これは検証済みの機器構成であり、最低要件や推奨構成ではありません。ホスト画面のリフレッシュレートはゲストの描画 FPS とは異なります。また、ホストの既存 PowerShell と、自動セットアップが必要に応じて取得する固定版 PowerShell 7.7.0 は別です。

実装は C API テスト 22 件に合格しました。10 秒間のウィンドウ移動テストでは、異なる画面の更新頻度が 54.17 FPS、最大フレーム間隔が 211 ms でした。これは同一条件での旧経路との比較や長期安定性の保証ではありません。ホストの device cache と必要なコピーが残るため、完全なゼロコピーではありません。

## 上流プロジェクトとライセンス

[Reims vGPU](https://github.com/steelbrain/reims-vgpu)、[metal2vulkan](https://github.com/steelbrain/metal2vulkan)、[QEMU Reims](https://github.com/steelbrain/qemu-reims-vgpu)、および [tqmane の Windows 移植](https://github.com/tqmane/reims-vgpu) に基づいています。公式の上流リリースではありません。

Reims と metal2vulkan は `GPL-2.0-or-later OR LGPL-3.0-or-later`、QEMU は元のファイルごとのライセンスを維持します。統合スクリプトと文書は GPL-2.0-or-later です。[LICENSE](LICENSE)、[帰属と来歴](NOTICE.md)、[ライセンス全文](licenses/) を参照してください。スクリーンショット内の Apple ソフトウェア、商標、第三者コンテンツの権利は、このリポジトリから許諾されるものではありません。
