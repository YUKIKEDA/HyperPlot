# 開発規約

コードレビューで同じ指摘を繰り返さないための規約をまとめる。エージェントが守る強制力のある規約は `.cursor/rules/` にあり、本書はその人間向けの要約と、ディレクトリ構成の正本である。要件は [requirements.ja.md](./requirements.ja.md)、設計の中身は [design.ja.md](./design.ja.md) にある。

## ディレクトリ構成

プロジェクトやフォルダは、それを必要とする Issue の PR で作る。先回りして空のものを作らない。プロジェクトを追加した PR で、下の表に1行足し、`HyperPlot.slnx` の最上位フォルダ名のソリューションフォルダ（`/src/`・`/tests/` など）に入れる。

```text
HyperPlot.slnx               ソリューション（.sln は使わない）
global.json                  .NET SDK の固定（10.0 系）とテストランナー（Microsoft.Testing.Platform）
Directory.Build.props        全プロジェクト共通の設定（警告はエラー、解析、AOT 互換、パッケージ情報）
Directory.Packages.props     パッケージのバージョン（Central Package Management）
BannedSymbols.txt            エンジン側で禁止する API（実時間・スリープ・リフレクション等）
.editorconfig                命名・スタイル・解析ルールの重大度
.csharpierrc.json            フォーマッタ（CSharpier）の設定
.config/dotnet-tools.json    ローカルツール（CSharpier）
build.ps1                    ローカルのゲート（CI も同じスクリプトを実行する）
src/                         ライブラリ（HyperPlot.*、NuGet パッケージになる）
tests/                       テスト（src/ の各プロジェクトに対応する *.Tests）
samples/                     サンプル（NativeAOT の検査に使う、WPF を使わないサンプルを含む）
benchmarks/                  計測（カーネル・データの経路・End-to-end）
tools/                       リポジトリの生成器など
docs/                        要件・設計・判断・ロードマップ・規約（本書）
.cursor/rules/               エージェント向けの規約
.agents/skills/              スキル（.claude/skills/ に同じ内容を複製して置く）
.github/                     Issue / PR テンプレート、CI
```

`src/`・`tests/`・`samples/`・`benchmarks/`・`tools/` は、最初のプロジェクトを入れる PR で作る。

依存の向きは `HyperPlot.Core` ← `HyperPlot.Rendering.*` ← ホスト（`HyperPlot.Wpf`、のちに `HyperPlot.XylemUI`・`HyperPlot.Avalonia`）で、Core は Rendering とホストのプロジェクトを参照しない（設計書 §2.2）。UI のスタックは、それ自身のホストアダプタのプロジェクトだけが参照する。

