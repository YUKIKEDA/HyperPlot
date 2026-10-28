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
- **作業中:** R2（設計: 線分のパイプラインとシェーダの生成器）
- **ブランチ:** `docs/42-r2-line-pipeline`
- **Issue:** [#42](https://github.com/YUKIKEDA/HyperPlot/issues/42)

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
| D1 | 3D に耐える v1 の契約と、v1.x の 3D の計画 | done | [#3](https://github.com/YUKIKEDA/HyperPlot/issues/3) |
| **A: データとヘッドレス描画の土台** | | | |
| C1 | 列とチャンク（`Column<T>`、3 種のストレージ、暗黙の列、チャンクの統計） | done | [#1](https://github.com/YUKIKEDA/HyperPlot/issues/1) |
| C2 | LOD ピラミッドと区間の min / max、M4 の抽出（CPU） | done | [#9](https://github.com/YUKIKEDA/HyperPlot/issues/9)（設計）、[#18](https://github.com/YUKIKEDA/HyperPlot/issues/18) |
| B1 | ベンチマークの基盤と参照機の固定（統計・LOD・M4 のカーネル、統計と水準 1 の走査の統合、暫定の表示が替わるまでの時間、メモリマップのページキャッシュに載る前の 1 フレーム） | done | [#26](https://github.com/YUKIKEDA/HyperPlot/issues/26)（設計）、[#30](https://github.com/YUKIKEDA/HyperPlot/issues/30) |
| R1 | D3D11 のデバイス、ヘッドレスの描画先、readback、PNG（WARP のテスト、NativeAOT のスクラッチの発行を 1 回） | done | [#34](https://github.com/YUKIKEDA/HyperPlot/issues/34)（設計）、[#38](https://github.com/YUKIKEDA/HyperPlot/issues/38) |
| R2 | シェーダの生成器と線分のパイプライン（アンチエイリアス、丸い結合、破線、喪失の後のパイプラインの作り直し） | in-progress | [#42](https://github.com/YUKIKEDA/HyperPlot/issues/42)（設計） |
| S1 | 最初の貫通: 1e9 点の時系列をヘッドレスで描くサンプル（NativeAOT の検査、系列ごとの M4 のフレーム内の並列、End-to-end の計測の基盤、GPU 時間の計測、ゴールデン画像の比較（`HyperPlot.Testing`）） | not-started | |
| **B: GoG** | | | |
| G1 | 仕様モデルとビルダ（Plot / Layer / Encoding / コンパイル） | not-started | |
| D2 | スケールの空間の精度（日時のティックを原点からの差で表す）の設計 | not-started | [#11](https://github.com/YUKIKEDA/HyperPlot/issues/11) |
| G2 | スケール（linear / log / time / band / 色）と目盛り、名前つきの色とパレット | not-started | |
| T1 | テキスト（契約、DirectWrite、アトラス、グリフのパイプライン） | not-started | |
| G3 | ガイドと配置（軸・凡例・タイトル・テーマ） | not-started | |
| G4 | Geom 一式（point / bar / area / rect / rule / segment / errorbar / band / text / image）、線のマイターと面取りの結合・平らと四角の端・3 つ以上の区間の破線 | not-started | |
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
| R3 | 密度タイルと生の点の切り替え、常駐キャッシュ（`ResidencyCache`）と喪失の後の作り直し | not-started | |
| C4 | ストリーミングとリングバッファ | not-started | |
| **E: XylemUI と公開** | | | |
| X1 | XylemUI のホスト | not-started | |
| Q1 | 性能の受け入れの計測（要件 §5） | not-started | |
| V1 | v1 の公開（NuGet、利用者向けの文書、サンプル） | not-started | |
| **v1 の後: 3D（v1.x）** | | | |
| K0 | 3D の設計（八分木・線の単純化・面の LOD・半透明の方式と、性能の仮説の確定） | not-started | |
| K1 | 3D の座標系・カメラ・軸の箱・3D のインタラクション、線分と点のパイプラインの深度と 3 成分の位置 | not-started | |
| K2 | 3D 散布（点群、八分木の LOD） | not-started | |
| K3 | 3D の線（軌跡） | not-started | |
| K4 | 面（z = f(x, y)、格子の LOD） | not-started | |
| K5 | メッシュと半透明の合成 | not-started | |
| K6 | ボリューム（レイマーチ、断面、等値面） | not-started | |
| **v1 の後: ホストとバックエンド** | | | |
| A1 | Avalonia のホスト | not-started | |
| P1 | wgpu-native のバックエンド（Linux） | not-started | |
| P2 | Metal のバックエンド（macOS） | not-started | |

## 意図的に対象外

要件定義書 §7 のとおり。
