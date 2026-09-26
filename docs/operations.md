# 運用手順（変換 → Unity → Quest 3 実機）

## 1. データ変換（PC）

```powershell
# 初回のみ（Rust は rustup 導入済み。cargo は %USERPROFILE%\.cargo\bin）
cd <リポジトリルート>\tools\splat-pipeline
cargo build --release

# 変換（tiny-lod 既定。--quality で bhatt-lod）
.\target\release\build-lod-unity.exe <scene>.ply --out ..\..\StreamingData\<name> --name <name> --force
```

- 対応入力: .ply / .compressed.ply / .spz / .splat / .ksplat / .sog
- 主要オプション: `--coord identity|flip-x|flip-y|flip-z`（座標系焼き込み。既定 `flip-x`）、`--lod-base <f32>`
  （大きくするとツリーが浅くなりスライスが速くなるが LoD が粗くなる。既定 1.5、`--quality` 時 1.75）
- exit code: 0=成功 / 1=入力エラー / 2=内部エラー / 3=自己検証失敗
- 出力: `<name>.usst`（常駐ツリー）、`<name>_XXXX.usc`（2MB チャンク群）、`<name>.manifest.json`
  （統計。quantization 誤差や treeDepth はここで確認）

Unity メニュー **Tools > Gsplat LoD > Setup Window** の Convert セクションからも実行可能。
こちらは `build-lod-unity.exe` を外部プロセスとして起動する方式で、ネイティブ
プラグインは使わない（Editor はネイティブプラグインを再起動までアンロードしないため）。

### ネイティブプラグインのビルド（実機変換用）

ビルド済みのものを同梱しているので、`gsplat-convert-ffi` を変更したときだけ必要。変換パイプラインを C ABI の cdylib
（`tools/splat-pipeline/gsplat-convert-ffi/`、`[lib] name = "gsplat_convert"`）として
切り出し、Unity から P/Invoke で呼べるようにしたもの。

```powershell
# 初回のみ
rustup target add aarch64-linux-android

# 既定は windows/android 両方
.\tools\build-native.ps1
# 個別指定・NDK パス指定も可能
.\tools\build-native.ps1 -Target android -NdkPath <NDKのパス>
```

- `-Target windows|android|all`（既定 all）、`-NdkPath <path>`
- NDK 探索順: `-NdkPath` → 環境変数 `ANDROID_NDK_ROOT` → `ProjectVersion.txt` の
  Unity バージョンから Unity 同梱 NDK を解決（Unity Hub の既定の場所以外に Editor を入れている場合は
  環境変数 `UNITY_EDITOR_ROOT` にそのフォルダを設定）
- cargo-ndk は使わず、NDK 同梱の `aarch64-linux-android32-clang.cmd`（API 32 = minSdk に
  一致）をリンカに指定している
- 検証時に使用した NDK: Unity 6000.3.11f1 同梱、`Pkg.Revision = 27.2.12479018`（r27c）
- 成果物の配置先:
  - `Packages/com.gsplat.lod/Runtime/Plugins/x86_64/gsplat_convert.dll`
  - `Packages/com.gsplat.lod/Runtime/Plugins/Android/arm64-v8a/libgsplat_convert.so`
- **Unity Editor が起動中だとネイティブプラグインを掴んでいてコピーに失敗する**。
  Editor を終了してから実行する

## 2. Unity での確認（Editor）

Setup Window で `.usst` を選び、目的別にオブジェクトを生成:

| ボタン | 用途 |
|---|---|
| Create Streaming Object | 実運用構成（固定プール + ストリーミング） |

- パラメータは `Assets/GsplatLod/LodStreamingConfig.asset`
  - `BudgetSplats`: 描画予算 N（LoD を目視したいときはシーンの葉数より小さく）
  - `MeasureSliceSync`: ✓でスライスを同期実行し HUD に正確な実行時間表示（計測時のみ ON）