| プロジェクト | 責務 |
| --- | --- |
| `src/HyperPlot.Core` | エンジン本体。今はデータ層の列（`Column`・`Column<T>`・`UniformColumn`。整数の等間隔の列を含む）、スケールの空間の原点と差（`ScaleOrigin`、internal の `ScaleSpace`・`ScaleRange`、設計書 §3.6）、3 種のストレージ（マネージド・ネイティブ・メモリマップ）、チャンクの統計（`ChunkStats<T>`、SIMD のカーネル）、min / max のピラミッド（`MinMaxPyramid<T>`・`MinMax<T>`）と M4 の抽出（`M4`）（`Data/`、設計書 §3.1〜§3.3・§5.5.1。ストレージとピラミッドのブロックに unsafe 可）と、ワーカーのプール（`PlotWorkerPool`、`Workers/`、設計書 §8.1）と系列ごとの M4 の並列（`M4Batch`）。表（`DataTable`・`ColumnRef<T>`、`Data/`、設計書 §3.1）、仕様のモデルとビルダ（`PlotSpec`・`LineLayerSpec` と行 G4 の Geom の仕様（`StepLayerSpec` から `ErrorBarLayerSpec`、利用者の Geom の口 `IGeom`・`CustomLayerSpec`・`GeomRanges`・`GeomFrame`）・位置のスケール（`LinearScaleSpec`・`LogScaleSpec`・`SymlogScaleSpec`・`PowScaleSpec`・`TimeScaleSpec`・`TransformScaleSpec` と変換の口 `IScale`、組み込みの変換は internal）・`Plot`、テーマ `Theme` と目盛りの書式の口 `TickFormatter`・`TimeTickFormatter`、`Spec/`、設計書 §4.1・§4.2・§4.7）、仕様のコンパイル（internal の `PlotPlan`: 検査・学習・差分・索引の待ち・配置・M4 と記録。レイヤーの種類ごとの internal の `LayerPlan`（`Compile/GeomPlans.cs`）と、パネルの cartesian の変換 `CartesianTransform`、`Compile/`、設計書 §4.2・§4.4・§4.8・§5.5.3）、ガイド（internal の目盛りの選び方 `ExtendedTicks`・`LogTicks`・`TimeTicks`、ラベルの書式と文字列の表、配置と記録の `PlotGuides`、`Guides/`、設計書 §4.6）。色の型（`Color`）と、GPU の契約・描画の命令（`RenderList`、折れ線・文字列・塗りつぶしの矩形・画素列の台形 `ColumnTrapezoid`）・テキスト（`TextStyle` などの型、契約 `ITextBackend`、整形のキャッシュとグリフのアトラスを持つ `TextEngine`、internal の `GlyphAtlas`）・ヘッドレスの描画・PNG の書き出し（`Rendering/`、`HyperPlot.Rendering`、設計書 §5.2・§5.6・§7.5） |
| `src/HyperPlot.Rendering.D3D11` | D3D11 のバックエンド（`D3D11Backend`、設計書 §5.3）: 共有のデバイス、ハードウェアから WARP、オフスクリーンの描画先と readback、デバイスの喪失と作り直し、折れ線の 3 回の描画（`D3D11LinePipeline`）、グリフの描画とアトラスの頁のテクスチャ（`D3D11GlyphPipeline`）、塗りつぶしの矩形（`D3D11RectPipeline`）、DirectWrite の整形とラスタライズ（`DWriteTextBackend`、リポジトリで最初の COM のコールバック、設計書 §5.6）。シェーダは `Shaders/*.hlsl` と、`tools/ShaderGen` の生成物 `Shaders/Shaders.g.cs`（設計書 §5.7）。TerraFX で COM の vtable を呼ぶ（unsafe 可） |
| `src/HyperPlot.Testing` | テストと利用者のテストの補助（設計書 §10.1）。今は画像の比較（`GoldenImage`・`ImageTolerance`・`ImageDifference`・`ImageMismatchException`）と、Core の書き手と別に書いた internal の PNG の復号器。Core だけを参照し、テストの枠組みに依らない。`BannedSymbols.txt` の対象外 |
| `tests/HyperPlot.Core.Tests` | Core の単体テスト。統計はスカラーのループと、ピラミッドと M4 はすべての要素を回るループ（`Data/Reference.cs`）と比べ、ゼロアロケーションを確かめる。ワーカーの状態は、ワーカーのスレッドでの読み出しを止める・失敗させるストレージ（`Data/TestStorage.cs`）で作る。仕様のコンパイル（`Compile/PlotPlanTests.cs`）は、記録した線を、同じ境界と写し方で手で組んだ `M4Batch` と `RenderList` と比べ、差分の段を internal の数え口で数える。位置のスケール（`Compile/PlotPlanScaleTests.cs`）は、変換・境界・写像を手で組んだ記録と比べる。行 G4 の Geom（`Compile/GeomPlanTests.cs`・`Compile/GeomCustomTests.cs`、共有の `Compile/GeomTesting.cs`）は、domain を [0, 100] にした 100 × 100 の描画先で、値がそのまま画素になる記録を手で組んだ `RenderList` と比べ、大きなデータの道を間引かない計算と比べる。スケールの空間の差と付け直し（`Data/ScaleSpaceTests.cs`）は、正確な分数（`Data/Rational.cs`）と比べる。テキスト（`Rendering/TextEngineTests.cs`）は、偽のテキストのバックエンド（`Rendering/FakeTextBackend.cs`、D3D11 のテストと共有）で、配置の式・整形のキャッシュ・アトラスの棚を確かめる |
| `tests/HyperPlot.Rendering.D3D11.Tests` | D3D11 のバックエンドのテスト。WARP で、Windows でだけ回す。喪失は internal の差し込み口（HRESULT・理由・デバイスの作成）で起こす。PNG は `HyperPlot.Testing` の internal の復号器で読み戻す。ゴールデン画像（`GoldenImageTests.cs`、基準は `Golden/*.png`）は `GoldenImage` で比べ、`HelloPlot` の場面は `samples/Shared` のコードで描く。折れ線は、描き手と別に書いた CPU の基準（`LineReference.cs`）と各チャネル 1 以内で比べる。行 R4 の丸でない結合と端と 3 区間以上の破線（`LineJoinTests.cs`）は、SVG 2 の破線の切り方で形を作り、画素の中の同じ 32 点で数える CPU の基準（`JoinReference.cs`）と比べる。仕様から描いた線のプロット（`PlotRenderTests.cs`）は、手で組んだ `M4Batch` と `RenderList` の画素とちょうど一致し、ゴールデン画像（`Golden/plot-lines.png`）と合う。テキスト（`TextTests.cs`）は、偽のテキストのバックエンドで描き、描き手と別に書いた CPU の基準（`TextReference.cs`）と各チャネル 1 以内で比べる。DirectWrite（`DirectWriteTests.cs`）は、画素がフォントと版に依るので、性質で確かめる |
| `tests/HyperPlot.Testing.Tests` | `HyperPlot.Testing` のテスト（両 OS、GPU なし）。比較の境目と、基準のファイル・更新の環境変数・復号器が拒む形を、手で作った画像と PNG（`Images.cs`）で試す。環境変数を書くテストは並列にしない |
| `benchmarks/HyperPlot.Benchmarks` | カーネルの計測（BenchmarkDotNet、設計書 §9）。チャンクの統計・水準 1・統計と水準 1 をまとめた試作（`FusedChunkPass`）、区間の min / max、M4、`RenderList` への記録、`M4Batch` と順の抽出の比較。internal のカーネルを呼ぶ。ゲートではない |
| `tools/ShaderGen` | `Shaders/*.hlsl` を Windows SDK の `fxc` でシェーダモデル 5.0 にコンパイルし、`Shaders.g.cs` に書き出す（設計書 §5.7）。シェーダを変えたら手で走らせる。ゲートは走らせず、Core のテストが HLSL の SHA-256 を比べる |
| `samples/HelloPlot` | メモリの 1e6 点の 4 系列を、ピラミッド・`M4Batch`・`RenderList` でヘッドレスに描き、DirectWrite の見出しを付けて PNG に書くサンプル（設計書 §2.1・§7.5）。NativeAOT の検査に使う（`aot.mdc`、`--auto`） |
| `samples/SpecPlot` | `PlotSpec` で 2 系列（1e6 点、ns の time の x）を、軸・タイトル・凡例つきでライトとダークの 2 枚の PNG に書き、同じ系列から作った band・bar・step・errorbar を 3 枚目の PNG に書くサンプル（設計書 §4.6・§5.5.3・§7.5、行 G3・G4）。`HelloPlot` と並べて NativeAOT の検査に使う（`aot.mdc`、`--auto`、`--scale`） |
| `samples/BillionSeries` | 1e9 点のメモリマップのファイル（なければシードから作る）の系列を、全体と拡大の表示で PNG に書くサンプル |
| `samples/Shared` | サンプルと計測とテストが共有するもの: データから画素への写し方（倍精度、`PlotArea.cs`）、1e9 点の系列のファイル（シードからのランダムウォーク、`WalkFile.cs`）、`HelloPlot` の場面（`HelloScene.cs`、D3D11 のテストがゴールデン画像に 1e5 点で描く）。ライブラリの API ではない（スケールは行 G2） |
| `benchmarks/HyperPlot.DataPath` | データの経路の計測（NativeAOT、GPU なし、設計書 §9）と、シードから系列のファイルをバッファなしで書く生成器。場面は構築・暫定が替わるまで・冷えた 1 フレーム。ゲートではない |
| `benchmarks/HyperPlot.E2E` | End-to-end のフレームの計測（NativeAOT、設計書 §9）: 1e9 点の系列（`samples/Shared/WalkFile.cs`）を、ズームとパンの台本で、抽出・写しと記録・描画の命令のフレームにし、段ごとの CPU 時間・GPU 時間（`LastFrameTime`）・フレームごとの確保・メモリを書く。要件 P-1 を測る。ゲートではない |

