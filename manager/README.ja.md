# Weims Manager 1.2.0-pre.1

## クイックスタート — Windows · 1.2.0-pre.1

> **既知の問題：解像度変更時などに VM がフリーズする可能性があります。既知の問題として今後の更新で修正予定です。重要なデータをバックアップしてください。** 管理アプリは開発中です。自動準備から Tahoe 復旧画面までを本機で確認しましたが、新規インストールからデスクトップまでの全工程は未検証です。

必要環境：Windows 10 2004+/11 x64、BIOS/UEFI の仮想化、**Windows ハイパーバイザー プラットフォーム（WHPX）**、`VK_EXT_external_memory_host` 対応の Vulkan GPU とドライバー。「Windows の機能の有効化または無効化」で WHPX を有効にして再起動してください。アプリは BIOS や Windows 機能を自動変更しません。配布版には **WSL2、MSYS2、Rust、.NET SDK は不要**です。GitHub、Apple、上流のダウンロードサーバーへの接続が必要です。

### インストールと VM 作成

1. [プレリリース](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.2.0-pre.1)から `Weims-Manager-1.2.0-pre.1-Setup-x64.exe` またはポータブル ZIP を取得します。インストーラーとアプリには Weims アイコンが含まれます。プレビュー版は Weims Manager Preview として正式版と並べてインストールされます。既定は英語とライトテーマで、サイドバーから言語と外観を即時に切り替えられます。
2. **Weims Manager → Create VM** で新しい保存先、名前、容量（既定 **200 GiB**）を選びます。Apple からの Tahoe 復旧イメージ自動取得を有効のままにするか、互換性のある `BaseSystem.dmg` / `.img` を選択します。完全な ISO と ARM macOS には対応しません。
3. **Create** を押すと PowerShell、QEMU/Reims、検証付き Apple 復旧イメージの取得、固有のマシン識別情報を持つ OpenCore、OVMF、対応グラフィックスドライバーと ROM の準備、イメージ変換、空ディスク作成を行います。WSL 操作、EFI 編集、ファームウェアのコピー、SMC キー入力は不要です。既存の保存先は上書きしません。スパースディスクの容量は実空き容量の予約ではありません。
4. 起動し、OpenCore で復旧項目を選びます（非表示の場合は **Space**）。**ディスクユーティリティ → すべてのデバイスを表示**で容量を確認し、**新規の空システムディスクだけ**を **GUID パーティションマップ / APFS** で消去します。復旧ディスクや OpenCore ディスクを消去しないでください。
5. ディスクユーティリティを閉じ、**macOS Tahoe を再インストール**で新しいボリュームを選びます。ダウンロードと再起動を待ち、進行に応じてインストーラー／システムを選択します。デスクトップに到達したら正常終了し（VM ウィンドウは自動的に閉じます）、復旧イメージの接続を解除し、保存後に再起動します。

新規 VM は**ウィンドウサイズに合わせたゲスト解像度変更**と自由なポインター移動が既定です。ログイン後に独立した Reims ウィンドウをドラッグします。既存 VM は停止後、**Display & Input → Auto fit guest resolution** を選択し、枠なし表示を無効にして保存します。起動解像度は **800–4096 × 600–2160**、**2560×1440 は本機のみで検証済み**です。Fixed は全画面を拡縮し、Compatibility は従来の動作を維持します。応答しなくなった場合はログを保存し、可能なら正常終了してください。クリップボード共有には Guest Tools（後述）が必要です。ファイル共有は含まれません。

### コマンドライン（インストーラー・ポータブル版に同梱）

アプリのディレクトリで Windows PowerShell を開きます。`create` は同じ構成を自動準備します。`--recovery` を省略すると Apple から取得します。

```powershell
.\Weims.Cli.exe create --destination D:\VMs\macOS-Tahoe --disk-gib 200
.\Weims.Cli.exe start --config D:\VMs\macOS-Tahoe\state\config.json --display-mode auto
.\Weims.Cli.exe status --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe shutdown --config D:\VMs\macOS-Tahoe\state\config.json
```

