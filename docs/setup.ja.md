# TabSpan の導入手順

TabSpan は、Windows の PC の画面を Android の端末に映すソフトです。端末を触ると、PC がその位置でタッチされます。
この手順では、PC 側と端末側を入れて、最初につなぐまでを説明します。

> [!WARNING]
> 開発中のソフトです。確かめた端末は dtab d-01J(Android 7.0)だけです。

## 1. 用意する物

| 物 | 要る場面 | 入れ方 |
|---|---|---|
| Windows 11(x64)の PC | いつも | — |
| .NET 10 SDK | PC 側をソースからビルドするとき | <https://dotnet.microsoft.com/download> |
| adb(Android SDK Platform-Tools) | USB でつなぐとき | 下の 2.2 |
| Android 7.0 以上の端末 | いつも | 開発者向けオプションで USB デバッグをオン(下の 2.3) |
| Virtual Display Driver | 拡張(デスクトップの続きを映す)で使うとき | 下の 2.1。複製だけなら要りません |

TabSpan は、管理者の権限を求めません。ドライバや adb を自分で入れたり、設定を書き換えたりもしません。

## 2. 下準備

### 2.1 仮想ディスプレイ(拡張で使う人だけ)

PowerShell で次を実行します。管理者の確認が出たら、内容を確かめて許可してください(入れるのは TabSpan ではなく winget です)。

```powershell
winget install --id=VirtualDrivers.Virtual-Display-Driver -e
```

入れた後、TabSpan の「映す画面」で「一覧を更新」を押すと、「仮想」の印が付いたモニターが出ます。それを選ぶと拡張、実在のモニターを選ぶと複製になります。

### 2.2 adb(USB でつなぐ人だけ)

普通の(管理者でない)PowerShell で次を実行し、PowerShell を開き直します。

```powershell
winget install Google.PlatformTools
```

TabSpan は、設定で指定した場所 → PATH → winget の既定の場所(`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Google.PlatformTools_*\platform-tools\adb.exe`)の順に adb を探します。

### 2.3 端末の USB デバッグ

1. 端末の設定 → 端末情報 → ビルド番号を 7 回押します(開発者向けオプションが出ます)。
2. 開発者向けオプション → USB デバッグをオンにします。
3. USB で PC につなぎ、端末に出る「USB デバッグを許可しますか」で「許可」を押します。

設定の場所は機種によって違います。

## 3. 入れる

1. PC 側をビルドします(ビルド済みの exe は、まだ配っていません)。

   ```powershell
   dotnet build pc\TabSpan.sln -c Release
   ```

   `pc\src\TabSpan.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\TabSpan.App.exe` ができます。.NET ごと 1 つのフォルダにまとめたいときは `tools\publish.ps1` を使います(下の 6)。
2. GitHub の Release から `TabSpan-<版>-android.apk` を取り、端末に入れます。

   ```powershell
   adb install -r TabSpan-<版>-android.apk
   ```

   PC 側と端末側は同じ版にします。

## 4. 最初につなぐ(ペアリング)

1. PC で `TabSpan.App.exe` を起動します。
2. 「映す画面」から映すモニターを選びます。
3. 「端末を追加」を押します。端末からの要求を 120 秒待ちます。
4. 端末で TabSpan を開き、「PC を追加」を押します。
5. PC と端末に 6 桁の番号が出ます。**同じなら**、PC で「許可」を押します。違うときは「拒否」を押してください。
6. 端末が自動で PC につながり、映ります。

ペアリングは USB でつないでいるときだけできます(LAN からはできません)。

## 5. LAN でつなぐ

1. 一度 USB でペアリングしておきます。
2. PC の設定で「LAN でつなぐ」をオンにします。
3. **Windows のファイアウォールの確認が出たら、プライベート ネットワークだけを許可してください。** パブリック ネットワーク(駅やお店の Wi-Fi など)では許可しないでください。
4. 端末を PC と同じネットワークにつなぎ、TabSpan を開きます。端末は 127.0.0.1(USB)→ 前回のアドレス → 手で入れたアドレス → 探索の順に試します。
5. 見つからないときは、端末の設定で PC の行から「アドレスを手で入れる」を選び、PC の IPv4 アドレスを入れます。

LAN では、ペアリングした端末だけがつなげます。PC は、ペアリングしていない端末の探索には返事をしません。

## 6. 一式を作る(`tools\publish.ps1`)

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

`dist\` に、.NET を同梱した PC 側のフォルダの zip と、端末側の APK を、版の番号付きで作ります。SHA-256 も出します。

## 7. 知っておくこと

- 管理者の権限で動いているウィンドウ(タスクマネージャーなど)には、タッチが届きません(Windows の決まり)。
- ロック画面、UAC の確認の画面、著作権で保護された動画は、映らないか黒くなることがあります(未確認)。
- 切断してから一定の時間(既定 30 秒)つなぎ直しが無ければ、仮想ディスプレイをデスクトップから外します。外すと、その画面にあったウィンドウは残りの画面へ移ります。つなぎ直すと元の位置に戻します。実在のモニターには触りません。
- 端末を縦にしても、映像の画面は横向きのままです。
- ペアリングをやり直すときは、PC の設定で端末を解除し、端末の設定で PC を解除してから、4 の手順をもう一度行います。