予定しているプロジェクトの全体は設計書 §2.1 にある。

## ビルドとゲート

- PR を出す前に `./build.ps1` を通す。ツールの復元、CSharpier の検査、復元、Release と Debug のビルド、全テストプロジェクトの実行を行う。CI も同じスクリプトを Windows と Linux で回す（Windows がマージの条件）
- 警告は最初からすべてエラー（`TreatWarningsAsErrors`）。ビルドを通すために `NoWarn` や `#pragma warning disable` を足さない。ルールがこのリポジトリに合わない場合は、理由をコメントに書いて `.editorconfig` を変え、PR でそう明記する
- `src/` のライブラリは `IsAotCompatible`。trim / AOT の警告もエラーになる。例外は `HyperPlot.Wpf` だけで、WPF 自体が NativeAOT で発行できないため、自身のプロジェクトファイルで `IsAotCompatible` を `false` にする。それでも自前のリフレクションは使わない。NativeAOT の検査は、WPF を使わずに Core と `HyperPlot.Rendering.D3D11` を使うサンプルで行う
- エンジン側（`src/` のうち `HyperPlot.Wpf` と `HyperPlot.Testing` 以外）では `BannedSymbols.txt` の API がビルドエラーになる。時刻は `TimeProvider` から取る（設計書 §8.3）
- パッケージを足す前に `.cursor/rules/dependencies.mdc` の4項目（AOT・配布ファイルを増やさない・ホットパス・ライセンス）を確かめ、設計書 §0 の表に1行足す

