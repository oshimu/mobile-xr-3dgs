# Third-Party Notices

本プロジェクト（Mobile XR 3DGS）は、以下の第三者ソフトウェアを利用・同梱しています。
各ソフトウェアの著作権は各権利者に帰属します。

---

## 1. 同梱しているソースコード（vendored）

### 1.1 Spark — `tools/splat-pipeline/spark-lib/`

3D Gaussian Splatting の各種フォーマットデコーダ、および LoD ツリー構築アルゴリズム
（`tiny_lod` / `bhatt_lod` / `chunk_tree` 等）を提供します。

- 提供元: https://github.com/sparkjsdev/spark
- 取り込み元コミット: `750812dcc15f3a7444765bf43af4942133fa3bcc`
  （`tools/splat-pipeline/VENDORED_COMMIT` に記録）
- 改変: **なし**（無改造で同梱）
- ライセンス: MIT（全文は `tools/splat-pipeline/spark-lib/LICENSE`、および下記）

```
The MIT License

Copyright © 2025 WORLD LABS TECHNOLOGIES, INC.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

> 備考: `tools/splat-pipeline/build-lod/` も Spark 由来の参照実装です（既定ビルドからは
> 除外。`Cargo.toml` の `default-members` を参照）。同じ MIT ライセンスが適用されます。

---

## 2. 派生・参考にしたコード

以下はソースコードとしては同梱していないが、本プロジェクトのシェーダ
（GsplatLod.shader / GsplatLodRecord.hlsl）に由来する数式を含むため、
ライセンスに従い著作権表示を掲載する。

### 2.1 gsplat-unity

Unity 向け Gaussian Splatting レンダラ。
`Packages/com.gsplat.lod/Runtime/Shaders/GsplatLod.shader` の
スプラット投影数学（EWA splatting、共分散の画面空間投影）は本実装を参考にしています。

- 提供元: https://github.com/wuyize25/gsplat-unity
- ライセンス: MIT（全文は下記）

```
MIT License

Copyright (c) 2025 Yize Wu

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### 2.2 PlayCanvas Engine（間接的な由来）

上記 gsplat-unity のスプラット描画は PlayCanvas Engine の gsplat chunks に由来します。
そのため本プロジェクトのシェーダにも同エンジン由来の数式が含まれます。

- 提供元: https://github.com/playcanvas/engine
- ライセンス: MIT

