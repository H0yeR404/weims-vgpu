# weims-vgpu

> **開発状況：** Weims Manager は開発中で、まだ完全なインストール・管理ツールではありません。一部の準備は手動で行う必要があり、Manager による復元イメージからデスクトップまでのインストール全体は未検証です。主な準備・インストール手順はこのページに記載しています。

[Weims Manager 1.0.1 をダウンロード](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)：Windows インストーラー、英語（既定）・中国語 UI、実行環境の自動セットアップ。

Windows 向け [Weims Manager 1.0.1](manager/README.md) を追加しました。復元イメージの準備と仮想マシン設定を GUI で行い、VM の表示ウィンドウは独立して動作します。UI は英語（既定）と中国語に対応しています。

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

Windows 上で WHPX と Vulkan を使い、Reims の新しいグラフィックス経路を動かすための統合プロジェクトです。新しいゲストドライバーとプロトコルを維持しながら、Windows の section によってゲスト RAM を共有し、従来の Windows 移植で発生していた深刻な書き戻し遅延に対処します。

![Windows 上で動作する macOS Tahoe](docs/images/macos-tahoe.png)

所有者提供のスクリーンショットには、macOS Tahoe、8 vCPU、32 GiB のメモリ、約 200 GiB の APFS ボリュームが表示されています。180 Hz という表示モードは、180 FPS の実測結果を意味しません。

## クイックスタート — Windows

Weims vGPU は WHPX と Vulkan を使い、Windows 上でネイティブに動作します。VM の実行に **WSL2 は不要**です。

### 方法 A：Weims Manager をインストール（推奨）

通常はビルド済みアプリを利用する方法が簡単です。

1. [リリースページ](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)から **Weims Manager 1.0.1** をダウンロードします。
2. `Weims-Manager-1.0.1-Setup-x64.exe` を実行します。
3. **Runtime → Check environment** を開きます。
4. **Prepare / Retry** をクリックし、Windows QEMU/Reims 実行環境を自動取得・設定します。

**必要条件：**

- Windows 10 バージョン 2004 以降 / Windows 11、x64
- BIOS/UEFI で CPU 仮想化が有効
- Windows Hypervisor Platform（WHPX）が有効
- `VK_EXT_external_memory_host` に対応する Vulkan GPU とドライバー

Manager が自動準備するのは Windows 実行環境です。macOS 復元イメージ、互換性のある OpenCore、OVMF ファームウェア、Reims グラフィックス ROM は別途用意します。UI は英語が既定で、中国語にも対応します。以下の「起動ファイルの準備と macOS のインストール」に進んでください。

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

**手順 4 — VM を準備して起動**

実行環境をビルドしても macOS VM は自動作成されません。互換性のある OpenCore、OVMF、macOS 復元イメージまたは既存の起動可能なシステムディスクが必要です。Manager は完全な ISO の取り込みに対応しません。以下で起動ファイルを準備し、Manager で新規 VM を作成するか、コマンドラインで既存ディスクを起動します。

### 起動ファイルの準備と macOS のインストール

準備順序は **BaseSystem.dmg → OpenCore.img → OVMF → ReimsGraphics.rom → Weims Manager → macOS のインストール**です。それぞれ別の入力であり、同じイメージを順番に変換するものではありません。互換性のある Intel/x86-64 版 macOS を使用します。Apple Silicon/ARM 版 macOS の変換実行には対応しません。

**手順 1 — BaseSystem.dmg を取得**