## C# の書き方

- フォーマットは CSharpier だけに任せる。`.csproj`・`.props`・`.slnx` も整形される（改行は LF）
- 現代的な C# を使ってよい（型が明らかなときの `var`、コレクション式、プライマリコンストラクタ、`ref struct` など）
- private フィールドは static も含めて `_camelCase`、定数は PascalCase、`this.` は付けない、インターフェースは `I` で始める
- 名前空間は設計書 §2.1 に従う。利用者が使う型は `HyperPlot` 1 つ（`HyperPlot.Core` もこれ）、バックエンドが実装する契約は `HyperPlot.Rendering`、ホストはアセンブリ名（`HyperPlot.Wpf` など）。フォルダには合わせない
- 不正な状態は型で表せないようにする。`bool` と、その `bool` 次第で意味を失う nullable の組み合わせは使わない
- 引数・状態の検査は `ThrowHelper` 経由で例外を投げ、内部の不変条件の破れは `Invariant.Fail` にする
- 公開 API には英語の XML ドキュメントを書く。internal は必須にしない
- フレームの経路では、アロケーション・LINQ・キャプチャするラムダ・`params`・ボクシング・要素ごとのインターフェース呼び出しを使わない。1 フレームの GPU へのアップロード量はデータの大きさではなくビューポートの画素数で抑え、データ全体をなめる O(N) の処理はワーカーで版を付けて行う（詳細は `.cursor/rules/perf.mdc`）

## テスト

- xUnit v3 と標準の `Assert` を使う
- 時刻とフレームは偽の `TimeProvider` とテスト用のフレームスケジューラから与える。テストは実時間で待たない
- メソッド名は `{対象}_{状況}_{期待}`。各テストに `summary` と `remarks`（Preconditions / Steps / Expected）を英語で書く
- GPU を使うテストは WARP で描き、Windows でだけ走る。`HyperPlot.Wpf` のテストも Windows だけ
- テストの層は設計書 §10 にある

## 言語

- コード・コメント・XML ドキュメント・シェーダ・テスト・コミットの件名・PR タイトル・ルール・スキルは英語
- 要件と設計書と `docs/` の文書、`README.ja.md`、Issue と PR の本文、コミットの本文は日本語でよい
- 詳細は `.cursor/rules/language.mdc`
