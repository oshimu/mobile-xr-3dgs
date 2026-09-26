# com.gsplat.lod

3D Gaussian Splatting シーンを LoD とチャンク読み込みで表示する Unity ランタイム。

- 仕様: `docs/spec.md`
- 変換〜実機の運用手順: `docs/operations.md`

## 使い方

### 1. データ変換（PC / Rust）

```
cd tools/splat-pipeline
cargo build --release
./target/release/build-lod-unity.exe scene.ply --out <出力先>
```

または Unity メニュー **Tools > Gsplat LoD > Setup Window** の Convert セクション。

出力: `scene.usst`（ツリー+索引）、`scene_XXXX.usc`（2MB チャンク群）、`scene.manifest.json`。

### 2. シーンセットアップ（Editor 拡張）

**Tools > Gsplat LoD > Setup Window** から `.usst` を選び、目的のオブジェクトを作成:

| ボタン | 用途 |
|---|---|
| Create Streaming Object | 固定プール + ストリーミング（実運用構成） |

シーンへの変更は GameObject 追加のみ（保存はユーザー操作）。

### 3. 座標系の確認

通常の 3DGS データは既定の `--coord flip-x` で正しい向きになる。反転して見える場合のみ:

1. ストリーミングオブジェクト（または XR パネルの Transform 項目）で正しい向きを探す
2. 対応するプリセット（`identity` / `flip-y` / `flip-z`）で再変換
3. 以降 Transform は identity 運用

### 4. Quest 3 実機

`.usst`/`.usc` を `Application.persistentDataPath` 配下に配置し、
LodStreamingDriver.UsstPath に相対パスを設定（例: `my-scene/my-scene.usst`）。

## 主要クラス

- `SplatSceneAsset` — .usst ロード（NodeTable 常駐）
- `TreeSlicer` / `TreeSliceJob` — 視点から予算 N のフロンティアを切り出す（Burst）
- `ChunkStreamer` — I/O スレッドでチャンクを固定ステージングへ読み込み
- `SplatPagePool` — 固定 GraphicsBuffer プール、LRU + Pin
- `VisibleIndexBuildJob` — 未ロードチャンクの親フォールバック込みで可視列を構築
- `GsplatLodRenderer` + `GsplatLod/Splat` — SplatRecord プール描画（URP/SPI 対応方式）
- `LodStreamingDriver` — フレームループ統合
- `LodDebugHud` — fps / プール使用率 / キュー長等の表示

## ライセンスと由来

本パッケージは MIT ライセンスです（リポジトリ直下の `LICENSE`）。
第三者ソフトウェアの表記は `THIRD_PARTY_NOTICES.md` にまとめてあります。

### 借用元

| 領域 | 由来 |
|---|---|
| 入力フォーマットのデコード、LoD ツリー構築（`tiny_lod` / `bhatt_lod` / `chunk_tree`） | [Spark](https://github.com/sparkjsdev/spark)（MIT）。`tools/splat-pipeline/spark-lib/` に無改造で同梱。取り込み元コミットは `tools/splat-pipeline/VENDORED_COMMIT` |
| スプラット投影数学（`GsplatLod.shader`） | [gsplat-unity](https://github.com/wuyize25/gsplat-unity)（MIT）→ [PlayCanvas Engine](https://github.com/playcanvas/engine)（MIT） |
