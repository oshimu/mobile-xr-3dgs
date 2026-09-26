# .usst / .usc フォーマットとランタイム仕様

3D Gaussian Splatting シーンを LoD ツリー付きの独自フォーマットに変換し、Unity で視点に応じて
必要な部分だけを読み込んで描画する仕組みの仕様。本書は現在の実装（フォーマット version 2）を記述する。

- LoD ツリーの構築は [Spark](https://github.com/sparkjsdev/spark) の実装（`spark-lib`）をそのまま使う
- ファイルフォーマット、Unity 向けエンコーダ、Unity ランタイムは本プロジェクトの実装

---

## 1. 範囲

### 1.1 目的

`.ply` を丸ごと RAM に展開して GPU に送る方式では、シーンの大きさに比例してメモリとロード時間が
増える。本システムはオフラインで LoD ツリーとチャンク分割を済ませておき、実行時は視点から必要な
ノードだけを固定サイズの GPU プールに読み込む。

### 1.2 設計上の性質

- R1: GPU のスプラットプールは固定サイズ（上限あり）。常駐するのは プール + ノードテーブル +
  固定ステージング。ノードテーブルはシーンの内部ノード数に比例する（実測で葉数の約 0.57 倍 × 40B）
- R2: 最初に粗い全景を描き、視点周辺から精細化する。未ロードのチャンクは祖先ノードで代替する（§5.5）
- R3: 1 フレームに描画するスプラット数は予算 N で打ち切る
- R4: 対象プラットフォームは Meta Quest 3（Android / Vulkan / URP / Single Pass Instanced）と
  XREAL（Android / OpenGL ES3）。Windows（Editor / Standalone）でも動作する
- R5: データはローカルストレージ（`Application.persistentDataPath` 配下）から読む

### 1.3 対象外

- スプラットの編集・リライティング
- 4DGS（アニメーション）
- .RAD フォーマット互換（入力としては読めるが、ランタイムは `.usst` / `.usc` のみ読む）
- SH1 以上（SH0 = 視点非依存色のみ）
- ネットワーク経由のチャンク配信

---

## 2. 全体構成

```
【オフライン（Rust CLI: build-lod-unity / ネイティブプラグイン: gsplat_convert）】

  scene.ply / .spz / ...
      │  MultiDecoder                （spark-lib）
      ▼
  全スプラット
      │  tiny_lod / bhatt_lod        （spark-lib：LoD ツリー構築）
      │  chunk_tree                  （spark-lib：空間的並べ替え）
      │  coord                       座標系プリセットの焼き込み（§4.6）
      │  UnityEncoder                並び順の再割当て + 書き出し（§4.4, §4.5）
      ▼
  scene.usst           ヘッダ + ノードテーブル + チャンク索引
  scene_0000.usc       スプラットチャンク（65,536 スプラット / 2MB）
  scene_0001.usc       ...
  scene.manifest.json  統計（ランタイム未使用）

【ランタイム（Unity）】

  SplatSceneAsset       .usst を読み、ノードテーブルを常駐
  TreeSlicer            視点からツリーを切り出し → 描画アイテム列 + 必要チャンク集合（Burst Job）
  ChunkStreamer         不足チャンクを I/O スレッドで読み込み
  SplatPagePool         固定 GraphicsBuffer プール。1 チャンク = 1 ページ
  VisibleIndexBuildJob  描画アイテム → 可視インデックス列（未ロードは祖先で代替）
  DepthSortJob          可視インデックスを奥から手前にソート（Burst, CPU）
  GsplatLodRenderer     ソート済みインデックス順に描画
```

| モジュール | 実装先 |
|---|---|
| 入力デコード、LoD ツリー構築、chunk_tree | `tools/splat-pipeline/spark-lib/`（Spark、無改造） |
| UnityEncoder、CLI、自己検証 | `tools/splat-pipeline/build-lod-unity/` |
| 実機変換用ネイティブプラグイン | `tools/splat-pipeline/gsplat-convert-ffi/` |
| ランタイム | `Packages/com.gsplat.lod/` |

---

## 3. ファイルフォーマット

### 3.1 共通規約

- リトルエンディアン
- 全構造体は 4 バイト境界
- バージョンが一致しない場合、ランタイムは例外を投げてロードを中止する（互換読み込みはしない）

### 3.2 スプラットレコード（32 bytes）

`.usc` の 1 スプラット。GPU の `StructuredBuffer` にそのまま転送できるレイアウト。

| offset | size | type | field | エンコード |
|---|---|---|---|---|
| 0 | 12 | float32 ×3 | position | Unity 座標系（変換はオフラインで焼き込み、§4.6） |
| 12 | 8 | unorm16 ×4 | rotation (x,y,z,w) | `u = round((clamp(v,-1,1) * 0.5 + 0.5) * 65535)`。シェーダでデコード後に正規化 |
| 20 | 6 | float16 ×3 | scale (sx,sy,sz) | 実スケール（対数ではない） |
| 26 | 1 | uint8 | lodOpacity | 0 = 葉。1〜255 = LoD 代表スプラットの LoD 不透明度 D（1〜5）を `1 + 254 × (D-1)/4` で格納 |
| 27 | 1 | uint8 | reserved | 0 |
| 28 | 4 | unorm8 ×4 | color RGBA | RGB = `clamp(0.5 + SH_C0 * dc, 0, 1)`。A = opacity（sigmoid 適用済み）。代表スプラットの A は未使用 |

- SH_C0 = 0.28209479177387814
- 内部ノードの代表（マージ済み）スプラットも同じ形式で、葉と同じチャンク列に格納する
- LoD 不透明度 D は Spark の `encode_lod_opacity()` と同じ定義。シェーダは D > 1 のとき描画範囲を
  `maxStdDev + 0.7 × (D - 1)` 標準偏差まで広げ、不透明度を D から求める（Spark の `splatVertex.glsl` と同じ）

HLSL 側の対応構造体:

```hlsl
struct SplatRecord      // 32 bytes
{
    float3 position;    // 12
    uint   rot01;       // x:low16, y:high16
    uint   rot23;       // z:low16, w:high16
    uint   scaleXY;     // half sx: low16, half sy: high16
    uint   scaleZ_flags;// half sz: low16, lodOpacity: bits 16-23
    uint   rgba;        // packed unorm8x4
};
```

### 3.3 グローバルスプラットインデックス

- 全スプラット（葉 + 代表）に 0 始まりの通し番号 globalIndex（uint32）を振る
- `chunkId = globalIndex >> 16`、`indexInChunk = globalIndex & 0xFFFF`（1 チャンク = 65,536 固定）
- 最終チャンクのみ端数を許す

### 3.4 .usst（ツリーファイル）

```
[Header 64B] [NodeTable nodeCount×40B] [ChunkIndex chunkCount×16B]
```

**Header（64 bytes）**

| offset | type | field | 値 |
|---|---|---|---|
| 0 | char[4] | magic | "USST" |
| 4 | uint32 | version | 2 |
| 8 | uint32 | nodeCount | 内部ノード数 |
| 12 | uint32 | chunkCount | |
| 16 | uint32 | chunkSplats | 65536 |
| 20 | uint64 | totalSplats | 葉 + 代表 |
| 28 | uint64 | leafSplats | 葉のみ |
| 36 | uint32 | rootNodeIndex | 0 |
| 40 | float32 ×6 | sceneAabb (min.xyz, max.xyz) | 描画範囲を含む |

**NodeRecord（40 bytes）— 内部ノードのみ**

| offset | type | field | 備考 |
|---|---|---|---|
| 0 | float32 ×3 | center | ノード中心 |
| 12 | float32 | radius | バウンディング球半径（子と、描画範囲を含む） |
| 16 | float32 | featureSize | LoD 優先度の基準（Spark の `feature_size()`） |
| 20 | uint32 | mergedSplatIndex | このノードの代表スプラットの globalIndex |
| 24 | uint32 | firstInteriorChild | 内部子ノードの先頭インデックス（連続配置）。なし = 0xFFFFFFFF |
| 28 | uint16 | interiorChildCount | |
| 30 | uint16 | flags | 予約（0） |
| 32 | uint32 | directLeafFirst | 直接の子である葉の先頭 globalIndex（連続配置）。なし = 0xFFFFFFFF |
| 36 | uint32 | directLeafCount | |

葉はノード化せず、親ノードから (first, count) で参照する。1 ノードの子は「内部子ノード 0..n 個 +
直接の葉 0..m 個」の混在を許す。

**ChunkIndexEntry（16 bytes）**

| offset | type | field |
|---|---|---|
| 0 | uint32 | chunkId |
| 4 | uint32 | splatCount |
| 8 | uint64 | byteLength（= splatCount × 32） |

チャンクの実体はファイル名規約 `<name>_%04d.usc` で解決する（索引にパスは持たない）。

### 3.5 .usc（スプラットチャンク）

ヘッダなし。SplatRecord × splatCount の生配列（最大 2,097,152 bytes）。
`FileStream.Read` → `GraphicsBuffer.SetData` を中間変換なしで行うため。
整合性はファイルサイズと ChunkIndex の byteLength の照合で確認する。

### 3.6 manifest.json（ランタイム未使用）

```json
{
  "source": "scene.ply",
  "generatedAt": "ISO8601",
  "generator": { "tool": "build-lod-unity", "version": "x.y.z", "sparkLibCommit": "sha" },
  "params": { "lodMethod": "tiny|bhatt", "lodBase": 1.5, "maxSh": 0, "chunkSplats": 65536,
              "regionSplats": 8192, "coordPreset": "flip-x" },
  "stats": {
    "inputSplats": 0, "leafSplats": 0, "interiorNodes": 0, "totalSplats": 0,
    "treeDepth": 0, "backboneSplats": 0, "chunkCount": 0,
    "maxInteriorChildren": 0, "maxDirectLeaves": 0, "usstBytes": 0, "uscTotalBytes": 0,
    "quantization": { "rotationMaxErrorDeg": 0, "scaleMaxRelError": 0, "scaleMeanRelError": 0 }
  }
}
```

---

## 4. オフライン変換（Rust: build-lod-unity）

### 4.1 構成

```
tools/splat-pipeline/
├── Cargo.toml            workspace
├── VENDORED_COMMIT       取り込んだ Spark のコミット
├── spark-lib/            Spark（無改造、MIT）
├── build-lod/            Spark の参照実装（無改造、既定ビルド対象外）
├── build-lod-unity/      CLI とエンコーダ
│   └── src/  main.rs, lib.rs, unity_encoder.rs (§4.4-4.5), coord.rs (§4.6), validate.rs (§4.8)
└── gsplat-convert-ffi/   同じパイプラインを C ABI で公開（実機変換用）
```

### 4.2 パイプライン

1. `MultiDecoder` で入力をデコード（.ply / PlayCanvas 圧縮 .ply / .spz / .splat / .ksplat / .sog / .zip）
2. `--max-sh` に従い SH 次数を制限（フォーマットは SH0 のみ）
3. LoD ツリー構築: 既定 `tiny_lod::compute_lod_tree`、`--quality` で `bhatt_lod::compute_lod_tree`
4. `chunk_tree::chunk_tree` で空間的に並べ替え
5. 座標系プリセットを適用（§4.6）
6. UnityEncoder で並び順を決めて書き出し（§4.4, §4.5）
7. 自己検証（§4.8）。失敗時は exit code 3

### 4.3 CLI

```
build-lod-unity <input> [options]

  --out <dir>            出力先（既定: 入力と同じディレクトリ）
  --name <base>          出力ベース名（既定: 入力ファイル名から拡張子を除いたもの）
  --quality              bhatt-lod を使う（既定: tiny-lod）
  --lod-base <f32>       LoD 基数（既定: tiny 1.5 / bhatt 1.75。Spark の build-lod と同じ）
  --max-sh <0..3>        既定 0（現フォーマットは 0 のみ受け付ける）
  --coord <preset>       identity | flip-x | flip-y | flip-z（既定: flip-x）
  --region-splats <n>    チャンク配置のリージョン粒度（既定 8192）
  --force                既存の出力を上書き

exit code: 0=成功 / 1=入力エラー / 2=内部エラー / 3=自己検証失敗
```

### 4.4 並び順（globalIndex の割当て）

UnityEncoder は chunk_tree の出力順をそのまま使わず、ツリーを自前で走査して globalIndex を振り直す。
並びは 2 段階で決める。

- **バックボーン**: 部分木が `regionSplats` を超えるノードを BFS 順に出力する。部分木が
  `regionSplats` 以下になったノード（リージョン根）は、その代表スプラットだけをこの時点で出力する。
  これにより、リージョン単位の粗い全景と §5.5 の代替先となる祖先が先頭のチャンクに集まる
- **リージョン**: 各リージョンの残り（葉と子孫）を、バックボーンで見つけた順に前順 DFS で連続して出力する。
  1 つのリージョンの精細化はそのリージョンのチャンクだけで完結する

どちらの段階でも次の不変条件を保つ。

- ノードの直接の葉は連続した globalIndex を持つ
- ノードの内部子は NodeTable 上で連続する（親の訪問時にまとめて確保）
- 親の NodeTable インデックスは子より小さい

### 4.5 書き出し

- `.usc` は 65,536 スプラットごとに分割してストリーム書き出しする
- 量子化（§3.2）はここで行い、回転の最大角度誤差とスケールの相対誤差を manifest に記録する
- `encode_lod_opacity()` の結果（LoD 不透明度 D）は scale に焼き込まず、レコードの lodOpacity に格納する
- ノードの radius は子の範囲と、各スプラットの描画範囲（代表は D に応じて拡大）を含むよう下から計算する

### 4.6 座標系プリセット

学習系（右手系）と Unity（左手系）の違いはオフラインで焼き込み、ランタイムでは基底変換しない。
反転は回転を共役にとる（反転軸に直交する 2 成分の符号を反転）。

| プリセット | position | quaternion |
|---|---|---|
| `identity` | そのまま | そのまま |
| `flip-x`（既定） | x 反転 | (y, z) 反転 |
| `flip-y` | y 反転 | (x, z) 反転 |
| `flip-z` | z 反転 | (x, y) 反転 |

一般的な 3DGS の PLY は `flip-x` で正しい向きになる。そうならないデータは、ストリーミングオブジェクト
（または XR パネルの Transform 項目、§8）で Transform を変えて向きを確認し、対応するプリセットで変換し直す。

### 4.7 SH

SH0 のみ。DC 係数から §3.2 の式で RGB を求める。

### 4.8 自己検証（validate.rs）

書き出し後に `.usst` / `.usc` を読み戻して検証する。

1. 全 NodeRecord の子ノード範囲が nodeCount 内
2. 全 directLeaf 範囲と mergedSplatIndex が totalSplats 内
3. ルートから到達できるノード数 = nodeCount（孤立ノードなし）
4. 到達できる葉の総数 = leafSplats、かつ nodeCount + 葉数 = totalSplats
5. 各チャンクのファイルサイズ = byteLength、splatCount の合計 = totalSplats
6. 親の radius が子ノードと直接の葉（描画範囲込み）を含む（許容誤差 1%）

### 4.9 テスト

- 合成 10 万スプラットの PLY での E2E（生成 → 検証通過）
- 量子化のラウンドトリップ、LoD 不透明度の lodOpacity 経由のラウンドトリップ
- ランダムツリーに対する §4.4 の不変条件
- FFI（`gsplat-convert-ffi`）の開始・進捗取得・キャンセル・失敗時の挙動

---

## 5. LoD スライス

### 5.1 定義

- **描画アイテム**: `Merged(node)`（内部ノードの代表スプラット 1 個）または `LeafRun(first, count)`（連続する葉）
- **フロンティア**: 描画アイテムの集合。ツリーのカット（ルートから各葉への経路上でちょうど 1 回描画される）
- **コスト**: Merged = 1、LeafRun = count
- **予算 N**: フロンティアのコスト合計の上限（`BudgetSplats`）

### 5.2 優先度（projectedError）

```
dist = max(|center - eye| - radius, nearClip) ^ k       （k = DistanceFalloffPower、既定 1）
pe   = featureSize / dist
視錐台: バウンディング球がいずれかの平面の完全に外側なら pe × OutOfFrustumFactor（カリングはしない）
視錐台内: 視線方向との角度で重み付け（FoveaInner 以内は 1、FoveaOuter で PeripheryFactor へ線形に低下。
          ノードの角半径ぶん内側に寄せて判定する）
```

VR では左右の目の視錐台の和（左目の左平面と右目の右平面）を使う。視点位置はセンターアイ。

### 5.3 展開

pe の大きい順に貪欲に展開する。

```
heap = max-heap of (pe, node)
push(root)
while heap not empty:
    node = pop-max
    expandCost = interiorChildCount + directLeafCount
    if expandCost == 0
       or pe <= PeThreshold                          # 画面上で十分小さい
       or frontierCost - 1 + expandCost > N          # 予算超過
       or 展開で必要になるチャンクが MaxChunks を超える:
        emit Merged(node); continue
    frontierCost += expandCost - 1
    emit LeafRun(directLeafFirst, directLeafCount)   # あれば
    push(interior children)
```

- `PeThreshold = LodTargetPixelSize / focalPx`。0 なら無効（予算のみで打ち切る）
- `MaxChunks` はプールに入りきらない場合の上限（プールのページ数 - 2）。シーン全体が常駐できる場合は制限なし。
  展開は必要チャンクをまとめて確保できたときだけ行う（部分確保は巻き戻す）
- ヒステリシスは実装していない
- Burst の `IJob` 1 本で実行する

### 5.4 出力

- `DrawItems`: フロンティア
- `RequiredChunks`: フロンティアが参照するチャンク（重複なし）
- `DesiredChunks`: フロンティアの各 Merged ノードをもう 1 段展開した場合に必要なチャンク（先読み用）

### 5.5 未ロードチャンクの代替

可視インデックス構築時（§7.6.2）に、参照先のチャンクが未ロードなら:

- `LeafRun` はチャンク単位で判定し、常駐している部分はそのまま出力する。欠けている部分だけ親の Merged で代替する
- `Merged` 自体が未ロードならさらに親へ遡る
- chunk 0 はロード直後に読み込んで常駐させる（追い出さない）ので、遡りは必ず止まる
- 同じ祖先に代替される複数のアイテムは 1 回だけ出力する

---

## 6. メモリとスレッド

### 6.1 メモリ

| 項目 | サイズ |
|---|---|
| スプラットプール（GraphicsBuffer） | 32B × 65,536 × ページ数（1 ページ 2MB） |
| チャンク → スロット表（GPU と CPU ミラー） | uint32 × chunkCount |
| プール内スプラット位置の CPU ミラー（ソート用） | float3 × プール容量 |
| 可視インデックス / ソート作業領域 / オーダーバッファ ×2 | uint32 × N 程度 × 数本 |
| ノードテーブル | 40B × 内部ノード数 |
| ステージング | 2MB × `MaxConcurrentLoads` |

- ページ数: `PoolPages = 0`（既定）なら、モバイル 128 / デスクトップ 256 ページを上限に、シーンのチャンク数と
  端末の上限で制限する。シーン全体が入る場合は全常駐になり、追い出しは発生しない
- 1 本の GraphicsBuffer は `SystemInfo.maxGraphicsBufferSize`（Quest 3 で 128MB = 64 ページ）までなので、
  プールは最大 4 本に分割する。端末上限 = 4 × 1 本あたりのページ数
- `PoolPages` を手動で指定する場合は `ceil(N / 65536) + 2` 以上

### 6.2 スレッド

- ファイル I/O: 専用スレッド 1 本
- `GraphicsBuffer.SetData` はメインスレッドのみ。チャンク転送とオーダーバッファ転送にそれぞれ
  1 フレームあたりの上限を設ける（`MaxUploadBytesPerFrame` / `MaxOrderUploadBytesPerFrame`）
- スライス、可視インデックス構築、ソートは Burst Job。完了したフレームで回収する（1〜数フレームの遅延を許容）

---

## 7. ランタイム（Unity / C#）

### 7.1 パッケージ構成

```
Packages/com.gsplat.lod/
├── Runtime/
│   ├── Format/     SplatFormat.cs, UsstReader.cs
│   ├── Core/       SplatSceneAsset, TreeSlicer, TreeSliceJob, ChunkStreamer, SplatPagePool,
│   │               LodStreamingDriver, LodStreamingConfig
│   ├── Rendering/  GsplatLodRenderer, VisibleIndexBuildJob, DepthSortJob
│   ├── Shaders/    GsplatLod.shader, GsplatLodRecord.hlsl
│   ├── Convert/    実機変換（SplatConverter, PlyHeader, SplatFilePicker）
│   ├── XrUI/       XR セットアップパネル
│   └── Debug/      LodDebugHud
├── Editor/         Setup Window、インスペクタ
└── Tests/Editor/   §9
```

### 7.2 SplatSceneAsset

- `.usst` 全体を読み込み、ノードテーブルとチャンク索引を `NativeArray` で保持する
- 読み込み時に magic / version / 各サイズを検証し、不一致なら例外
- `ChunkPath(chunkId)` は `.usst` と同じフォルダの `<name>_{id:D4}.usc`

### 7.3 TreeSlicer

- 次のすべてを満たすとスライスを開始する: 前回から `SliceMinIntervalFrames` 以上経過、
  視点が `SliceMoveThresholdMeters` 以上移動または `SliceRotateThresholdDegrees` 以上回転、前回のジョブが完了
- ジョブの内容は §5.3。結果は完了したフレームで `TryCollect` により回収する

### 7.4 ChunkStreamer

1. `Request(required, desired)` で要求を置き換える。前回の要求で未処理のものは破棄する
2. I/O スレッドが `FileStream` でチャンクをステージングに読む。開いたファイルハンドルは LRU で
   `FileHandleCacheSize` 本までキャッシュする
3. `Update()`（メインスレッド）が上限内でプールに転送する。空きページがなければ次フレームに回す
4. ファイル欠損・サイズ不一致は 1 回再試行し、それでも失敗すれば欠損扱いにして §5.5 の代替で描画を続ける

### 7.5 SplatPagePool

- 呼び出し側が「現在必要なチャンク集合」（描画中のオーダーバッファが参照するものと、次のスライスが
  参照するもの）を宣言する。集合外の常駐ページだけが追い出し候補になり、その中で LRU を追い出す。
  chunk 0 は常に必要集合に含める
- ページの追加はシェーダが参照するスロット表を更新する。GPU 側のアドレスは
  `poolIndex = slot(g >> 16) × 65536 + (g & 0xFFFF)`、バッファは `poolIndex / BufferSplatCapacity` で選ぶ

### 7.6 描画

#### 7.6.1 境界

`GsplatLodRenderer` はプールの GraphicsBuffer 群、スロット表、ソート済みオーダーバッファだけを受け取って
描画する。ツリー、スライス、ストリーミングには依存しない。

#### 7.6.2 VisibleIndexBuildJob

描画アイテム列を globalIndex の列に展開する Burst Job。LeafRun は連番に展開し、Merged は 1 要素。
未ロードの代替（§5.5）はこのジョブ内で行う。

#### 7.6.3 ソートと描画

1. ソート: 可視インデックスごとに `dot(position - eye, forward)` をキーにし、Burst の LSD 基数ソート
   （8 bit × 4 パス）で奥から手前に並べる（CPU）。視点が `SortMoveThresholdMeters` /
   `SortRotateThresholdDegrees` 以上動いたとき、フロンティアが変わらなくても再ソートする
2. 転送: 結果をオーダーバッファ（ダブルバッファ）に分割転送し、転送が終わったフレームで切り替える
3. 描画: 1 インスタンスに複数の四角形を持つメッシュをインスタンシングで描画し、頂点シェーダが
   オーダーバッファ経由でスプラットを読む。Single Pass Instanced に対応。ソートはセンターアイ基準で 1 回
4. シェーダは宣言する StructuredBuffer の本数をプールのバッファ本数に合わせたバリアント
   （`GSPLAT_POOLS_1/2/4`）を使う。OpenGL ES では頂点ステージの SSBO 本数に上限があり
   （Adreno で 4）、宣言しただけで数えられるため
5. `KernelAlphaCutoff` 未満の寄与しかない範囲は四角形から削り、画面上の半径が `MinPixelRadius` 未満の
   スプラットは描画しない

### 7.7 フレームループ（LodStreamingDriver）

```
Update():
  1. 条件を満たせばスライスを開始（§7.3）
  2. スライスが完了していれば: 必要チャンク集合を更新 → ChunkStreamer に要求 → 可視インデックス構築 + ソートを開始
     それ以外で、チャンクのロードが進んでいれば: 前回のスライス結果から可視インデックスを作り直す
     それ以外で、視点が動いていれば: ソートだけやり直す
  3. ジョブが走っていなければ ChunkStreamer.Update()（チャンク転送）
  4. 構築/ソートが完了していればオーダーバッファへの分割転送を開始し、終わったら切り替える
描画: カメラごとに beginCameraRendering で GsplatLodRenderer に描画を依頼する
```

初期化: `.usst` 読み込み → プール確保 → chunk 0 を同期ロード → ルートだけのフロンティアで初回描画。

### 7.8 LodStreamingConfig

| key | 既定 | 説明 |
|---|---|---|
| BudgetSplats | 1,000,000 | 予算 N |
| LodTargetPixelSize | 1.5 | 精細化を止める画面上のサイズ（px）。0 で無効 |
| DistanceFalloffPower | 1.0 | §5.2 の k |
| OutOfFrustumFactor | 0.1 | §5.2 |
| FoveaInnerDegrees / FoveaOuterDegrees / PeripheryFactor | 25 / 70 / 0.35 | §5.2 |
| PoolPages | 0（自動） | §6.1 |
| MaxUploadBytesPerFrame | 4 MB | チャンク転送の上限 |
| MaxOrderUploadBytesPerFrame | 1 MB | オーダーバッファ転送の上限 |
| MaxConcurrentLoads | 4 | ステージングの本数 |
| SliceMoveThresholdMeters / SliceRotateThresholdDegrees / SliceMinIntervalFrames | 0.1 / 5 / 3 | §7.3 |
| PrefetchEnabled | true | DesiredChunks を読むか |
| ResidencyRebuildIntervalFrames | 10 | ロード中に可視インデックスを作り直す最短間隔 |
| FileHandleCacheSize | 8 | §7.4 |
| KernelAlphaCutoff | 1/255 | §7.6.3 |
| MinPixelRadius | 1.0 | §7.6.3 |
| SortMoveThresholdMeters / SortRotateThresholdDegrees | 0.05 / 2 | §7.6.3 |
| FoveatedRenderingLevel / EyeResolutionScale | 1 / 1 | 起動時に XR 表示設定へ適用 |
| MeasureSliceSync / StatsLogIntervalSeconds | false / 0 | 計測用 |

### 7.9 デバッグ表示（LodDebugHud）

fps / フレーム時間、フロンティアコスト、可視スプラット数、LoD 目標 px、常駐ページ数 / プールのページ数、
必要チャンク数、ロードキュー長とロード量、スライス時間（`MeasureSliceSync` 時）、1 フレームの転送量、
祖先で代替した描画アイテム数、欠損チャンク数。

---

## 8. 表示構成

表示は `LodStreamingDriver` によるストリーミング構成のみである。プールのページ数がチャンク数以上の場合は
自動的に全チャンク常駐（FullResidency）となり、追い出しを行わない。それ以外の場合は固定プールでのストリー
ミングとなる。プールのページ数は `Config.PoolPages` = 0 のときモバイルで 128、デスクトップで 256 であり、
いずれの場合もチャンク数と端末のバッファ上限でさらに制限される。

---

## 9. テスト（Unity EditMode）

- `UsstReaderTests`: 合成 `.usst` のラウンドトリップ。magic 不一致・バージョン不一致・chunkSplats 不一致・
  切り詰められたファイルで例外
- `TreeSliceTests`: 合成ツリーに対し、フロンティアがカットを成しコスト合計 ≤ N、チャンク上限の遵守、
  しきい値による一様なカット。可視インデックス構築の出力が常駐チャンクか祖先だけを参照し、
  一部だけ常駐している LeafRun は常駐部分を残すこと
- `SplatPagePoolTests`: 必要集合を除いた LRU 追い出し、複数バッファへの分割、スロット表の追従
- `PlyHeaderTests`: PLY ヘッダの解析と実機変換のメモリ見積もり
