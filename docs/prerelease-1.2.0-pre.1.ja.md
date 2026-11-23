# Weims Manager 1.2.0-pre.1

1.2 のプレビュー版です。Windows の GUI と CLI、macOS 用の Guest Tools、新しい QEMU/Reims ランタイムを含みます。正式版 1.1.1 が引き続き Latest です。

## 新機能

- **Guest Tools：クリップボード共有。** VM のウィンドウが前面にある間、Windows でコピーしたテキストを macOS に貼り付けられ、その逆も可能です。テキストは 4 MiB までです。パスワードマネージャーが機密と示した内容は、相手側のクリップボード履歴に残りません。
- **Manager からインストール。** **Enable Guest Tools integration for this VM** をオンにして **Install Guest Tools…** をクリックすると、macOS にインストールディスクが現れます。その中の **Install Weims Guest Tools** をダブルクリックします。現在の macOS ユーザーにインストールされ、この VM とペアリングし、ディスクを取り出します。入力も数字の照合も不要です。
- **Guest Tools の自動更新。** 新しい Manager に新しい Guest Tools が付属している場合、macOS 側の Guest Tools はホストとの接続を通じて受け取り、再起動します。自分と同じ署名のものだけを受け入れます。
- **ランタイム 1.2.0-pre.1：QEMU がすぐに終了。** 以前のランタイムでは、ウィンドウを閉じた後や macOS のシャットダウン後に QEMU が数分間残ることがあり、その間 VM はロックされたままでした。終了時にランタイムが、Windows がすでに終了させたグラフィックススレッドを、登録されたハンドラーごとに 2 秒ずつ待っていたためです。現在はスレッドがないことを検出して待ちません。
- **古いランタイムの置き換え。** Manager が導入したランタイムがこのバージョンに付属するものより古い場合、起動時と Runtime ページに表示され、**One-click setup** で新しいものを並べて導入して切り替えます。自分で指定したランタイムは置き換えません。VM の起動時には何もダウンロードしません。
- Manager、CLI、Guest Tools のホスト側プロセスは .NET 10 でビルドされています。パッケージは自己完結型で、別途インストールは不要です。

コマンドラインにも同じ機能があります：`tools-enable`、`tools-disable`、`tools-install`、`tools-status`、`tools-pair`、`tools-reset-pairing`。

## インストール / 更新

`Weims-Manager-1.2.0-pre.1-Setup-x64.exe` をダウンロードするか、`Weims-Manager-1.2.0-pre.1-win-x64.zip` を展開します。プレビュー版は **Weims Manager Preview** として正式版と並べてインストールされ、同じ設定と VM を使います。

正式版には正式版の更新だけが表示されるため、1.1.1 のユーザーはこのプレビュー版を手動でインストールします。プレビュー版からは **Software updates → Check for updates** で見つかります。

インストール後、**Runtime** を開いて **One-click setup** をクリックし、ランタイム 1.2.0-pre.1（95 MB）を取得します。既存の VM に変更は不要です。実行中の VM は、再起動するまで起動時のランタイムを使い続けます。

macOS から Guest Tools を削除するには、インストールディスクの **Uninstall Weims Guest Tools.command** を実行します。

## Guest Tools の接続方法

連携は既定でオフで、VM ごとに切り替えます。オンの間、ホスト上の小さなプロセス（`Weims.Broker`）がループバックインターフェースでのみ待ち受け、macOS は VM 自身のネットワーク経由で接続します。接続は TLS で、証明書はこの VM 専用に作られ、Guest Tools がペアリング時に記憶します。QEMU のコマンドラインには何も追加されず、ゲストが QEMU の制御チャネルにアクセスできるようにもなりません。

Guest Tools は Apple 発行の証明書ではなく、本プロジェクト独自の証明書（`guest-tools/signing-certificate.pem`）で署名されています。

## 既知の問題

- 使用中の VM フリーズ（解像度変更時を含む）は引き続き調査中です。
- Intel グラフィックスでの描画の問題（[issue #2](https://github.com/H0yeR404/weims-vgpu/issues/2)）は、このランタイムでは修正されていません。
- Guest Tools が運ぶのはテキストのみです。画像、ファイル、ドラッグ＆ドロップは含まれません。

## 検証

自動テスト：コア 97 件、Guest Tools ホスト側プロセス 161 件、WPF 66 件、更新選択 22 件。

実際のゲスト（macOS 26.7.2、NVIDIA GPU 搭載の Windows 11 ホスト 1 台）：Gatekeeper 有効の状態で Manager のボタンと CLI から Guest Tools をインストール。CJK、絵文字、4 MiB を含む双方向のテキスト。Guest Tools またはホスト側プロセスを終了させた後の復帰。ホストからの Guest Tools 更新。終了の修正は、グラフィックススレッドを GPU ドライバー内で停止させてから終了する方法で確認しました。新しいランタイムは 4 秒で終了し、古いランタイムは 20 分後も残っていました。

ランタイム ZIP と対応するソースアーカイブを添付し、すべてのファイルに SHA256 チェックサムを付けています。
