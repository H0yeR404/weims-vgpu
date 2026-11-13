# Weims Manager Preview 1.1.0-pre.1

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

[プレリリースの説明](https://github.com/H0yeR404/weims-vgpu/blob/v1.1.0-pre.1/docs/prerelease-1.1.0-pre.1.ja.md) · [安定版 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1)。安定版には自動ブート準備と可変解像度は含まれません。
