# ロードマップ

進捗の正本。何を作るかは [requirements.ja.md](./requirements.ja.md) と [design.ja.md](./design.ja.md)。却下した案は [design-decisions.md](./design-decisions.md)。

GitHub Issue は着手中の作業単位だけ切る。完了条件の詳細は Issue 本文に書き、この表は状態と Issue 番号だけを持つ。

状態: `not-started` / `in-progress` / `blocked` / `done`

## 運用

1. 新しい作業は、先にこの表へ行を足してから Issue を切る（行の追加は Grill を経る）
2. 次の Issue は、前の行が `done` または `blocked` になってから切る
3. Spike の完了は「仮説を判定できたこと」。全部 Yes である必要はない
4. アーキテクチャの前提が折れたら、行を `blocked` にし、設計を直してから次へ進む
5. 結果は Issue の本文に残す。この表は状態と Issue のリンクだけ
6. 再現の手順（`dotnet test` / `dotnet run` / `dotnet publish -c Release` のコマンド）を Issue に必ず残す

## 今

- **フェーズ:** A（データとヘッドレス描画の土台）
- **作業中:** C2（C1 は #1 の PR で済み）
- **ブランチ:** —
- **Issue:** —

## ツールチェーン

- .NET SDK 10.0.x（LTS）。`global.json` で固定
- C# 14。共通の設定は `Directory.Build.props`（警告はエラー、`src/` は `IsAotCompatible`。`HyperPlot.Wpf` を除く）
- Windows の NativeAOT には MSVC のリンカが要る（Visual Studio の「C++ によるデスクトップ開発」）
- シェーダの生成（`tools/ShaderGen`、行 R2 から）には Windows SDK の `fxc.exe` が要る

## 全体

| ID | 内容 | 状態 | Issue |
| --- | --- | --- | --- |
| F0 | 開発基盤（規約・スキル・ビルド設定・CI・テンプレート） | done | — |
| D0 | 要件定義と設計 v1（requirements・design・decisions） | done | — |
| **A: データとヘッドレス描画の土台** | | | |
| C1 | 列とチャンク（`Column<T>`、3 種のストレージ、暗黙の列、チャンクの統計） | done | [#1](https://github.com/YUKIKEDA/HyperPlot/issues/1) |
| C2 | LOD ピラミッドと区間の min / max、M4 の抽出（CPU） | not-started | |
| B1 | ベンチマークの基盤と参照機の固定（統計・LOD・M4 のカーネル） | not-started | |
| R1 | D3D11 のデバイス、ヘッドレスの描画先、readback、PNG（WARP のテスト） | not-started | |
| R2 | シェーダの生成器と線分のパイプライン（アンチエイリアス） | not-started | |
| S1 | 最初の貫通: 1e9 点の時系列をヘッドレスで描くサンプル（NativeAOT の検査） | not-started | |
| **B: GoG** | | | |
| G1 | 仕様モデルとビルダ（Plot / Layer / Encoding / コンパイル） | not-started | |
| G2 | スケール（linear / log / time / band / 色）と目盛り | not-started | |
| T1 | テキスト（契約、DirectWrite、アトラス、グリフのパイプライン） | not-started | |
| G3 | ガイドと配置（軸・凡例・タイトル・テーマ） | not-started | |
| G4 | Geom 一式（point / bar / area / rect / rule / segment / errorbar / band / text / image） | not-started | |
| G5 | Stat と Position | not-started | |
| G6 | ファセットと複数のプロット | not-started | |
| **C: インタラクションと WPF** | | | |
| I1 | 入力のモデル、ツールの状態機械、ビューの操作、ズームの履歴 | not-started | |
| W1 | WPF のホスト（D3DImage、ソフトウェア描画の経路、DPI、入力） | not-started | |
| I2 | ヒットテスト、ホバー、クロスヘア、ツールチップ | not-started | |
| I3 | 選択とリンク | not-started | |
| I4 | 注釈と計測 | not-started | |
| **D: 散布の大規模化とストリーミング** | | | |
| C3 | 空間タイル索引（Morton 順の並べ直し、四分木の表） | not-started | |
| R3 | 密度タイルと生の点の切り替え | not-started | |
| C4 | ストリーミングとリングバッファ | not-started | |
| **E: XylemUI と公開** | | | |
| X1 | XylemUI のホスト | not-started | |
| Q1 | 性能の受け入れの計測（要件 §5） | not-started | |
| V1 | v1 の公開（NuGet、利用者向けの文書、サンプル） | not-started | |
| **v1 の後** | | | |
| A1 | Avalonia のホスト | not-started | |
| P1 | wgpu-native のバックエンド（Linux） | not-started | |
| P2 | Metal のバックエンド（macOS） | not-started | |

## 意図的に対象外

要件定義書 §7 のとおり。
