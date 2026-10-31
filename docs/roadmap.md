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

- **フェーズ:** B（GoG）
- **作業中:** G3b（G7c は #172 の PR で済み）
- **ブランチ:** `docs/176-g3b-labels`（設計）
- **Issue:** [#176](https://github.com/YUKIKEDA/HyperPlot/issues/176)（設計）

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
| R2 | シェーダの生成器と線分のパイプライン（アンチエイリアス、丸い結合、破線、喪失の後のパイプラインの作り直し、CPU（記録と書き込み）と GPU（参照機、1,920 列の M4 の線 1 本と 16 本）の計測） | done | [#42](https://github.com/YUKIKEDA/HyperPlot/issues/42)（設計）、[#46](https://github.com/YUKIKEDA/HyperPlot/issues/46) |
| S1 | 最初の貫通: 1e9 点の時系列をヘッドレスで描くサンプル（`HelloPlot`・`BillionSeries`、NativeAOT の検査と P-10 の確定、系列ごとの M4 の並列 `M4Batch`） | done | [#50](https://github.com/YUKIKEDA/HyperPlot/issues/50)（設計）、[#54](https://github.com/YUKIKEDA/HyperPlot/issues/54) |
| S2 | End-to-end の計測（`benchmarks/HyperPlot.E2E`）、公開の GPU 時間の計測、P-1 の計測（1e9 点の全体の表示の M4 の内訳を含む。S1 の `BillionSeries` で 2 系列 4.9〜6.9 ms）、系列が少ないときの画素列の範囲での分け方の要否 | done | [#58](https://github.com/YUKIKEDA/HyperPlot/issues/58)（設計）、[#62](https://github.com/YUKIKEDA/HyperPlot/issues/62) |
| S3 | ゴールデン画像の比較（`HyperPlot.Testing` の `GoldenImage`、`HelloPlot` の場面と線の種類の 2 場面、基準の更新の手順、許容差は手元と CI の WARP の差を測って決める） | done | [#66](https://github.com/YUKIKEDA/HyperPlot/issues/66)（設計）、[#70](https://github.com/YUKIKEDA/HyperPlot/issues/70) |
| **B: GoG** | | | |
| G1 | 仕様モデルとビルダ（`DataTable`、Plot / Layer / Encoding / コンパイル、`SpecId` と差分の再計算）、linear の位置のスケール、線のプロットをヘッドレスで描く（`HeadlessRenderer.Render(PlotSpec)`） | done | [#76](https://github.com/YUKIKEDA/HyperPlot/issues/76)（設計）、[#81](https://github.com/YUKIKEDA/HyperPlot/issues/81) |
| D2 | スケールの空間の精度（日時のティックを原点からの差で表す）の設計と実装（`ScaleOrigin`、M4 と `PlotWorkerPool.Extract` の原点、整数の等間隔の列 `Column.UniformInteger`、コンパイルの中の domain、R-6 のテスト、M4 の前後の計測） | done | [#11](https://github.com/YUKIKEDA/HyperPlot/issues/11)（設計）、[#85](https://github.com/YUKIKEDA/HyperPlot/issues/85) |
| G2 | 位置のスケール（linear の zero・reverse・clamp、log・symlog・sqrt・power、time の単位と起点（既定は .NET のティック、ns・µs の Unix 起点、日時の domain を `long` で正確に指定する口、§3.1・§4.2）、変換を足す口 `IScale`、log と linear の描き直しの計測） | done | [#94](https://github.com/YUKIKEDA/HyperPlot/issues/94)（設計）、[#95](https://github.com/YUKIKEDA/HyperPlot/issues/95) |
| T1 | テキスト（`RenderList.DrawText` と `HeadlessRenderer.MeasureText`、契約 `ITextBackend`、DirectWrite の整形とグレースケールのラスタライズ、グリフのアトラス、グリフのパイプライン、`HelloPlot` の見出し、ラベルの描き直しの計測） | done | [#103](https://github.com/YUKIKEDA/HyperPlot/issues/103)（設計）、[#104](https://github.com/YUKIKEDA/HyperPlot/issues/104) |
| G3 | ガイドと配置（x と y の軸・線のレイヤーの凡例・タイトル・テーマ・DPI の倍率）、目盛り（Wilkinson の拡張・log の 10 の冪・symlog・日時の暦の段、§4.6）と nice（要件 G-6）、塗りつぶしの矩形 `DrawRect`、`SpecPlot` のサンプル、ガイドの CPU 時間の計測 | done | [#111](https://github.com/YUKIKEDA/HyperPlot/issues/111)（設計）、[#115](https://github.com/YUKIKEDA/HyperPlot/issues/115) |
| G4 | 数の列の Geom（step・area・band・bar・rect・rule・segment・errorbar、§5.5.3）と大きなデータの道、Geom を足す口（`IGeom`・`CustomLayerSpec`）、列の台形の命令 `DrawColumns`、座標系の cartesian の 4×4 の変換（internal、§4.4）、Geom の CPU 時間の計測 | done | [#120](https://github.com/YUKIKEDA/HyperPlot/issues/120)（設計）、[#124](https://github.com/YUKIKEDA/HyperPlot/issues/124) |
| R4 | 線のマイターと面取りの結合、平らと四角の端、3 つ以上の区間の破線（§5.1・§5.3）、丸・マイター・面取りの線の GPU 時間と記録の CPU 時間と 8 区間の破線の GPU 時間の計測（丸の線が R2 から変わらないことを含む） | done | [#128](https://github.com/YUKIKEDA/HyperPlot/issues/128)（設計）、[#132](https://github.com/YUKIKEDA/HyperPlot/issues/132) |
| G10 | 座標系（要件 G-7）: 公開の `CoordSpec`、cartesian の flip（大きなデータの道を画素行で、`DrawColumns` の向き）、flip のあり・なしの 1 フレームの時間の計測（NativeAOT の Release、1,920 × 1,080、1e9 点の線（S2 の場面）と 1e7 点の area と bar） | done | [#136](https://github.com/YUKIKEDA/HyperPlot/issues/136)（設計）、[#140](https://github.com/YUKIKEDA/HyperPlot/issues/140) |
| G11 | polar（要件 G-7）: `PolarCoordSpec`、伸ばした空間で M4 と G4 の Geom を使い記録の段で写す道（線は弦とのずれ 0.25 画素以下の小片）、極座標の台形の命令 `DrawSectors`、角度と半径の軸と目盛り、円のグリッド（§4.4）。polar（theta = x）と cartesian の 1 フレームの時間の計測（NativeAOT の Release、1,920 × 1,080、交互に 3 回、1e9 点の線（S2 の場面）と 1e7 点の area と bar、小片の数と `DrawSectors` の GPU 時間） | done | [#145](https://github.com/YUKIKEDA/HyperPlot/issues/145)（設計）、[#149](https://github.com/YUKIKEDA/HyperPlot/issues/149) |
| G7a | 名前つきの色（`Colors`、CSS の 148 色）とパレット（`Palette`・`Palettes` の質的な 7 つ）、テーマが決める系列の既定の色（`Theme.Palette`、tab10）、仕様の色の null（`StrokeSpec`・`Color?` の `Fill`）、`GeomFrame.SeriesColor`（§4.1・§4.7）。時間の計測はしない（色はコンパイルで決まり、フレームの道は変わらない。確保 0 はテストで見る） | done | [#153](https://github.com/YUKIKEDA/HyperPlot/issues/153)（設計）、[#157](https://github.com/YUKIKEDA/HyperPlot/issues/157) |
| G7b | カテゴリの列 `CategoricalColumn`（`Column.Categorical`、`CategoryRef`、§3.1）、band のスケール（`Scale.Band`、§4.2）とその軸（カテゴリごとの目盛り、重なれば k 個おきのラベル、§4.6）、カテゴリの x の bar（カテゴリごとの最小・最大をワーカーが版ごとに作る、画素より多いカテゴリは画素列ごと、§5.5.3）。計測: NativeAOT の Release、1,920 × 1,080、交互に 3 回。ワーカーの最小・最大（1e8 行、カテゴリ 10 と 1e5）と 1 フレームの時間（カテゴリ 50 と 1e5） | done | [#159](https://github.com/YUKIKEDA/HyperPlot/issues/159)（設計）、[#163](https://github.com/YUKIKEDA/HyperPlot/issues/163) |
| G7c | 列で色を分けるチャネル（`ColorBy`・`FillBy`）、ordinal・sequential・diverging の色のスケールと 12 の連続の色の写像（§4.1・§4.2）、カテゴリ名の凡例と連続のカラーバー（§4.6）。カテゴリは層を系列に分け（区間の道と集める道、§5.5.3）、数の列は bar と rect の要素ごとの色。計測: NativeAOT の Release、1,920 × 1,080、交互に 3 回。分けるワーカーの仕事（1e8 行、8 カテゴリ、区間に並ぶ場合と混ざる場合）と 1 フレームの時間（1e8 行の線を 8 と 256 カテゴリで分けた場合、連続の色の rect 131,072 個） | done | [#168](https://github.com/YUKIKEDA/HyperPlot/issues/168)（設計）、[#172](https://github.com/YUKIKEDA/HyperPlot/issues/172) |
| G3b | 軸の端のラベルを描画先の内に収める配置（F を越えるラベルだけ内側へ寄せ、重なりは寄せた後の箱で測る。下と左の軸、flip と polar の半径の軸を含む、§4.6）。行 G7b の band の軸の長い名前で見つかった。時間の計測はしない（配置の段の数え方は変わらず、確保 0 はテストで見る） | in-progress | [#165](https://github.com/YUKIKEDA/HyperPlot/issues/165)、[#176](https://github.com/YUKIKEDA/HyperPlot/issues/176)（設計） |
| G8 | text の Geom（データの文字列のラベル、`CategoricalColumn` と文字列の列を使う、§5.5.3） | not-started | |
| G9 | image の Geom（ラスタのデータの型、タイルとミップマップ、`DrawImage`、§5.5.3） | not-started | |
| G5 | Stat と Position（カテゴリの x の errorbar・rule を、並べ（dodge）と一緒に。行 G7b から、§4.2。色で分けた bar と area の積み上げを既定にするか（行 G7c から、§4.1）） | not-started | |
| G6 | ファセットと複数のプロット、二次軸（要件 G-9）と上や右に置く軸（§4.6） | not-started | |
| **C: インタラクションと WPF** | | | |
| I1 | 入力のモデル、ツールの状態機械、ビューの操作（原点の付け直し、§6.2）、ズームの履歴、ズーム中に新しい目盛りのラベルで確保しないこと（span を受ける `DrawText` と確保しない整形、§4.6・不変条件 3） | not-started | |
| W1 | WPF のホスト（D3DImage、ソフトウェア描画の経路、DPI とその変化での描き直し、入力）、層のテクスチャでの合成（§5.4）、索引ができる前の暫定の表示とホストへの通知（§4.8、要件 P-2）、プロットの要約をアクセシビリティの API に渡すこと（要件 N-10、§4.6） | not-started | |
| I2 | ヒットテスト、ホバー、クロスヘア、ツールチップ、End-to-end のホバーの場面（P-5・P-6） | not-started | |
| I3 | 選択とリンク | not-started | |
| I4 | 注釈と計測、注記（要件 G-10、§4.6） | not-started | |
| **D: 散布の大規模化とストリーミング** | | | |
| C3 | 空間タイル索引（Morton 順の並べ直し、四分木の表） | not-started | |
| R3 | 密度タイルと生の点の切り替え、常駐キャッシュ（`ResidencyCache`）と喪失の後の作り直し、point の Geom（`DrawMarkers`、polar では頂点シェーダで写す、§4.4）、point のスケールとカテゴリの x の line・step（行 G7b から、§4.2）、GPU で写す `RenderList.SetTransform`（§5.1）と座標系の 4×4 の変換の公開の契約（不変条件 12、Issue #136）、segment と rect の大きなデータの道（131,072 を超える要素の密度、§5.5.3）、線分ごとの色（rule・segment・errorbar の要素ごとの連続の色、行 G7c から、§4.1） | not-started | |
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
