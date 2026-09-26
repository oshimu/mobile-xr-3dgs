# Contributing

日本語・英語どちらの Issue / Pull Request でも構いません。
Either Japanese or English is fine for issues and pull requests.

## 開発環境 / Development environment

- Unity **6000.3.11f1**（URP 17.3.0）
- Rust **stable**（`rust-version = 1.82` 以上）

リポジトリを clone して Unity Hub から開けば、パッケージは Package Manager が解決します。
3DGS のシーンデータはリポジトリに含まれていないため、動作確認には手持ちの `.ply` の変換が必要です。

## テスト / Running tests

```sh
# Rust
cd tools/splat-pipeline
cargo test
cargo fmt -p build-lod-unity -p gsplat-convert-ffi -- --check
cargo clippy -p build-lod-unity -p gsplat-convert-ffi --no-deps --all-targets -- -D warnings
```

Unity 側は **Window > General > Test Runner > EditMode** から実行します。

> `tools/splat-pipeline/build-lod/` と `tools/splat-pipeline/spark-lib/` は
> [Spark](https://github.com/sparkjsdev/spark) からの vendored コードです。
> **変更しないでください。** 上流を取り込み直す場合は
> `tools/splat-pipeline/VENDORED_COMMIT` も併せて更新してください。
> `build-lod` は既定の `gpu` feature が wgpu を引くため `default-members` から外してあり、
> 素の `cargo build` ではビルドされません。

## コーディング規約 / Style

- **C#** — 周囲のコードのスタイルに合わせてください。パフォーマンスクリティカルな箇所は
  Burst / Jobs 前提です。変更後は Console に `Burst is disabled ...` が出ていないことを確認してください。
- **Rust** — `cargo fmt` / `cargo clippy` を通してください（自作の `build-lod-unity` / `gsplat-convert-ffi` のみ対象）。
  `gsplat-convert-ffi` を変更したら `tools/build-native.ps1` で同梱バイナリも更新してください。
- **コメント** — 「何をしているか」ではなく「なぜそうしたか」を書いてください。

## Pull Request

1. `main` からブランチを切る
2. 上記のテストがローカルで通ることを確認する
3. PR の説明に「何を・なぜ」と、検証方法（実機で確認したなら機種と fps）を書く

シーンやプレハブ（`.unity` / `.prefab`）を変更する PR は差分レビューが難しいため、
変更内容を文章で説明してください。

## ライセンス / License

Pull Request を送った時点で、その貢献が本プロジェクトの
[MIT License](LICENSE) の下で配布されることに同意したものとみなします。

By submitting a pull request, you agree that your contribution is licensed
under this project's [MIT License](LICENSE).