目的の macOS バージョンに対応する Apple 提供の復元イメージを取得します。[OpenCorePkg の macrecovery ツール](https://github.com/acidanthera/OpenCorePkg/tree/master/Utilities/macrecovery)で Apple から取得できます。選択した OpenCore リリース付属の説明に従ってください。このツールには Python が必要で、要求する機種・ボード識別子によって返される復元バージョンが変わります。

`BaseSystem.dmg` と付属の検証ファイルを保存します。Manager は DMG を raw `BaseSystem.img` に変換します。変換済みの raw `.img` も使用できますが、DMG や ISO の拡張子変更だけでは変換できません。復元環境は Apple からインストールファイルをダウンロードするため、ゲストのネット接続が必要です。Manager は基本構造を確認しますが、Apple の付属 chunklist や全バージョンの互換性までは検証しません。

**手順 2 — OpenCore とゲストドライバーを準備**

x86-64 QEMU に対応する OpenCore 起動イメージを用意します。検証基準は **OpenCore 1.0.8** です。[OpenCorePkg リリース](https://github.com/acidanthera/OpenCorePkg/releases)からプログラムと設定資料を取得してください。配布 ZIP はそのまま起動できる Weims ディスクではありません。VM 用の基本設定とマシン識別情報は手動で準備する必要があり、Manager は生成しません。

以下は `EFI/BOOT`、`EFI/OC` と動作する `EFI/OC/config.plist` を含む **raw FAT ファイルシステムイメージ**を前提とします。停止済み VM のコピーを使用してください。新しい経路には対応する **Graft、ReimsGraphics、RgfxGate** ゲストドライバーが必要です。

方法 B では既にこれらを取得しています。Manager 利用者も上記の Git、PowerShell 7、MSYS2 を導入すれば、ホスト実行環境をビルドせずに **PowerShell 7** で取得できます。

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu
$env:WEIMS_MSYS2 = 'C:\msys64'
pwsh -File scripts/fetch.ps1
```

方法 B で `sources/` を作成済みなら再取得は不要です。スクリプトは既存ソースディレクトリを拒否します。ゲストファイルは選択したホスト実行環境のリリースに合わせ、無関係な上流のビルド済みファイルを混在させないでください。

**MSYS2 UCRT64** で前述の更新を行い、準備ツールをインストールします。

```sh
pacman -S --needed git diffutils mtools mingw-w64-ucrt-x86_64-python
```

同じ端末でリポジトリのルートに移動し、入力パスを自分のファイルに置き換えてコピーを準備します。

```sh
mkdir -p vm/boot-work
cp /c/VM-inputs/OpenCore.img vm/boot-work/OpenCore.img
mcopy -s -i vm/boot-work/OpenCore.img ::/EFI vm/boot-work/
/ucrt64/bin/python.exe scripts/prepare-opencore.py vm/boot-work/EFI
```

OpenCore 1.0.8 の `ocvalidate` で `vm/boot-work/EFI/OC/config.plist` を検証します。例として、リポジトリのルートで **PowerShell 7** を使い、実行ファイルのパスを展開先に合わせます。

```powershell
& 'C:\Tools\OpenCore-1.0.8-RELEASE\Utilities\ocvalidate\ocvalidate.exe' `
  'vm/boot-work/EFI/OC/config.plist'
```

検証成功後、**MSYS2 UCRT64** に戻り EFI ツリーを書き戻します。

```sh
mcopy -o -s -i vm/boot-work/OpenCore.img vm/boot-work/EFI ::/
```

補助スクリプトは検証構成に合わせて Apple Secure Boot を無効化し、想定外の有効な kext がある場合は処理を拒否します。その場合は依存関係を確認し、チェック回避のために無関係な項目を削除しないでください。上記 mtools コマンドはパーティション付き EFI ディスクには使えません。EFI パーティションを抽出またはマウントしてください。QCOW2 起動ディスクはまずコピーを `qemu-img convert -f qcow2 -O raw source.qcow2 OpenCore.img` で変換します。変換してもパーティションテーブルは除去されません。

**手順 3 — OVMF と ReimsGraphics.rom を用意**

同じファームウェアビルドの x86-64 QEMU 用 `OVMF_CODE.fd` と `OVMF_VARS.fd` を用意するか、停止済みの互換 VM からコピーします。[TianoCore EDK II / OvmfPkg](https://github.com/tianocore/edk2/tree/master/OvmfPkg)に上流ソースとビルド手順があります。CODE はファームウェア本体、VARS は書き込み可能な UEFI 設定です。異なるレイアウトを混在させたり、動作中の VM 間で書き込み可能な VARS を共有したりしないでください。

ゲストドライバーとホスト実行環境に対応する ROM を使用します。

```text
sources/reims/firmware/prebuilt/ReimsGraphics.rom
```

ROM は OpenCore のゲストドライバーの代わりにはなりません。固定した場所に保存してください。Manager は OpenCore と OVMF をコピーしますが、**ROM は元のパスを参照**します。

**手順 4 — Manager で新規 VM を作成**

方法 A の環境チェック完了後、復元イメージからの作成ウィザードに入力します。

| 項目 | 指定内容 |
| --- | --- |
| VM 名 | 例：`macOS-Tahoe`。ASCII 英数字・アンダースコア・ハイフンの 1–48 文字で、先頭は英数字 |
| 復元イメージ | `BaseSystem.dmg` または raw `BaseSystem.img` |
| 保存先の親フォルダー | 既存のローカルフォルダー。その中に VM 名の新しいサブフォルダーを作成 |
| システムディスク容量 | 例：`200` GiB。対応範囲は 64–2048 GiB |
| OpenCore | 準備した `vm/boot-work/OpenCore.img` |
| ファームウェア CODE / VARS | 対応する `OVMF_CODE.fd` と `OVMF_VARS.fd` |
| グラフィックス ROM | 対応する `ReimsGraphics.rom` の固定パス |
| QMP / SSH ポート | 未使用で互いに異なる localhost ポート。ウィザードの既定値は `4455` / `2233` |

最終的な VM ディレクトリは未作成である必要があります。**Create** を選び、イメージ変換とディスク作成を待ちます。スパースディスクは macOS の書き込みに応じて増大するため、ホストに十分な実空き容量を確保してください。失敗・キャンセル時は調査用の作業ディレクトリが残ります。

準備後は VM を登録するだけで、自動起動しません。復元 VM の既定値は 8 vCPU、8 GiB RAM、`1920x1080` です。ホストに合わせてリソースを調整し、初回は `1920x1080` と復元イメージの接続を維持してください。Manager では SMC キーの入力は不要です。

**手順 5 — VM 内で macOS をインストール**

1. VM を起動します。QEMU/Reims の独立した表示ウィンドウが開きます。
2. OpenCore で復元環境を選択します。補助項目が非表示の場合は **Space** キーを押します。
3. **ディスクユーティリティ → 表示 → すべてのデバイスを表示**を開きます。容量から新しい空のシステムディスクを特定し、**そのディスクだけ**を **GUID パーティションマップ**と **APFS** で消去します。名前は例として `Macintosh HD` を指定します。OpenCore や復元メディアは消去しないでください。
4. ディスクユーティリティを終了し、macOS のインストールと新しいシステムボリュームを選択します。ダウンロードと再起動を待ち、インストーラー項目、続いてインストール済みシステム項目から処理を継続します。再起動のたびに復元インストールを最初から始めないでください。
5. デスクトップに到達して設定アシスタントが完了したら、Apple メニューから終了します。QEMU 停止後に Manager で復元イメージの接続を解除し、保存して再起動し、システム単独で起動できることを確認します。OpenCore、OVMF、ROM は保持します。

起動に失敗したら、入力を変更する前に VM の `logs` ディレクトリを保存し、エラーを記録してください。まず形式とドライバー・実行環境の組み合わせを確認します。[初回インストールガイド（英語）](docs/first-install.md)には補足とトラブルシューティング表があります。主な手順はこのページに記載しています。

### コマンドラインで既存 VM を起動

方法 B と上記の起動ファイル準備後、元の VM を停止して独立したシステムディスクのコピーを作ります。**リポジトリのルートで PowerShell 7** を開き、入力パスを自分のファイルに置き換えます。

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

出力ディスクには新しいパスを使い、保存したい既存ディスクを上書きしないでください。この例は QCOW2 ソースを前提とし、論理容量を維持して backing chain を統合します。`init-vm.ps1` は指定ディスクを直接書き込みに使用し、既存の `vm/default` ディレクトリを拒否します。起動前に `vm/default/state/config.json` を確認してください。CLI の既定値は 8 vCPU、32 GiB RAM、`1920x1080`、QMP `4444`、SSH `2222` です。リソースとポート競合を確認します。Manager と異なり CLI は `WEIMS_OSK` を要求します。保存されるコマンドログでは伏せられますが、QEMU のプロセス引数には渡されます。

入力キャプチャは **Ctrl+Alt+Win** または **Ctrl+Alt+Home** で解除できます。まず macOS 内で正常終了してください。その後も QEMU が残る場合は `pwsh -File scripts/start.ps1 -Exit` で終了できますが、通常のゲスト終了の代用にはしないでください。同じ書き込み可能なディスクを 2 つのインスタンスで同時に開かないでください。

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

### 注意事項

- WSL2 は任意であり、この Windows ネイティブビルドに必要な MSYS2 の代わりにはなりません。
- Reims のゲストドライバーとホスト実行環境のバージョンを合わせてください。ソースビルドでの自由なポインター移動は[表示とポインターの説明（英語）](manager/README.md#display-and-free-pointer)を参照してください。
- 他の GPU や Windows 構成での広範な検証は未実施です。
- 変更前に既存の VM ディスクとファームウェアをバックアップしてください。
- macOS の使用には Apple のソフトウェアライセンス条項が適用されます。

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
