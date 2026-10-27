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
benchmarks/                  計測（BenchmarkDotNet と End-to-end）
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
| `src/HyperPlot.Core` | エンジン本体。今はデータ層の列（`Column`・`Column<T>`・`UniformColumn`）、3 種のストレージ（マネージド・ネイティブ・メモリマップ）、チャンクの統計（`ChunkStats<T>`、SIMD のカーネル）（`Data/`、設計書 §3.1・§3.2。ストレージに unsafe 可） |
| `tests/HyperPlot.Core.Tests` | Core の単体テスト。統計はスカラーのループと比べ、ゼロアロケーションを確かめる |

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
