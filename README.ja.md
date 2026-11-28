# weims-vgpu

> 最新正式版：**1.2.1** — ポインターが VM の画面内にある間は Windows キーが Command として働き、VM の実行中にフルスクリーンを切り替えられ、VM を iMac または MacBook Pro にできます。[ダウンロード](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.2.1)

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

![macOS Tahoe on Windows](docs/images/macos-tahoe.png)

## クイックスタート — Windows · 1.2.1

> 管理アプリは開発中です。自動準備から Tahoe 復旧画面までを本機で確認しましたが、新規インストールからデスクトップまでの全工程は未検証です。

必要環境：Windows 10 2004+/11 x64、BIOS/UEFI の仮想化、**Windows ハイパーバイザー プラットフォーム（WHPX）**、Vulkan 1.3 対応の GPU とドライバー。「Windows の機能の有効化または無効化」で WHPX を有効にして再起動してください。アプリは BIOS や Windows 機能を自動変更しません。配布版には **WSL2、MSYS2、Rust、.NET SDK は不要**です。GitHub、Apple、上流のダウンロードサーバーへの接続が必要です。

### インストールと VM 作成

1. [正式リリース](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.2.1)から `Weims-Manager-1.2.1-Setup-x64.exe` またはポータブル ZIP を取得します。インストーラーとアプリには Weims アイコンが含まれます。正式版の Weims Manager フォルダーにインストールします。既定は英語とライトテーマで、サイドバーから言語と外観を即時に切り替えられます。
2. **Weims Manager → Create VM** で新しい保存先、名前、容量（既定 **200 GiB**）を選びます。Apple からの Tahoe 復旧イメージ自動取得を有効のままにするか、互換性のある `BaseSystem.dmg` / `.img` を選択します。完全な ISO と ARM macOS には対応しません。
3. **Create** を押すと PowerShell、QEMU/Reims、検証付き Apple 復旧イメージの取得、固有のマシン識別情報を持つ OpenCore、OVMF、対応グラフィックスドライバーと ROM の準備、イメージ変換、空ディスク作成を行います。WSL 操作、EFI 編集、ファームウェアのコピー、SMC キー入力は不要です。既存の保存先は上書きしません。スパースディスクの容量は実空き容量の予約ではありません。
4. 起動し、OpenCore で復旧項目を選びます（非表示の場合は **Space**）。**ディスクユーティリティ → すべてのデバイスを表示**で容量を確認し、**新規の空システムディスクだけ**を **GUID パーティションマップ / APFS** で消去します。復旧ディスクや OpenCore ディスクを消去しないでください。
5. ディスクユーティリティを閉じ、**macOS Tahoe を再インストール**で新しいボリュームを選びます。ダウンロードと再起動を待ち、進行に応じてインストーラー／システムを選択します。デスクトップに到達したら正常終了し（VM ウィンドウは自動的に閉じます）、復旧イメージの接続を解除し、保存後に再起動します。

macOS の解像度は VM のウィンドウに追従します。ウィンドウの端をドラッグすると、画面が新しい大きさを埋めます。ウィンドウは **Display & Input** で設定した起動解像度（**800–4096 × 600–2160**）で開きます。新規 VM は自由なポインター移動が既定で、ポインターはウィンドウを自由に出入りし、Windows のショートカットもそのまま使えます。ただし、ポインターが VM の画面内にある間、Windows キーは Command キーとして macOS に渡されます。オフにすると、ウィンドウ内のクリックでキーボードとポインターがキャプチャされ、**Ctrl+Alt+Win** で解放します。相対的なマウス移動が必要なゲームはこのモードを使います。VM の実行中は、VM のウィンドウで **Ctrl+Alt+Enter** を押すか、Manager の **Full screen / window** を押すと、ウィンドウ表示と全画面を切り替えられます。全画面では、ポインターを画面上端の中央に置くと小さなバーが現れ、最小化とウィンドウ表示への復帰ができます。macOS から音が出ます。クリップボード、ファイルのコピー＆ペースト、共有フォルダーには Guest Tools（後述）が必要です。

macOS は VM を iMac（27 インチ、2020）として、またこのバージョンのランタイムでは MacBook Pro（16 インチ、2019）として扱います。**Create VM** で選び、後から VM を停止した状態で **Hardware & Network → Mac model** で変更できます。機種を変えるとシリアル番号も変わるため、iCloud、iMessage、App Store には新しいデバイスとして見えます。変更の前に macOS で Apple Account からサインアウトし、変更後にサインインし直してください。ディスク、システム UUID、ネットワークアドレスは変わらず、元の機種に戻すと以前のシリアル番号に戻ります。グラフィックスとハードウェアビデオデコードはどちらの機種でも同じように動作します。

### コマンドライン（インストーラー・ポータブル版に同梱）

