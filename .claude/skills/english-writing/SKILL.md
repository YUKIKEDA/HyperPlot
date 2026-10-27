---
name: english-writing
description: >-
  Write HyperPlot XML docs, inline comments, test names and Preconditions/Steps/Expected remarks,
  exception messages, rules, and the README in English with the repository's terminology.
  Use when writing or editing any of these, when translating a term from the Japanese requirements
  or design, or when a review flags wording.
---

# Writing English in HyperPlot

Read this before writing. When a review corrects wording, add the general rule here in the same change; do not leave it in chat. The language policy is `.cursor/rules/language.mdc`.

## Sentence shape

- Plain, direct English. Short sentences and common words.
- XML doc elements are full sentences and end with a period (SA1629).
- Summaries start with a verb in the third person: "Returns ...", "Creates ...", "Maps ...".
  - Properties: "Gets ..." or "Gets or sets ..." (SA1623).
  - Constructors: "Initializes a new instance of the <see cref="T"/> class." (SA1642).
  - Boolean members: "Gets a value indicating whether ...".
- Write `<see langword="true"/>`, `<see langword="false"/>`, `<see langword="null"/>` for keywords.
- Refer to types and members with `<see cref="..."/>` and parameters with `<paramref name="..."/>`.
- Inline comments explain why, not what the next line does. A measured reason names the measurement.
- Keep the subject the same across a method name, its summary, and its remarks.
- Name the unit of every coordinate and size: data units, plot-area pixels, physical pixels, or device-independent pixels.

## Test names and remarks

- `{Target}_{Scenario}_{Expected}` in PascalCase.
- Remarks use the headings Preconditions, Steps, Expected (`.cursor/rules/testing.mdc`). Name the actual input (the point count, the chunk length, the viewport) and the actual check.

## Exception messages

- One sentence that names the condition that failed, with the same terms as the design and the XML docs.

## Terminology

The left column is the term in `docs/requirements.ja.md` and `docs/design.ja.md`. Write the right column in code, docs, and tests. When the design adds a term, add its row here in the same PR.

| Design term | Write |
| --- | --- |
| 列、列指向 | column, columnar |
| チャンク | chunk |
| メモリマップ | memory-mapped (file, view) |
| LOD ピラミッド、水準 | LOD pyramid, level |
| バケット（LOD の 1 水準の決まった数の要素） | bucket |
| 画素列 | pixel column |
| チャンクの統計 | chunk statistics (`ChunkStats<T>`) |
| M4 集約 | M4 aggregation |
| 密度集約 | density aggregation |
| 空間タイル索引、タイル | spatial tile index, tile |
| ストリーミング、追記 | streaming, append |
| 版（列と派生の結果の世代） | version |
| GoG、仕様モデル | Grammar of Graphics, spec (the spec model) |
| Geom、Stat、Position | geom, stat, position |
| レイヤー（GoG の重ね合わせ） | layer |
| エンコーディング（チャネル） | encoding (channel) |
| スケール、軸、目盛り | scale, axis, tick |
| ファセット | facet |
| 描画 IR | render IR (`RenderList`) |
| GPU 抽象 | GPU abstraction |
| 層、層の合成（背景・データ・インタラクション・オーバーレイ） | render layer, compositing |
| 大規模描画経路 | large-data path |
| ビューポート、プロット領域 | viewport, plot area |
| ホスト、ホストアダプタ | host, host adapter |
| 共有テクスチャ | shared texture |
| 縮退動作 | degradation |
| 不変条件 | invariant |
| 定常状態 | steady state |
| ホットパス、フレームの経路 | frame path |
| データの経路（ワーカーの処理） | data path (worker pass) |

Words to avoid:

| Avoid | Write |
| --- | --- |
| "the chart" when one series, one layer, or the plot area is meant | "series", "layer", or "plot area" |
| "data" when one column or one chunk is meant | "column" or "chunk" |
| "pixels" without saying which | "plot-area pixels", "physical pixels", or "device-independent pixels" |
| "fails" for throwing | "throws <see cref="InvalidOperationException"/>" (the concrete type) |
| "fast", "real-time", or "zero-cost" without a number | the measurement, or nothing |

## After a review

1. Before applying a suggested replacement, look for similar sentences elsewhere (`.cursor/rules/similar-findings.mdc`).
2. If the finding is not covered above, add it here as a general rule, not as one sentence.