```
MIT License

Copyright (c) 2011-2024 PlayCanvas Ltd.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 3. Rust クレート依存（同梱ネイティブプラグインと `build-lod-unity` に静的リンクされるもの）

本リポジトリはビルド済みのネイティブプラグイン
（`Packages/com.gsplat.lod/Runtime/Plugins/` の `gsplat_convert.dll` / `libgsplat_convert.so`）を
同梱しており、以下のクレートが静的リンクされています。
**各ライセンスの全文は [`licenses/rust-crates.txt`](licenses/rust-crates.txt) にまとめてあります**
（`cargo-about` で生成。手順は下記「完全な表記の自動生成」）。

以下は依存の一覧です（`cargo metadata` の `license` フィールドより機械的に取得）。

複数ライセンスの選択肢（`OR`）がある場合、本プロジェクトは可能な限り **MIT** を選択します。

| クレート | ライセンス |
|---|---|
| adler2 | 0BSD OR MIT OR Apache-2.0 |
| ahash (0.7, 0.8) | MIT OR Apache-2.0 |
| anyhow | MIT OR Apache-2.0 |
| bitflags | MIT OR Apache-2.0 |
| bumpalo | MIT OR Apache-2.0 |
| bytemuck | Zlib OR Apache-2.0 OR MIT |
| byteorder-lite | Unlicense OR MIT |
| cfg-if | MIT OR Apache-2.0 |
| const-random, const-random-macro | MIT OR Apache-2.0 |
| crc32fast | MIT OR Apache-2.0 |
| crunchy | MIT |
| doc-comment | MIT |
| either | MIT OR Apache-2.0 |
| equivalent | Apache-2.0 OR MIT |
| fdeflate | MIT OR Apache-2.0 |
| flate2 | MIT OR Apache-2.0 |
| getrandom | MIT OR Apache-2.0 |
| glam | MIT OR Apache-2.0 |
| half | MIT OR Apache-2.0 |
| hashbrown (0.11, 0.17) | Apache-2.0 OR MIT |
| hnsw | MIT |
| image | MIT OR Apache-2.0 |
| image-webp | MIT OR Apache-2.0 |
| indexmap | Apache-2.0 OR MIT |
| itertools | MIT OR Apache-2.0 |
| itoa | MIT OR Apache-2.0 |
| libm | MIT |
| log | MIT OR Apache-2.0 |
| memchr | Unlicense OR MIT |
| miniz_oxide | MIT OR Zlib OR Apache-2.0 |
| **moxcms** | **BSD-3-Clause OR Apache-2.0**（MIT の選択肢なし） |
| num-traits | MIT OR Apache-2.0 |
| once_cell | MIT OR Apache-2.0 |
| ordered-float | MIT |
| png | MIT OR Apache-2.0 |
| proc-macro2 | MIT OR Apache-2.0 |
| **pxfm** | **BSD-3-Clause OR Apache-2.0**（MIT の選択肢なし） |
| quick-error | MIT OR Apache-2.0 |
| quote | MIT OR Apache-2.0 |
| rand_core | MIT OR Apache-2.0 |
| rand_pcg | MIT OR Apache-2.0 |
| serde, serde_core, serde_derive | MIT OR Apache-2.0 |
| serde_json | MIT OR Apache-2.0 |
| simd-adler32 | MIT |
| smallvec | MIT OR Apache-2.0 |
| space | MIT |
| syn | MIT OR Apache-2.0 |
| **tiny-keccak** | **CC0-1.0**（パブリックドメイン相当） |
| typed-path | MIT OR Apache-2.0 |
| **unicode-ident** | **(MIT OR Apache-2.0) AND Unicode-3.0** |
| zerocopy, zerocopy-derive | BSD-2-Clause OR Apache-2.0 OR MIT |
| zip | MIT |
| **zlib-rs** | **Zlib** |
| zmij | MIT |
| **zopfli** | **Apache-2.0**（唯一の選択肢） |

### 注意が必要なクレート

- **zopfli (Apache-2.0)** — MIT の選択肢がありません。Apache-2.0 の義務
  （ライセンス全文の提供、NOTICE ファイルがある場合はその伝達、変更点の告知）が発生します。
- **moxcms / pxfm (BSD-3-Clause OR Apache-2.0)** — BSD-3-Clause を選ぶ場合、
  「著作権者名を推奨・宣伝に無断使用しない」条項が加わります。
- **unicode-ident** — Unicode-3.0 ライセンスが AND 結合されており、必ず適用されます。
- 上記はいずれも `image` クレート（PNG / WebP デコード）経由の間接依存です。

Apache-2.0 / BSD / Zlib / Unicode-3.0 の全文は、それぞれの配布元、または
https://spdx.org/licenses/ を参照してください。

### 完全な表記の自動生成

依存を更新したら、以下で `licenses/rust-crates.txt` を再生成してください
（設定は `tools/splat-pipeline/about.toml`、テンプレートは `about.hbs`）。

```
cargo install cargo-about --locked --features cli
cd tools/splat-pipeline
cargo about generate --manifest-path gsplat-convert-ffi/Cargo.toml -c about.toml -o ../../licenses/rust-crates.txt about.hbs
```

---

## 4. Unity パッケージ本体

`Packages/manifest.json` で参照している Unity 公式パッケージ本体
（URP、XR Interaction Toolkit、OpenXR、AR Foundation 等）は、
Unity Package Manager 経由で各利用者の環境に解決されるため、
本リポジトリには同梱していません（`Library/PackageCache/` は `.gitignore` 済み）。
これらには Unity Companion License または Unity 独自の EULA が適用されます。

ビルド成果物（APK 等）を配布する場合は、Unity のライセンス条項に従ってください。

---

## 5. 同梱している Unity 提供アセット（Unity Companion License）

デモシーンを clone 直後に再生できるようにするため、以下の Unity 提供アセットを
`Assets/` 配下に**同梱しています**。いずれも Unity Companion License (UCL) が適用されます。

- ライセンス全文: https://unity.com/legal/licenses/unity-companion-license

| 対象 | パス | 由来パッケージ / バージョン |
|---|---|---|
| XR Interaction Toolkit Starter Assets | `Assets/Samples/XR Interaction Toolkit/3.3.0/Starter Assets/` | `com.unity.xr.interaction.toolkit` 3.3.0 の Sample |
| XR Interaction Simulator | `Assets/Samples/XR Interaction Toolkit/3.3.1/XR Interaction Simulator/` | `com.unity.xr.interaction.toolkit` 3.3.1 の Sample |
| TextMeshPro Essential Resources | `Assets/TextMesh Pro/` | `com.unity.ugui` (TextMeshPro) の Essential Resources |

`Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF` に含まれる
Liberation Sans フォントには SIL Open Font License 1.1 が適用されます
（https://openfontlicense.org/ ）。


### 5.1 Noto Sans JP — `Assets/Fonts/`

UI の日本語表示のため、`NotoSansJP-Regular.ttf` と、そこから生成した TextMeshPro の
Dynamic フォントアセット（`NotoSansJP-Regular SDF Dynamic.asset`）を同梱しています。

- 提供元: https://github.com/google/fonts/tree/main/ofl/notosansjp
- ライセンス: SIL Open Font License 1.1
- 全文: [`licenses/NotoSansJP-OFL.txt`](licenses/NotoSansJP-OFL.txt)

---

## 6. リポジトリに含めていないアセット・データ

以下は `.gitignore` によりリポジトリから除外されています。
**ビルド成果物（APK 等）として配布する場合は、別途ライセンス表記が必要です。**

| 対象 | パス | ライセンス / 備考 |
|---|---|---|
| XREAL XR Plugin | `Packages/com.xreal.xr.tar.gz` | 再配布不可。XREAL の開発者サイトから各自取得 |