アプリのディレクトリで Windows PowerShell を開きます。`create` は同じ構成を自動準備します。`--recovery` を省略すると Apple から取得します。

```powershell
.\Weims.Cli.exe create --destination D:\VMs\macOS-Tahoe --disk-gib 200
.\Weims.Cli.exe start --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe status --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe shutdown --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe model --config D:\VMs\macOS-Tahoe\state\config.json --model MacBookPro16,1
```

既存の VM は実際の `state/config.json` を `start` に渡します。ディスクを作り直さないでください。以前のバージョンで作成した VM は、最初の起動時にブートイメージを作り直し、現在のランタイムのグラフィックスドライバーを入れます。Mac の識別情報は変わらず、元のイメージは `OpenCore.img.previous` として残ります。実行環境がない場合は先に `setup` を実行します。GUI と CLI は同じ設定を使用します。更新時に保持したカスタムフォルダーを優先し、それがなければ既存の `%LOCALAPPDATA%\Weims\Manager-Preview` を再利用します。新規インストールは `%LOCALAPPDATA%\Weims\Manager` を使用します。`--data-dir` で別の設定フォルダーを指定できます。同じディスクを二重起動しないでください。`shutdown` は macOS にすべてのアプリを終了してシステムを終了するよう要求します。未保存の書類がある場合は macOS が先に確認し、ゲストの電源が切れると QEMU は終了します。`stop` は 1.1.0 以前で起動し、終了後も QEMU が残った VM にのみ必要です。