- HUD（LodDebugHud）: fps / frontier cost / pool 使用率 / load queue / slice ms / fallback items
- **Console に `Burst is disabled ...` が出ていないことを必ず確認**（出ているとスライスが大幅に遅くなる。
  ジョブ内に Burst 非対応 API を追加していないか確認する）

## 3. Quest 3 実機

1. ビルドシーンにストリーミングオブジェクトを配置。`UsstPath` は**相対パス**（例 `my-scene/my-scene.usst`）、
   `GammaToLinear` は Color Space が Linear なら✓
2. Build And Run（Android / IL2CPP / ARM64 / Vulkan — MR テンプレート既定のまま）
3. アプリを一度起動（persistentDataPath のフォルダを作らせる）
4. データ転送:
   ```powershell
   adb push <リポジトリルート>\StreamingData\<name> /sdcard/Android/data/<パッケージ名>/files/<name>
   ```
5. アプリ再起動 → 確認:
   ```powershell
   adb logcat -s Unity   # "[GsplatLod] driver ready" とエラー有無
   adb shell dumpsys meminfo <パッケージ名>   # メモリ使用量
   ```
- fps は OVR Metrics Tool（HUD オーバーレイ）か Meta Quest Developer Hub で計測
- VR 内で HUD を見たい場合: シーンに 3D Text (TextMesh) を置き LodDebugHud の TargetText に割り当て

## 4. 実機での変換（アプリ内 .ply → .usst/.usc）

同梱のネイティブプラグイン（`libgsplat_convert.so`）を使い、PC で変換せずに
実機上で `.ply` 等から `.usst`/`.usc` を生成できる。実機 UI（`LodSetupPanel`）に変換セクションと
Convert ボタンがある。出力先は `Application.persistentDataPath` 配下固定。

### 入力ファイルの選択（Android 標準ファイルピッカー / SAF）

入力ファイルは Android 標準のファイルピッカー（SAF、`ACTION_OPEN_DOCUMENT`）から選ぶ。
`Assets/GsplatFilePicker.androidlib/` が実体で、AAR の事前ビルドは不要（Unity の Gradle
ビルドが Java ソースから直接コンパイルする）。選択結果は `content://` URI ではなく POSIX
ファイルディスクリプタとして C# 側（`SplatFilePicker.cs`）に渡り、Rust 側がそのまま読む。
**ファイルのコピーは発生しない**。**ストレージ権限は一切不要**（`MANAGE_EXTERNAL_STORAGE` /
`READ_EXTERNAL_STORAGE` とも未宣言）。

adb push した場合のフォールバックとして、`Application.persistentDataPath` 直下のファイル
一覧もパネルに表示される。

1. ピッカーで変換したい `.ply` 等を選ぶ、または事前に adb push しておいたファイルを
   フォールバックの一覧から選ぶ
   ```powershell
   # フォールバック経路（adb push する場合）
   adb push <scene>.ply /sdcard/Android/data/<パッケージ名>/files/<転送先フォルダ>
   ```
2. パネルの変換セクションで入力ファイルを選んで Convert
   - 変換はメモリを大量に消費する。`SplatConverter.CheckInputSize` が必要 RAM を概算し、
     実機では閾値超過時に変換を弾く。PLY はヘッダの splat 数から見積もり（tiny-lod で
     約 209 B/splat、bhatt-lod (Quality) で約 361 B/splat に、入力の SH 係数分を上乗せ、
     固定 32MB・安全係数 1.5 倍）。PLY 以外や ascii PLY は拡張子別の倍率でファイルサイズ
     から見積もる。上限は `SystemInfo.systemMemorySize`（物理 RAM）の 40%、下限 768MB。
     閾値に達しても「警告を無視して続行」で越えられる（ネイティブ側の OOM は abort で
     プロセスが即死し C# では捕捉できないため、既定は拒否）
   - 実機での変換は数分かかる前提。進捗はパネルのステータス行に出る
   - **キャンセルは即座には効かない**。decode 全体・`.usc` 書き出し・validate がノーログ区間
     のため、そこに入っている間はキャンセル要求を出しても反映が遅れる