既存 200 GiB VM は実際の `state/config.json` を `start` に渡します。ディスクを作り直さないでください。実行環境がない場合は先に `setup` を実行します。GUI と CLI は同じ設定を使用します。更新時に保持したカスタムフォルダーを優先し、それがなければ既存の `%LOCALAPPDATA%\Weims\Manager-Preview` を再利用します。新規インストールは `%LOCALAPPDATA%\Weims\Manager` を使用します。`--data-dir` で別の設定フォルダーを指定できます。同じディスクを二重起動しないでください。`shutdown` は macOS にすべてのアプリを終了してシステムを終了するよう要求します。未保存の書類がある場合は macOS が先に確認し、ゲストの電源が切れると QEMU は終了します。`stop` は 1.1.0 以前で起動し、終了後も QEMU が残った VM にのみ必要です。

[1.2.0-pre.1 リリースノート](https://github.com/H0yeR404/weims-vgpu/blob/v1.2.0-pre.1/docs/prerelease-1.2.0-pre.1.ja.md)。このバージョンにはランタイム 1.2.0-pre.1 が付属し、VM を閉じると QEMU がすぐに終了します。

## 外観、言語、ソフトウェア更新

サイドバーの **Appearance → Light / Dark** で現在のウィンドウと開いているセットアップ画面がすぐに切り替わります。選択は保存され、再起動や未保存設定の破棄は不要です。Windows のハイコントラスト設定が優先されます。既定の言語は英語で、英語と中国語も即時に切り替えられます。

**Software updates → Check for updates** でリリースノートを確認し、**Update now** を選択します。公式インストーラーを取得して SHA256 を検証します。インストール前にすべての QEMU VM とほかの Manager を閉じてください。アプリの更新時のみ Manager を再起動します。プレビュー版には新しいプレビュー版と正式版が表示されます。正式版には正式版のみが表示されるため、1.1.1 のユーザーはプレビュー版を手動でインストールします。

更新後も選択した設定ディレクトリを GUI と CLI で使用します。ポータブル版の更新はユーザー用アプリフォルダーへのインストールになります。ランタイムと VM ディスクは置き換えません。旧 Preview アプリは自動削除されないため、更新後は正式版 Weims Manager のショートカットを使用してください。

## ワンクリック実行環境セットアップ

初回起動時は実行環境のページを表示します。**One-click setup** で取得、検証、パスの保存を行い、有効な既存環境は再利用します。Manager が導入した実行環境がこのバージョンに付属するものより古い場合は実行環境のページに表示され、**One-click setup** で新しいものに切り替わります。自分で指定した実行環境は置き換えません。手動パスは **Advanced** にあります。インポートした VM の起動時も不足コンポーネントを準備します。BIOS の仮想化、Windows 機能、GPU ドライバーは手動対応が必要な場合があります。

## Guest Tools：クリップボード共有

VM のウィンドウが前面にある間、Guest Tools が Windows と macOS の間でクリップボードのテキストを共有します。

1. VM を起動して macOS にログインします。
2. Manager で **Enable Guest Tools integration for this VM** をオンにし、**Install Guest Tools…** をクリックします。macOS にインストールディスクが現れます。
3. macOS でディスクを開き、**Install Weims Guest Tools** をダブルクリックします。現在のユーザーにインストールされ、この VM とペアリングし、ディスクを取り出します。入力は不要です。

片方でコピーし、もう片方で貼り付けます。テキストは 4 MiB までで、パスワードマネージャーが機密と示した内容は相手側のクリップボード履歴に残りません。新しい Manager を入れると macOS 側の Guest Tools は自動で更新されます。削除するには、インストールディスクの **Uninstall Weims Guest Tools.command** を実行します。CLI にも同じ機能があります：`tools-enable`、`tools-install`、`tools-status`。