[1.2.1 リリースノート](https://github.com/H0yeR404/weims-vgpu/blob/v1.2.1/docs/release-1.2.1.ja.md)。このバージョンにはランタイム 1.2.1 が付属します。1.2.0 から更新した後は、Manager の上部に表示される **Update runtime now** をクリックしてください。Windows キーの Command 動作、実行中のフルスクリーン切り替え、MacBook Pro の機種には新しいランタイムが必要です。1.2.0 より古いランタイムは更新が必須で、更新するまで VM は起動できません。

## 外観、言語、ソフトウェア更新

サイドバーの **Appearance → Light / Dark** で現在のウィンドウと開いているセットアップ画面がすぐに切り替わります。選択は保存され、再起動や未保存設定の破棄は不要です。Windows のハイコントラスト設定が優先されます。既定の言語は英語で、英語と中国語も即時に切り替えられます。

**Software updates → Check for updates** でリリースノートを確認し、**Update now** を選択します。公式インストーラーを取得して SHA256 を検証します。インストール前にすべての QEMU VM とほかの Manager を閉じてください。アプリの更新時のみ Manager を再起動します。1.1.0、1.1.1、1.2.0 は既存の更新機能から 1.2.1 に更新でき、プレビュー版 1.2.0-pre.1 にも表示されます。1.0.1 は一度だけ手動インストールが必要です。

**Update channel** で、更新の確認時に表示されるリリースを選びます。**Stable releases** は正式版のみ、**Preview and stable releases** は両方を表示します。選択していない場合、正式版の Manager は正式版チャンネル、プレビュー版の Manager はプレビュー版チャンネルになります。プレビュー版は Weims Manager Preview という名前で正式版と並んでインストールされ、同じ設定を使います。プレビュー版チャンネルから正式版チャンネルに戻してもダウングレードはされません。インストール済みのプレビュー版は、それより新しい正式版が出るまでそのままです。

更新後も選択した設定ディレクトリを GUI と CLI で使用します。ポータブル版の更新はユーザー用アプリフォルダーへのインストールになります。ランタイムと VM ディスクは置き換えません。旧 Preview アプリは自動削除されないため、更新後は正式版 Weims Manager のショートカットを使用してください。

## ワンクリック実行環境セットアップ

初回起動時は実行環境のページを表示します。**One-click setup** で取得、検証、パスの保存を行い、有効な既存環境は再利用します。Manager が導入した実行環境がこのバージョンに付属するものより古い場合は Manager の上部に通知が表示され、**Update runtime now** で新しいものに切り替わります。1.2.1 で追加された機能には付属のランタイムが必要で、1.2.0 より古いランタイムでは VM を起動できません。自分で指定した実行環境は置き換えません。手動パスは **Advanced** にあります。インポートした VM の起動時も不足コンポーネントを準備します。BIOS の仮想化、Windows 機能、GPU ドライバーは手動対応が必要な場合があります。

## Guest Tools：クリップボード、ファイル、共有フォルダ

VM のウィンドウが前面にある間、Guest Tools が Windows と macOS の間でクリップボードを共有します。テキストとファイルが対象です。また、この PC のフォルダを macOS にマウントします。

1. VM を起動して macOS にログインします。
2. Manager で **Enable Guest Tools integration for this VM** をオンにし、**Install Guest Tools…** をクリックします。macOS にインストールディスクが現れます。
3. macOS でディスクを開き、**Install Weims Guest Tools** をダブルクリックします。現在のユーザーにインストールされ、この VM とペアリングし、ディスクを取り出します。入力は不要です。

片方でコピーし、もう片方で貼り付けます。テキストは 4 MiB までで、パスワードマネージャーが機密と示した内容は相手側のクリップボード履歴に残りません。新しい Manager を入れると macOS 側の Guest Tools は自動で更新されます。削除するには、インストールディスクの **Uninstall Weims Guest Tools.command** を実行します。CLI にも同じ機能があります：`tools-enable`、`tools-install`、`tools-status`。

### ファイルのコピーと貼り付け

エクスプローラーでファイルやフォルダをコピーし、VM のウィンドウをクリックして Finder で貼り付けます。または Finder でコピーし、Windows に切り替えてエクスプローラーで貼り付けます。Guest Tools の **Copy and paste files between Windows and macOS** がそのスイッチです。オフにしない限りオンで、クリップボードの共有がオンの間だけ働きます。

- **Windows から macOS へ。** コピーの時点では何も送りません。貼り付けるときに macOS がこの PC からファイルを読むので、サイズの上限はなく、Finder がいつもの進行状況を表示します。macOS が読めるのは、コピーしたものと、コピーしたフォルダの中身だけです。隣にある他のファイルは読めません。直近 3 回分のコピーは読めるままです。ネットワーク上の場所にあるファイルは対象外で、1 回のコピーで 8 個を超える別々のフォルダにまたがる分も対象外です。Manager が対象外になった項目の数を表示します。
- **macOS から Windows へ。** エクスプローラーはこの PC にあるファイルしか貼り付けられないため、VM のウィンドウを離れたときにファイルを取得します。1 回のコピーにつき 256 MiB までです。Manager は Guest Tools の状態の下に取得の進み具合を表示します。終わってから貼り付けてください。256 MiB を超える場合は共有フォルダを使います。シンボリックリンクは取得しません。Windows の名前に使えない文字は下線に置き換わります。取得したファイルは、次の分が届くか VM が止まるまで、VM の `state\transfers` フォルダに残ります。

エクスプローラーで切り取ったファイルはコピーとして扱います。VM のウィンドウへの、またはウィンドウからのファイルのドラッグには対応していません。

### 共有フォルダ

**Guest Tools → Shared folders** で **Share a folder…** をクリックし、この PC のフォルダを選びます。VM の実行中、Guest Tools がそれを macOS の `/Volumes` にマウントし、ホームフォルダの **Weims Shared Folders** にリンクを置きます。メニューバーの Guest Tools メニューからも開けます。共有してから約 1 秒でフォルダが現れます。共有フォルダは Finder のサイドバーには表示されません。サイドバーに出るのはサーバーのアドレスだけで、そこを開いてもこれらのフォルダはないためです。**Read-only** にチェックを入れると、macOS からは変更できません。一覧は VM の実行中に変更できます。フォルダは一度切断され、すぐに戻ります。

共有フォルダは書類、インストーラ、ソースファイルの受け渡し用です。macOS のディスクではありません。シンボリックリンクは使えず、アプリや Xcode プロジェクトを置く場所には向きません。Finder で開いたフォルダには `.DS_Store` が残ります。共有フォルダの中にあってフォルダの外を指すリンクは、macOS からは開けません。macOS のアプリが共有フォルダを初めて開くとき、ネットワークボリューム上のファイルへのアクセスを許可するか macOS が尋ねます。

Windows 側は何も変更しません。Windows の共有は作成せず、管理者権限も不要で、ファイアウォールにも触れません。Manager 付属のコンポーネントがこの PC のループバックアドレスでフォルダを提供し、ユーザー名とパスワードは毎回作り直されます。

**Upload limit** は、macOS が共有フォルダへ 1 秒あたりに書き込める上限です。既定は 0（無制限）です。macOS が大きなファイルを書き込む間に VM の画面がカクつく場合に設定してください。コマンドライン：`tools-share --folder D:\Work [--name Work] [--access read]`、`tools-unshare --name Work`、`tools-upload-limit --mib 12`。

## ソースからビルド

ソースツリーのランタイムは、上流の [Reims vGPU](https://github.com/steelbrain/reims-vgpu) から直接ビルドします。上流が Windows ホストに対応したため、本プロジェクトが加えるのはウィンドウ用のパッチ 1 つと、ブートイメージ作成ツール reims-util に MacBook Pro を追加するパッチ 1 つです。1.2.0 以降が配布しているのは、こうしてビルドしたランタイムです。

1.1.1 と比べて VM で変わる点：

- macOS の解像度はウィンドウに追従します。ウィンドウの大きさを変えると、画面がそれを埋めます。固定解像度モードと互換モードはなくなりました。
- ゲストから音が出ます。
- キャプチャした入力は Ctrl+Alt+Win で解放します。
- 以前のバージョンで作成した VM は、最初の起動時に OpenCore イメージを作り直し、新しいランタイムのドライバーを入れます。Mac の識別情報は変わりません。元のイメージは `OpenCore.img.previous` として残ります。
- GPU は Vulkan 1.3 に対応している必要があります。

[Git for Windows](https://git-scm.com/downloads/win)、[PowerShell 7](https://github.com/PowerShell/PowerShell/releases)、[MSYS2](https://www.msys2.org/)（空白を含まないパス）、[.NET 10 SDK](https://dotnet.microsoft.com/download) をインストールし、PowerShell 7 で次を実行します。

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu
$env:WEIMS_MSYS2 = "C:\msys64"
pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
pwsh -File manager/build.ps1 -SelfContained
./manager/artifacts/Weims-Manager/Weims.Manager.exe
```

`fetch.ps1` は固定した上流ソースを取得してパッチを適用します。`build.ps1` は MSYS2 の CLANG64 環境で上流自身のインストーラーを実行し（必要な MSYS2 パッケージと Rust ツールチェーンもそこで導入されます）、`build/weims-runtime` を組み立てます。チェックアウトから起動した Manager はこのフォルダーを使います。検証機での初回ビルドは約 10 分でした。詳細、テスト、リリース用パッケージ作成は[ソースからのビルド](docs/reproduce.md)にあります。

## 検証環境

> **互換性について：** 本プロジェクトは現在、以下の実機構成でのみテストし、正常動作を確認しています。他の機器も理論上は対応可能ですが、互換性と正常動作の確認には実機での検証が必要です。

| 項目 | 構成 |
| --- | --- |
| ノート PC | Lenovo Legion Y9000P IRX8H（82WQ） |
| ホスト OS | Windows 11 Pro for Workstations、x86_64 |
| Windows ビルド | 10.0.26300.9550 |
| CPU | Intel Core i9-13900HX |
| GPU | NVIDIA GeForce RTX 4090 Laptop GPU |
| ホストメモリ | 95.81 GiB |
| ホスト画面 | CU34G4 外部モニター、3440 × 1440、180 Hz |
| ホストシェル | PowerShell 7.6.9 |
| 仮想化 / グラフィックス | WHPX / Vulkan、ゲストメモリ共有経路 |
| 検証用ゲスト | macOS Tahoe、8 vCPU、32 GiB メモリ、200 GiB 仮想システムディスク |
| ゲスト解像度 | 1920 × 1080 |

これは検証済みの機器構成であり、最低要件や推奨構成ではありません。ホスト画面のリフレッシュレートはゲストの描画 FPS とは異なります。また、ホストの既存 PowerShell と、自動セットアップが必要に応じて取得する固定版 PowerShell 7.7.0 は別です。

このバージョンのランタイムは、2026-11-24 から 25 日にこのマシンで macOS 26.7.2 ゲストを使って実行しました。起動、描画、自由なポインター移動、ウィンドウのサイズ変更、音声出力、シャットダウン、および Guest Tools のクリップボード、ファイルのコピー＆ペースト、共有フォルダーが動作しました。ウィンドウ移動テストは 1 回で 56 フレーム/秒でした。長期安定性やほかの GPU での動作を示すものではありません。テストした項目としていない項目は[検証記録](docs/validation.md)にあります。

## 上流プロジェクトとライセンス

本プロジェクトは上流の [Reims vGPU](https://github.com/steelbrain/reims-vgpu) をベースに、Windows 上でワンクリックで macOS 仮想マシンを体験・利用できるようにすることを目指しています。

利用している上流プロジェクト：

- **[Reims vGPU](https://github.com/steelbrain/reims-vgpu)**：仮想グラフィックスデバイス、macOS ゲストドライバー、ホスト側ウィンドウ。
- **[metal2vulkan](https://github.com/steelbrain/metal2vulkan)**：Apple の Metal API とシェーダーを Vulkan に変換します。
- **[QEMU Reims](https://github.com/steelbrain/qemu-reims-vgpu)**：このデバイスを組み込んだ QEMU ビルド。

これら三つのプロジェクトの作者である [steelbrain](https://github.com/steelbrain) に感謝します。本プロジェクトは上流の公式リリースではありません。

Reims と metal2vulkan は `GPL-2.0-or-later OR LGPL-3.0-or-later`、QEMU は元のファイルごとのライセンスを維持します。統合スクリプトと文書は GPL-2.0-or-later です。[LICENSE](LICENSE)、[帰属と来歴](NOTICE.md)、[ライセンス全文](licenses/) を参照してください。スクリーンショット内の Apple ソフトウェア、商標、第三者コンテンツの権利は、このリポジトリから許諾されるものではありません。
