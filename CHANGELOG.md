# Changelog

このプロジェクトの主な変更点を記録します。
形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に、
バージョニングは [Semantic Versioning](https://semver.org/lang/ja/) に従います。

## [Unreleased]

## [0.1.0] - 2026-09-24

初回公開。

### Added

- **`.usst` / `.usc` ストリーミングフォーマット** — LoD ツリー + ノードテーブル + 2MB 固定チャンク。
  仕様は `docs/spec.md`
- **変換 CLI `build-lod-unity`**（Rust） — `.ply` / `.compressed.ply` / `.spz` / `.splat` / `.ksplat` / `.sog`
  からの変換、ハイブリッド coarse-first レイアウト、量子化、自己検証。
  座標系プリセット（`identity` / `flip-x` / `flip-y` / `flip-z`、既定 `flip-x`）の焼き込みに対応
- **実機での変換** — ネイティブプラグイン `gsplat_convert`（Android arm64 / Windows x86_64、ビルド済みを同梱）で
  ヘッドセット上で `.ply` 等を `.usst` / `.usc` に変換。入力は Android のファイルピッカー（SAF）から選択
- **Unity ランタイム `com.gsplat.lod`**
  - `TreeSlicer` / `TreeSliceJob` — 視点から予算 N のフロンティアを切り出す Burst ジョブ（画面上 px しきい値付き）
  - `ChunkStreamer` — I/O スレッドでの固定ステージングへの非同期読み込み
  - `SplatPagePool` — 固定 GraphicsBuffer プール、LRU + Pin、最大 4 本のバッファ分割
  - `VisibleIndexBuildJob` — 未ロードチャンクの祖先フォールバック込みの可視列構築
  - `GsplatLodRenderer` — URP / Single Pass Instanced 対応の描画
  - `LodStreamingDriver` — フレームループ統合
  - `LodDebugHud` — fps、frontier cost、プール使用率、ロードキュー長、スライス時間の表示
- **XR セットアップパネル**（`LodSetupPanel`） — ヘッドセット内でのファイル選択、ロード、
  Config のライブ調整、Transform プリセットの保存
- **Editor 拡張** — **Tools > Gsplat LoD > Setup Window** から変換と
  ストリーミングオブジェクトの生成
- **Meta Quest 3 スタンドアロン対応** — Android / IL2CPP / ARM64 / Vulkan
- **XREAL 対応（ランタイム側）** — OpenGL ES3 の SSBO 本数上限に合わせたシェーダバリアント、
  両眼の視錐台の和によるカリング、タッチパッド移動。プロジェクト設定は `feature/xreal` ブランチ
- EditMode テスト（`SplatPagePoolTests` / `TreeSliceTests` / `UsstReaderTests` / `PlyHeaderTests`）と
  Rust の e2e / FFI テスト

[Unreleased]: https://github.com/oshimu/mobile-xr-3dgs/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/oshimu/mobile-xr-3dgs/releases/tag/v0.1.0
