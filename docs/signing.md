# APK の署名

いま Release に置いている APK は、Android Studio の debug の鍵で署名した仮の物です。正式な鍵に変えると、署名が変わるので、端末の TabSpan をいったん消して入れ直す必要があります(ペアリングもやり直しになります)。

## 鍵の決まり

- 鍵(`.jks` / `.keystore`)とパスワードを、リポジトリに入れません(`.gitignore` で `*.jks` と `*.keystore` を外しています)。
- 鍵は作者の PC の、リポジトリの外に置きます。置き場所はまだ決めていません。
- 鍵を失うと、同じアプリとして更新できなくなります。リポジトリの外に控えを持ちます。

## 鍵を作る(1 回だけ)

JDK の `keytool` で作ります。

```powershell
keytool -genkeypair -v -keystore <リポジトリの外>\tabspan-release.jks -alias tabspan -keyalg EC -groupname secp256r1 -validity 10000
```

## 署名した APK を作る

`tools\publish.ps1` は、次の環境変数があるときだけ release の APK を作って署名します。無ければ debug の鍵の APK を作ります。

| 環境変数 | 中身 |
|---|---|
| `TABSPAN_KEYSTORE` | 鍵のファイルのパス |
| `TABSPAN_KEYSTORE_PASSWORD` | 鍵のファイルのパスワード |
| `TABSPAN_KEY_ALIAS` | 鍵の別名(上の例では `tabspan`) |
| `TABSPAN_KEY_PASSWORD` | 鍵のパスワード |

```powershell
$env:TABSPAN_KEYSTORE = '<リポジトリの外>\tabspan-release.jks'
$env:TABSPAN_KEYSTORE_PASSWORD = Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText  # PowerShell 7 の場合
$env:TABSPAN_KEY_ALIAS = 'tabspan'
$env:TABSPAN_KEY_PASSWORD = $env:TABSPAN_KEYSTORE_PASSWORD
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -SkipPc
```

パスワードは、履歴に残らないように打ち込みで渡します。終わったら環境変数を消します。

## 確かめる

```powershell
apksigner verify --print-certs dist\TabSpan-v<版>-android.apk
```

出た証明書の SHA-256 を、Release の説明に書きます。
