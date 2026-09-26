# Mobile XR 3DGS

**3D Gaussian Splatting を Android の XR 端末（Quest 3 / XREAL）で描画する Unity ランタイムと変換パイプライン。LoD とチャンク読み込みで GB 級のシーンを扱えます。**

[English](#english)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Unity 6000.3](https://img.shields.io/badge/Unity-6000.3-black)

---

## 概要

オフラインで LoD ツリー付きの独自フォーマット（`.usst` / `.usc`）へ変換し、実行時は視点から必要なノードだけを固定サイズの GPU プールへ読み込みます。

- **GPU メモリに上限** — 固定サイズのページプールを使い回す
- **未ロード領域は祖先ノードで代替** — 粗い全景を先に出し、視点周辺から精細化する
- **描画スプラット数に上限** — 1 フレームの描画数を予算 N で打ち切る
- **スタンドアロン動作** — Quest 3（URP / Vulkan / Single Pass Instanced）、XREAL（OpenGL ES3）

## 構成

| ディレクトリ | 内容 |
|---|---|
| `Packages/com.gsplat.lod/` | Unity ランタイム（UPM パッケージ）。ツリースライス、ページプール、ストリーミング、レンダラ統合 |
| `tools/splat-pipeline/` | Rust 製の変換 CLI。`.ply` 等 → `.usst` / `.usc` |
| `Assets/` | デモ用の Unity プロジェクト（`Assets/Scenes/DemoScene.unity`） |
| `docs/` | 運用手順、フォーマット選定の根拠 |
| `docs/spec.md` | フォーマットとランタイムの実装仕様 |

## 動作環境

- **Unity 6000.3.11f1**（URP 17.3.0）
- **Rust stable**（変換ツールのビルドに必要）
- 検証済み: Meta Quest 3（Android / IL2CPP / ARM64 / Vulkan）、XREAL（Android / OpenGL ES3、`feature/xreal` ブランチ）、Windows Standalone

## セットアップ

### 1. クローンして Unity で開く

```sh
git clone https://github.com/oshimu/mobile-xr-3dgs.git
cd mobile-xr-3dgs
```

Unity Hub から Unity 6000.3.11f1 で開きます。パッケージは Package Manager が自動解決します。

> **表示するデータについて**: 3DGS のシーンデータ（`StreamingData/`）はサイズが大きいためリポジトリに含めていません。手持ちの `.ply` を下記手順で変換してください。

### 2. データ変換

```sh
cd tools/splat-pipeline
cargo build --release
./target/release/build-lod-unity <scene>.ply --out ../../StreamingData/<name> --name <name>
```

- 対応入力: `.ply` / `.compressed.ply` / `.spz` / `.splat` / `.ksplat` / `.sog`
- 主なオプション
  - `--coord identity|flip-x|flip-y|flip-z` — 座標系をデータに焼き込む。既定は `flip-x`（右手系の一般的な 3DGS / PLY を Unity の左手系に合わせる）
  - `--lod-base <f32>` — 既定 1.5（`--quality` 指定時は 1.75）。大きくするとツリーが浅くなりスライスは速いが LoD は粗くなる
- 出力: `<name>.usst`（ツリー + 索引）、`<name>_XXXX.usc`（2MB チャンク群）、`<name>.manifest.json`（統計）

Unity メニュー **Tools > Gsplat LoD > Setup Window** の Convert セクションからも同じ処理を実行できます。

### 3. シーンに配置

**Tools > Gsplat LoD > Setup Window** で `.usst` を選び、目的に応じたオブジェクトを生成します。

| ボタン | 用途 |
|---|---|
| Create Streaming Object | 固定プール + ストリーミング（実運用構成） |

通常の 3DGS データは既定の `flip-x` で正しい向きになります。左右や上下が反転して見える場合は、ストリーミングオブジェクト（または XR パネルの Transform 項目）で正しい向きを探し、対応するプリセットで再変換してください。再変換したあとは Transform を identity のまま使います。

### 4. Quest 3 実機

`.usst` / `.usc` を `Application.persistentDataPath` 配下に置き、`LodStreamingDriver.UsstPath` に相対パス（例: `my-scene/my-scene.usst`）を設定します。詳細な手順は [docs/operations.md](docs/operations.md) を参照してください。

PC で変換する代わりに、実機上のアプリ内（`LodSetupPanel`）で `.ply` 等を直接変換することもできます。変換用のネイティブプラグインはビルド済みのものを同梱しています。手順は [docs/operations.md](docs/operations.md) の実機変換の節を参照してください。

### 5. XREAL（任意）

XREAL 向けのプロジェクト設定は Quest と両立しないため、`feature/xreal` ブランチにあります。XREAL XR Plugin は同梱していないので、XREAL の開発者サイトから `com.xreal.xr.tar.gz` を取得して `Packages/` に置いてください。

## アーキテクチャ

```
【オフライン: Rust CLI build-lod-unity】
  scene.ply/.spz → MultiDecoder → LoD ツリー構築 (tiny_lod/bhatt_lod)
                 → 空間的並べ替え (chunk_tree) → UnityEncoder
                 → scene.usst + scene_XXXX.usc

【ランタイム: com.gsplat.lod】
  TreeSlicer      視点から予算 N のフロンティアを切り出す (Burst Job)
  ChunkStreamer   I/O スレッドで固定ステージングへ読み込み
  SplatPagePool   固定 GraphicsBuffer プール、LRU + Pin
  VisibleIndexBuildJob  未ロードチャンクを祖先で埋めて可視列を構築
  GsplatLodRenderer     URP / Single Pass Instanced 対応の描画
```

より詳しくは以下を参照してください。

- 仕様: [docs/spec.md](docs/spec.md)
- パッケージ内 README: [Packages/com.gsplat.lod/README.md](Packages/com.gsplat.lod/README.md)

## 開発

```sh
# Rust 側のテスト
cd tools/splat-pipeline && cargo test

# Unity 側のテスト
# Window > General > Test Runner > EditMode
```

コントリビューションについては [CONTRIBUTING.md](CONTRIBUTING.md) を参照してください。

## ライセンス

MIT License — [LICENSE](LICENSE)

本リポジトリは第三者ソフトウェアを同梱しています。必要な著作権表記とライセンスは [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) にまとめてあります。

主な借用元:

| 領域 | 由来 |
|---|---|
| 入力フォーマットのデコード、LoD ツリー構築 | [Spark](https://github.com/sparkjsdev/spark) (MIT) — `tools/splat-pipeline/spark-lib/` に無改造で同梱 |
| スプラット投影数学（シェーダ） | [gsplat-unity](https://github.com/wuyize25/gsplat-unity) (MIT) → [PlayCanvas Engine](https://github.com/playcanvas/engine) (MIT) |
| XR Rig / UI アセット | Unity XR Interaction Toolkit Samples, TextMeshPro (Unity Companion License) |

---
---

<a name="english"></a>

# Mobile XR 3DGS (English)

**A Unity runtime and conversion pipeline for rendering 3D Gaussian Splatting on Android-based XR headsets (Quest 3 / XREAL). LoD and chunked loading make gigabyte-scale scenes usable.**

## Overview

Scenes are converted offline into a custom LoD-tree format (`.usst` / `.usc`). At runtime, only the nodes the current viewpoint needs are loaded into a fixed-size GPU pool.

- **Bounded GPU memory** — a fixed-size page pool is reused
- **Unloaded regions fall back to ancestor nodes** — a coarse overview appears first and refines around the viewer
- **Capped splat count** — draws per frame stop at budget N
- **Standalone** — Quest 3 (URP / Vulkan / Single Pass Instanced), XREAL (OpenGL ES3)

## Repository layout

| Directory | Contents |
|---|---|
| `Packages/com.gsplat.lod/` | Unity runtime (UPM package): tree slicing, page pool, streaming, renderer integration |
| `tools/splat-pipeline/` | Rust conversion CLI: `.ply` and friends → `.usst` / `.usc` |
| `Assets/` | Demo Unity project (`Assets/Scenes/DemoScene.unity`) |
| `docs/` | Operating procedures, format rationale (Japanese) |
| `docs/spec.md` | Format and runtime specification (Japanese) |

## Requirements

- **Unity 6000.3.11f1** (URP 17.3.0)
- **Rust stable** (to build the conversion tool)
- Verified on: Meta Quest 3 (Android / IL2CPP / ARM64 / Vulkan), XREAL (Android / OpenGL ES3, `feature/xreal` branch), Windows Standalone

## Getting started

### 1. Clone and open in Unity

```sh
git clone https://github.com/oshimu/mobile-xr-3dgs.git
cd mobile-xr-3dgs
```

Open the project with Unity 6000.3.11f1 via Unity Hub. Package Manager resolves dependencies automatically.

> **About scene data**: 3DGS scene data (`StreamingData/`) is not committed because of its size. Convert your own `.ply` with the steps below.

### 2. Convert data

```sh
cd tools/splat-pipeline
cargo build --release
./target/release/build-lod-unity <scene>.ply --out ../../StreamingData/<name> --name <name>
```

- Supported input: `.ply`, `.compressed.ply`, `.spz`, `.splat`, `.ksplat`, `.sog`
- Key options
  - `--coord identity|flip-x|flip-y|flip-z` — bake the coordinate system into the data. Default `flip-x` (maps typical right-handed 3DGS / PLY data to Unity's left-handed space)
  - `--lod-base <f32>` — default 1.5 (1.75 with `--quality`). Larger values give a shallower tree (faster slicing, coarser LoD)
- Output: `<name>.usst` (tree + index), `<name>_XXXX.usc` (2 MB chunks), `<name>.manifest.json` (statistics)

The same conversion is available from the Unity menu **Tools > Gsplat LoD > Setup Window**, Convert section.

### 3. Place it in a scene

In **Tools > Gsplat LoD > Setup Window**, pick a `.usst` and create the object you need.

| Button | Purpose |
|---|---|
| Create Streaming Object | Fixed pool + streaming (production configuration) |

Typical 3DGS data comes out correctly oriented with the default `flip-x`. If the scene looks mirrored or upside down, find the correct orientation with the Transform on the streaming object (or the Transform section of the XR panel), re-convert with the matching preset, and from then on keep the Transform at identity.

### 4. Quest 3 device

Place `.usst` / `.usc` under `Application.persistentDataPath` and set `LodStreamingDriver.UsstPath` to a relative path (e.g. `my-scene/my-scene.usst`). See [docs/operations.md](docs/operations.md) for the full procedure (Japanese).

Instead of converting on PC, you can convert a `.ply` (etc.) directly on the device via `LodSetupPanel`. The native conversion plugins ship prebuilt. See the on-device conversion section of [docs/operations.md](docs/operations.md) (Japanese).

### 5. XREAL (optional)

The XREAL project settings conflict with the Quest setup, so they live on the `feature/xreal` branch. The XREAL XR Plugin is not bundled: download `com.xreal.xr.tar.gz` from the XREAL developer site and place it in `Packages/`.

## Architecture

```
[Offline: Rust CLI build-lod-unity]
  scene.ply/.spz → MultiDecoder → LoD tree build (tiny_lod / bhatt_lod)
                 → spatial reordering (chunk_tree) → UnityEncoder
                 → scene.usst + scene_XXXX.usc

[Runtime: com.gsplat.lod]
  TreeSlicer            Cuts a frontier of budget N from the viewpoint (Burst job)
  ChunkStreamer         Reads chunks into fixed staging on an I/O thread
  SplatPagePool         Fixed GraphicsBuffer pool, LRU + pin
  VisibleIndexBuildJob  Builds the visible list, filling unloaded chunks with ancestors
  GsplatLodRenderer     Rendering for URP / Single Pass Instanced
```

## Development

```sh
# Rust tests
cd tools/splat-pipeline && cargo test

# Unity tests
# Window > General > Test Runner > EditMode
```

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT License — [LICENSE](LICENSE)

This repository bundles third-party software. Required copyright and license notices are collected in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

| Area | Origin |
|---|---|
| Input format decoding, LoD tree construction | [Spark](https://github.com/sparkjsdev/spark) (MIT) — vendored unmodified under `tools/splat-pipeline/spark-lib/` |
| Splat projection math (shader) | [gsplat-unity](https://github.com/wuyize25/gsplat-unity) (MIT) → [PlayCanvas Engine](https://github.com/playcanvas/engine) (MIT) |
| XR rig / UI assets | Unity XR Interaction Toolkit Samples, TextMeshPro (Unity Companion License) |