3. 変換が終わると出力（`.usst`/`.usc`/`.manifest.json`）はそのまま
   `Application.persistentDataPath` 配下にあるため、追加の adb push は不要。
   `LodStreamingDriver.UsstPath` に相対パスを設定してアプリを再起動すれば表示できる

### 既知の制約: ピッカーのウィンドウが狭い

Quest 3 ではピッカーが常に 500×800 px で開く。ピッカーは DocumentsUI 自身のタスクで
別ウィンドウとして開くため、アプリ側からサイズを変える手段はない
（`ActivityOptions.setLaunchBounds()` は無視される）。必要ならユーザーが手でパネルを広げる。

### 実機での動作実績（2026-09-16、Quest 3）

- 入力 `scene_sh2.ply`（370.4MB、SH degree 2、2,368,164 splat）
- Quality: High（bhatt-lod）で変換成功。出力 3,103,176 splat / 48 チャンク /
  `.usst` 29,401,312 B / `.usc` 合計 99,301,632 B
- Fast（tiny-lod）と High の両方で成功し、変換後の Load（描画）も確認済み
- High は Fast より大幅に時間がかかる（bhatt-lod は近傍から最も似た 1 個を選ぶペア統合の
  二分木構築、tiny-lod はセル単位の一括統合）

### 実機でのピークメモリ・所要時間の目安（2026-09-16、Quest 3、Quality High）

上記と同じ入力（370.4MB、2,368,164 splat、bhatt-lod）を変換した実測値。

| 項目 | 値 |
|---|---|
| ベースライン（変換前、VR レンダラ込みのアプリ全体） | 約 372 MB |
| ピーク `VmHWM` | 1,283 MB |
| 変換が追加で要求したメモリ | 約 911 MB（約 403 B/splat） |
| 変換完了後 | 554 MB |
| 所要時間 | 約 210 秒 |

デコード中に一時的なピーク（`VmHWM` 1,037 MB）が立ち、LoD ツリー構築で段階的に増加して
207 秒地点で最終ピークに達し、完了直後に 554 MB まで落ちる。他の入力サイズでの目安は 1 splat あたり
約 403 B に splat 数を掛けて概算できる。

測定方法: `/proc/<pid>/status` の `VmHWM`（プロセス開始からの `VmRSS` ピーク）を 1 秒間隔で
サンプリング。Quest は root が取れず `clear_refs` でカウンタをリセットできないため、
変換前のベースラインを先に記録し、その差分を「変換が追加で要求したメモリ」として扱う。

Quality Fast（tiny-lod）での実機ピークメモリ、および PLY 以外の入力形式（.spz / .splat /
.ksplat / .sog / .zip）でのピークメモリは未測定。

## 5. トラブルシューティング

| 症状 | 原因の見当 / 対処 |
|---|---|
| ロード時ヒッチ | `MaxUploadBytesPerFrame` を 2MB に下げる |
| 精細化が遅い | `MaxConcurrentLoads` 増、`PrefetchEnabled` を確認、ストレージ実効帯域を HUD の loaded MB で確認 |
| fps 不足（常時） | `BudgetSplats` を下げる |
| .usst が数百 MB | `--lod-base 2.0` で再変換（ノード数削減） |
| 表示されない | logcat で `chunk 0 missing` / パス解決を確認。シェーダ参照が Inspector に入っているか |
| `Burst is disabled` | ジョブ呼び出しグラフに Burst 非対応 API（例: Stopwatch.GetTimestamp）が入った。除去する |
| チャンク欠損エラー | 転送漏れ。`<name>_0000.usc` 〜 全ファイルが揃っているか `adb shell ls` で確認 |
