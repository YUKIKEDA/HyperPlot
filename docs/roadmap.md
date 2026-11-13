# ロードマップ

進捗の正本。何を作るかは [requirements.ja.md](./requirements.ja.md) と [design.ja.md](./design.ja.md)。却下した案は [design-decisions.md](./design-decisions.md)。

GitHub Issue は着手中の作業単位だけ切る。完了条件の詳細は Issue 本文に書き、この表は状態と Issue 番号だけを持つ。

状態: `not-started` / `in-progress` / `blocked` / `done`

## 運用

1. 新しい作業は、先にこの表へ行を足してから Issue を切る（行の追加は Grill を経る）
2. 次の Issue は、前の行が `done` または `blocked` になってから切る。ただし、文書だけの design の行（コードとファイルが重ならない行）は、コードの行と並べて進めてよい（Issue #470 の決定 1）
3. Spike の完了は「仮説を判定できたこと」。全部 Yes である必要はない
4. アーキテクチャの前提が折れたら、行を `blocked` にし、設計を直してから次へ進む
5. 結果は Issue の本文に残す。この表は状態と Issue のリンクだけ
6. 再現の手順（`dotnet test` / `dotnet run` / `dotnet publish -c Release` のコマンド）を Issue に必ず残す

## 今

- **フェーズ:** D（散布の大規模化とストリーミング）
- **作業中:** R3b1（設計。R3f は #454 と #456 の PR で済み）
- **ブランチ:** `docs/462-density`（設計）
- **Issue:** #462（設計）
- **並べた design の行:** K0a（運用 2）。ブランチ `docs/470-k0`（設計）、Issue #470（設計）、#477（試作）
- **並べた design の行:** K0e（運用 2）。ブランチ `docs/481-k0be`（設計）、Issue #484（設計）、#491（試作）

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
| G3b | 軸の端のラベルを描画先の内に収める配置（F を越えるラベルだけ内側へ寄せ、重なりは寄せた後の箱で測る。下と左の軸、flip と polar の半径の軸を含む、§4.6）。行 G7b の band の軸の長い名前で見つかった。時間の計測はしない（配置の段の数え方は変わらず、確保 0 はテストで見る） | done | [#165](https://github.com/YUKIKEDA/HyperPlot/issues/165)、[#176](https://github.com/YUKIKEDA/HyperPlot/issues/176)（設計）、[#180](https://github.com/YUKIKEDA/HyperPlot/issues/180) |
| G8 | text の Geom（データのラベル: `CategoricalColumn` の名前か、数の列を書式で書いた文字列。プロットの text の層の行の総和 4,096 まで、重なりは避けない、整形のキャッシュの上限を 8,192 に、§4.1・§5.5.3）と、4,096 個のラベルの 1 フレームの CPU と GPU の時間の計測（NativeAOT の Release、1,920 × 1,080） | done | [#184](https://github.com/YUKIKEDA/HyperPlot/issues/184)（設計）、[#188](https://github.com/YUKIKEDA/HyperPlot/issues/188) |
| G9 | image の Geom（ラスタの型 `Raster<T>` と RGBA、2 × 2 の平均のミップマップと 256 × 256 のタイル、`DrawImage`、常駐キャッシュ `ResidencyCache` と喪失の後の作り直し、1 フレーム 4 MB の転送と粗い段の仮の描画、§3.7・§5.2・§5.5.3）と、2 万 × 2 万の `float` のラスタ（メモリマップ）のミップマップを作る時間とパン・ズームの 1 フレームの時間と転送量の計測（NativeAOT の Release、1,920 × 1,080） | done | [#196](https://github.com/YUKIKEDA/HyperPlot/issues/196)（設計）、[#200](https://github.com/YUKIKEDA/HyperPlot/issues/200) |
| G5a | Position（要件 G-5）: `PositionSpec`（identity・stack・dodge・fill）と `LayerSpec.Position`、色で分けた bar と area の既定の積み上げ（行 G7c から）、カテゴリの x の積み上げ（セルごとの正と負の和をワーカーが作る）と数の x の積み上げ（系列を併合した累積の列、area は ggplot2 の `stat_align` の補間）、fill、dodge（全体で固定の場所）、カテゴリの x の errorbar・縦の rule（行 G7b から）、flip と polar（§4.1・§4.2・§5.5.3）。計測: NativeAOT の Release、1,920 × 1,080、交互に 3 回。ワーカー（セルの和 1e8 行、色 8 × x 50 と 8 × 1e5。累積の列 1e8 点・8 系列、x がそろう場合とずれる場合の時間とメモリ）と、積んだ bar・area、並べた bar と errorbar の 1 フレームの CPU と GPU の時間 | done | [#204](https://github.com/YUKIKEDA/HyperPlot/issues/204)（設計）、[#208](https://github.com/YUKIKEDA/HyperPlot/issues/208) |
| G5b | Stat の枠（`IStat`、ワーカーが作る版付きの派生の表、§4.3）と、bin（1D）・count・summary（mean・min・max・quantile）（要件 G-4）。bin は Vega の刻み・[a, b)・スケールの空間、summary の quantile は厳密な type 7 の radix select、Stat の結果はワーカーが作る派生の表で、層は Geom の道と Position で描く（§4.1・§4.3・§5.5.3）。計測: NativeAOT の Release、1,920 × 1,080、3 回。1e9 点の `float`（メモリマップ）のヒストグラム（1 群と 8 群）、count（1e9 行、カテゴリ 50 と 1e5）、summary（1e9 行・カテゴリ 50 の平均・最小・最大と quantile 2 つ）を作る時間と 1 フレームの時間（要件 P-2・P-1） | done | [#214](https://github.com/YUKIKEDA/HyperPlot/issues/214)（設計）、[#218](https://github.com/YUKIKEDA/HyperPlot/issues/218) |
| G5c | 残りの Stat: density（KDE、1D）・smooth（移動平均）・bin（2D）（要件 G-4）。density は R の `density()` の手順（格子への線形の配りとガウスの核、既定の帯域幅は厳密な `bw.nrd0`）、smooth は x の単位の窓の中の y の平均、bin 2D は軸ごとの `BinStatSpec` のセルを rect で数で塗る（§4.1・§5.5.3）。計測: NativeAOT の Release、1,920 × 1,080、3 回。1e9 点の `float`（メモリマップ）の density（1 群と 8 群、帯域幅の自動と数）、smooth（x は `UniformColumn`）、bin 2D（x と y の `float`、既定の区切りと 300 × 300）を作る時間と 1 フレームの時間（要件 P-2・P-1） | done | [#222](https://github.com/YUKIKEDA/HyperPlot/issues/222)（設計）、[#226](https://github.com/YUKIKEDA/HyperPlot/issues/226) |
| G6a | 軸の置き場所と二次軸（要件 G-9、§4.6）: スケールの `Side`（上と右の軸）、独立した二次のスケール（`PlotSpec.ScaleX2`・`ScaleY2` と層の `SecondaryX`・`SecondaryY`）と主の軸の変換の軸（`WithSecondaryAxis`）、グリッドは主だけ（§4.1・§4.6）。計測: NativeAOT の Release、1,920 × 1,080、3 回。1e9 点の系列 2 本を左右の y に載せ、x の上に変換の軸を付けた場面の 1 フレームの CPU と GPU の時間と確保を、軸が 1 本の場面と比べる（要件 P-1） | done | [#230](https://github.com/YUKIKEDA/HyperPlot/issues/230)（設計）、[#234](https://github.com/YUKIKEDA/HyperPlot/issues/234) |
| G6b | ファセット（要件 G-8、§4.5）: wrap と grid（R の `n2mfrow` の並べ方、行のないカテゴリを落とす）、軸の `Fixed`・`FreeX`・`FreeY`・`Free`（共有の軸は外側のパネルだけ）、文字だけの見出し、データは行 G7c の分けた層の道（§4.5・§4.6）。計測: NativeAOT の Release、1,920 × 1,080、3 回。1e9 点の系列を 8 カテゴリ（区間に並ぶ）の wrap と 1 パネルで、混ざる場合は 1e8 点で、作る時間と 1 フレームの CPU と GPU の時間と確保（要件 P-1） | done | [#238](https://github.com/YUKIKEDA/HyperPlot/issues/238)（設計）、[#243](https://github.com/YUKIKEDA/HyperPlot/issues/243)（設計の修正）、[#242](https://github.com/YUKIKEDA/HyperPlot/issues/242) |
| G6c | 複数のプロット（要件 G-9、§4.5）: `FigureSpec` の concat と grid（セルのまたぎ、同じ大きさのセル、セルごとに 1 枚の図と同じ配置、スケールと凡例はプロットごと、入れ子なし、図の中でピラミッドを共有）と `HeadlessRenderer.Render(FigureSpec)`（§4.5・§4.6）。計測: NativeAOT の Release、1,920 × 1,080、3 回。2 × 2 の図に同じ 1e9 点の系列を 4 つと 1 枚の図で、作る時間と 1 フレームの CPU と GPU の時間と確保（要件 P-1） | done | [#254](https://github.com/YUKIKEDA/HyperPlot/issues/254)（設計）、[#258](https://github.com/YUKIKEDA/HyperPlot/issues/258) |
| **C: インタラクションと WPF** | | | |
| I1c | ズーム中に新しい目盛りのラベルで確保しない（不変条件 3）: ラベルの文字を計画の置き場に書き、span を受ける `DrawText`、文字で引く整形のキャッシュの使い回し、`ITextBackend.Shape` を span を受けて `ShapedText` に書く形に（§4.6・§5.1・§5.6）。計測: x の domain を毎フレームずらして新しいラベルが出続ける 300 フレームの確保（偽の文字のバックエンドと DirectWrite、前の値は 1 フレーム 8.0 KB） | done | [#266](https://github.com/YUKIKEDA/HyperPlot/issues/266)（設計）、[#270](https://github.com/YUKIKEDA/HyperPlot/issues/270) |
| I1a | 対話の土台（要件 I-1・I-7・I-8・I-9）: `PlotEngine`（`SetPlot`・`SetFigure`・`FrameRequested`・`RenderFrame`、作ったスレッドだけ）、入力のモデル、ビュー（`PanelView2D`・`ViewRange`、仕様の domain との関係）、ビューの操作の API と制約と通知と履歴とアニメーション、原点の付け直し（§6.2）、場所の当たり判定（`HitArea`）、ツールの枠（`IPlotTool`）と割り当ての表（`InputBindings`）と組み込みのツール（パン・矩形ズーム・軸のドラッグ・ホイール・全体表示・キーボード・戻る・進む・Y の自動範囲）、入力の台本と記録と再生（`HyperPlot.Testing`、§6.6）。計測: 1e9 点に入力の台本を流した 1 フレームの CPU と GPU の時間の p95 と確保、入力の処理の時間（NativeAOT の Release、1,920 × 1,080、3 回） | done | [#266](https://github.com/YUKIKEDA/HyperPlot/issues/266)（設計）、[#274](https://github.com/YUKIKEDA/HyperPlot/issues/274) |
| I1b | 1 枚の図の外への対話（§6.2）: 二次のスケール（行 G6a）へのズームとパン（パネルの中は同じ割合、軸の上はその軸だけ）、ファセット（行 G6b）の共有する軸を一緒に動かすこと、図（行 G6c）の操作の相手の決め方とプロットごとのビュー、band の軸（行 G7b）のズームとパン、polar（行 G11）の半径のズームとパン | done | [#266](https://github.com/YUKIKEDA/HyperPlot/issues/266)（設計）、[#278](https://github.com/YUKIKEDA/HyperPlot/issues/278) |
| W1a | WPF のホストの土台（要件 H-1・H-2・H-3、§7.2）: `HyperPlot.Wpf` の `HyperPlotView`（`Spec`・`Engine`）、`D3D11SharedSurface` で共有テクスチャ 2 枚へ写して D3DImage へ渡す道、アダプタの LUID、`Dispatcher` ごとのバックエンド、`D3D11ReadbackSurface` で待たない読み出しの経路（ソフトウェア描画、フロントバッファの喪失、アダプタの不一致）、デバイスの喪失、DPI と大きさ（`PlotEngine.Resize`）、入力の変換とキャプチャとフォーカス（§6.1）、図の表示、`samples/WpfPlot`、写す時間の計測（§9） | done | [#282](https://github.com/YUKIKEDA/HyperPlot/issues/282)（設計）、[#286](https://github.com/YUKIKEDA/HyperPlot/issues/286) |
| W1b1 | 索引ができる前の暫定の表示とホストへの通知（要件 P-2、不変条件 4・7、§4.8 の「`PlotEngine` の待たない描き方」）: フレームの中で待たない `RenderFrame`（ピラミッドの M4 の暫定の結果、統計のあるチャンクからの学習、カテゴリの最小と最大・積み上げの和・X の統計は、できるまで描かず作り直しの間は前の版）、`IsProvisional`・`ProvisionalChanged`・`WorkFailed`、失敗した層の作り直し。計測: 1e9 点の構築中の暫定の `RenderFrame` の CPU 時間（NativeAOT） | done | [#282](https://github.com/YUKIKEDA/HyperPlot/issues/282)（設計）、[#291](https://github.com/YUKIKEDA/HyperPlot/issues/291) |
| W1b2 | 層を作る段の待ちをなくす（不変条件 7、§4.8、Issue #291 の決定 1・5、Issue #297）: Stat の派生の表、数の x の積み上げ、`ColorBy`・`FillBy` で分けた系列の集め、ファセットのパネルと集めを、計画ごとのスレッドプールのタスクで作り、層ごとの M4 の一括で差し替える。作り直しの間は前の版、作る段の失敗は `WorkFailed` | done | [#291](https://github.com/YUKIKEDA/HyperPlot/issues/291)（決定）、[#297](https://github.com/YUKIKEDA/HyperPlot/issues/297)（設計）、[#308](https://github.com/YUKIKEDA/HyperPlot/issues/308) |
| W1b3 | ラスタのミップマップの更新を待たない（不変条件 7、§4.8、Issue #291 の決定 6、Issue #297 の決定 1）: 最初の構築の間は描かず、`Invalidate` の後は GPU にあるタイルだけを描き、更新が失敗した行を作り直す | done | [#297](https://github.com/YUKIKEDA/HyperPlot/issues/297)（決定）、[#314](https://github.com/YUKIKEDA/HyperPlot/issues/314)（設計）、[#318](https://github.com/YUKIKEDA/HyperPlot/issues/318) |
| W1c | プロットの要約をアクセシビリティの API に渡す（要件 N-10、§7.1・§7.2）: `PlotEngine.Describe`、`HyperPlotView` の `AutomationPeer` | done | [#282](https://github.com/YUKIKEDA/HyperPlot/issues/282)（設計）、[#323](https://github.com/YUKIKEDA/HyperPlot/issues/323)（設計）、[#327](https://github.com/YUKIKEDA/HyperPlot/issues/327) |
| I2a | ヒットテスト（要件 I-2・P-5、§6.3）: `PlotEngine.HitTest`・`SeriesHit`・`HitMode`、系列の層の X の最近傍と XY の最近傍（ピラミッド）、image のテクセル、分けた系列・ファセット・Stat の行 | done | [#337](https://github.com/YUKIKEDA/HyperPlot/issues/337)（設計）、[#341](https://github.com/YUKIKEDA/HyperPlot/issues/341) |
| I2b | ホバー、クロスヘア、ツールチップ、End-to-end のホバーの場面（P-6）、層のテクスチャでの合成（§5.4、要件 R-5。マウスを動かしただけのフレームでデータ層を描き直さない。行 W1 から移した、Issue #282） | done | [#337](https://github.com/YUKIKEDA/HyperPlot/issues/337)（決定）、[#347](https://github.com/YUKIKEDA/HyperPlot/issues/347)（設計）、[#351](https://github.com/YUKIKEDA/HyperPlot/issues/351) |
| I2c | 要素の層（bar・error bar・rect。普通・カテゴリ・積み上げ・浮いた棒・並べた棒）のヒットテスト（要件 I-2、§6.3）と、ホバーのツールチップのカテゴリの軸の値（カテゴリ名、§6.5）。行 I2a から分けた（Issue #341 の決定 2） | done | [#341](https://github.com/YUKIKEDA/HyperPlot/issues/341)（決定）、[#355](https://github.com/YUKIKEDA/HyperPlot/issues/355)（設計）、[#359](https://github.com/YUKIKEDA/HyperPlot/issues/359) |
| I3a | 選択（要件 I-3・I-7、§6.4）: 矩形・X の範囲・投げ縄・クリックの点、`Selection`・`SelectionTool`・`RowSelection`、行の集合の API（区間とビット列）、強調、`Escape` で消す。行 I3 から分けた（Issue #364 の決定 1） | done | [#364](https://github.com/YUKIKEDA/HyperPlot/issues/364)（設計）、[#368](https://github.com/YUKIKEDA/HyperPlot/issues/368) |
| I3b | リンク（要件 I-4、§6.4）: `LinkGroup` で、図のプロットの間とファセットのパネルの間のビュー（x・y・両方）・カーソル・選択の共有、ファセットのパネルも入れられる形（行 G6b・G6c。共有する軸の既定の動きは行 I1b のまま、Issue #364 の決定 2）。行 I3 から分けた | done | [#364](https://github.com/YUKIKEDA/HyperPlot/issues/364)（分け方）、[#373](https://github.com/YUKIKEDA/HyperPlot/issues/373)（設計）、[#377](https://github.com/YUKIKEDA/HyperPlot/issues/377) |
| I4a | 注記と注釈（要件 G-10・I-5、§4.6・§6.5）: `Subtitle`・`Caption`、線・矩形・テキスト・マーカーの注釈（データの座標かパネルの割合に軸ごとに固定）、`AnnotationTool` のドラッグと `AnnotationChanged`。行 I4 から分けた（Issue #382 の決定 1） | done | [#382](https://github.com/YUKIKEDA/HyperPlot/issues/382)（設計）、[#386](https://github.com/YUKIKEDA/HyperPlot/issues/386) |
| I4b | 計測（要件 I-6、§6.5）: 2 点の差（Δx・Δy・傾き）、範囲の統計（件数・平均・最小・最大）。行 I4 から分けた（Issue #382 の決定 1）。`MeasureTool`（2 点と範囲）、`Statistics`、ピラミッドのバケットの和 | done | [#390](https://github.com/YUKIKEDA/HyperPlot/issues/390)（設計）、[#394](https://github.com/YUKIKEDA/HyperPlot/issues/394) |
| **D: 散布の大規模化とストリーミング** | | | |
| C3a | 空間タイル索引（要件 D-4・R-3・P-15、§3.4）: Morton 順に並べ直した写し、四分木の表、問い合わせ（矩形の中の点の数、矩形にかかるセル、写しの点と元の行）、一時ファイル、構築の計測、P-15 の値と `Dispose` が確保の仕事を待ってよい時間を決める ADR。行 C3 から分けた（Issue #401 の決定 1） | done | [#401](https://github.com/YUKIKEDA/HyperPlot/issues/401)（設計）、[#405](https://github.com/YUKIKEDA/HyperPlot/issues/405) |
| C3b | 密度タイル（要件 R-3・P-8、§3.4）: 256 × 256 のビンのタイルと 2 × 2 の和、メモリの計測、`DensitySplit` の値。行 C3 から分けた（Issue #401 の決定 1） | done | [#413](https://github.com/YUKIKEDA/HyperPlot/issues/413)、設計は [#401](https://github.com/YUKIKEDA/HyperPlot/issues/401) |
| S4 | 裏の構築が CPU を譲るか（ピラミッド、空間タイル索引、密度タイル、統計の先行計算）。行 C3a・C3b の計測で、構築が 1e9 点で 1〜3 分のあいだ平均 8.5 コアを使うと分かった（オーナーの指摘、2026-11-11）。優先度を下げる方式と、混み具合を測って休む方式を実装して測り、どちらも取らなかった: 譲る仕組みは作らず、ワーカーは通常の優先度のまま OS の配分に任せる（オーナーの決定、2026-11-12）。要件 N-12 を実測に合わせて書き直した。`docs/adr/0003-no-yield.md`、`docs/benchmarks/s4-yield.md` | done | [#414](https://github.com/YUKIKEDA/HyperPlot/issues/414)（設計）、[#422](https://github.com/YUKIKEDA/HyperPlot/issues/422)（設計の書き直し）、[#421](https://github.com/YUKIKEDA/HyperPlot/issues/421)（計測と決定） |
| R3a | 散布の生の点（要件 R-3・R-4・P-4、§5.5.2）: point の仕様とビルダ（層で 1 つの大きさ・形・不透明度、カテゴリと数の列の色）、`DrawMarkers`、2 つの道（行の数が 131,072 以下の層は毎フレーム CPU で写す、それより多い層は部分ごとの索引の Morton 順の写しを GPU に常駐させる）、索引ができるまでの暫定の印とヘッドレスの待ち、CPU の道のヒットテストとホバー、polar は CPU の道だけ。1e7 行を超える層と、索引の道で 64 を超える部分に分かれる層は例外。行 R3 から分けた（Issue #431 の決定 1） | done | [#431](https://github.com/YUKIKEDA/HyperPlot/issues/431)（設計）、[#437](https://github.com/YUKIKEDA/HyperPlot/issues/437)（CPU の道）、[#438](https://github.com/YUKIKEDA/HyperPlot/issues/438)（索引の道） |
| R3f | 点の大きさと形のチャネル（要件 R-4）: 大きさと形を列から決める `SizeBy`・`ShapeBy`、そのスケールと凡例、形の追加。行 R3 から分けた（Issue #431 の決定 1・8） | done | [#448](https://github.com/YUKIKEDA/HyperPlot/issues/448)（設計）、[#454](https://github.com/YUKIKEDA/HyperPlot/issues/454)（実装の 1 本目）、[#456](https://github.com/YUKIKEDA/HyperPlot/issues/456)（実装の 2 本目） |
| R3b1 | 散布の密度と切り替え（要件 R-3・P-3、§5.5.2）: 密度タイルの常駐と描画（画像の命令 `DrawImage` に乗せる）、画面内の点の数での切り替えと利用者の指定（`PointDraw`）、色の写像（log・線形・立方根）、索引ができるまでの粗い密度、上限のセルと疎なセル、1e7 行を超える層の例外を外す。行 R3b を分けた（Issue #462） | in-progress | [#462](https://github.com/YUKIKEDA/HyperPlot/issues/462)（設計） |
| R3b2 | 散布の重なりと予算（§5.5.2）: 点の数 × 印の面積での密度への切り替え（倍数は計測で決める）、予算に入らない画面の見せ方、1e9 点の写しの読み出し（ページ）をワーカーに移すか、毎フレーム引く葉とセルの表を表の行の数で抑えたままでよいか、写しがページキャッシュにない場合（メモリマップの列）のページの読み出しの時間。行 R3f から送られたもの（Issue #456）: 1e7 点を 6 つの形に分けた層の全体表示は、GPU 時間の p50 が 17.3〜18.0 ms で 16.7 ms を超える（6 つの色に分けた層は 15.5〜16.1 ms。`docs/benchmarks/r3f-size-shape.md`）。形の違いからではなく、部分がどれも同じ色であることから遅い（同じ列で色と形に分けると 15.4 ms）。その GPU の中の理由は確かめていない。行 R3a から送られたもの（Issue #438）: 点が重なる画面は生の点で 16.7 ms を超える（1e7 点の中央の塊を直径 6 DIP で全体表示して 18.2 ms、8 カテゴリで 17.5〜17.6 ms。`docs/benchmarks/r3a-points.md`）ので、点の数 × 印の面積でも密度に切り替えるか、写しがページキャッシュにない場合（メモリマップの列）のページの読み出しの時間。行 R3b を分けた（Issue #462） | not-started | |
| R3c | 散布のヒットテストと選択（要件 P-5、§6.3・§6.4）: 索引の道の半径のヒットテスト（`GetCells` の `maxLevel` を画素の大きさで止めるか。行 C3a の計測では、半径の中の点を全部読むと p99 が 10〜19 ms）、密度の領域の点の数と `SeriesHit` の形、範囲と重ならない矩形の `Count`、選択した点の強調（シェーダが述語で判定する生の点と、選択部分の密度）、point の層の選択。常駐の点は目盛りに寄って描かれることがあり（§5.5.2 の「限界」）、倍精度の値で答えるヒットテストの位置とずれるので、どちらに合わせるかを決める。行 R3 から分けた | not-started | |
| R3d | 散布の色のカテゴリと点ごとの色（§5.5.2）: 索引の道で数の列の色を点ごとに付ける（エンコードの列の Morton 順の並べ直し）、索引の道の `SizeBy`（点ごとの大きさ。行 R3f は CPU の道だけ、Issue #448）、64 を超える部分に分かれる大きな層を 1 つの索引で描く、同じ列を描く層の間で索引を使い回すか（行 R3a は使い回さない）、カテゴリごとの密度の合成、色のスケールの写像をチャンクごとに持つ形。行 R3 から分けた | not-started | |
| R3e | ほかの Geom の残り: point のスケールとカテゴリの x の line・step（行 G7b から、§4.2）、索引の道の polar の point（頂点シェーダで写す、§4.4）、pointrange、segment と rect の大きなデータの道（131,072 を超える要素の密度、§5.5.3）、線分ごとの色（rule・segment・errorbar の要素ごとの連続の色、行 G7c から、§4.1）。行 R3 から分けた | not-started | |
| C4 | ストリーミングとリングバッファ（全体表示を追い続ける表示と、ビューの間に学び直した後の全体表示の値と通知、行 I1a の `FullStale`）。空間タイル索引への追記は v1 で作らない（要件 §7、Issue #401 の決定 6） | not-started | |
| **E: XylemUI と公開** | | | |
| X1 | XylemUI のホスト | not-started | |
| Q1 | 性能の受け入れの計測（要件 §5） | not-started | |
| V1 | v1 の公開（NuGet、利用者向けの文書、サンプル） | not-started | |
| **v1 の後: 3D（v1.x）** | | | |
| K0a | 3D の共通の設計と八分木（要件 §4.6、§13）: 座標系（`CoordSpec` の 3D の cartesian、`PlotSpec` の z のスケール、層の Z チャネル、flip・polar・二次軸・band・Position・ファセット・Stat との組ごとに受けるか断るかの表）、GPU に送る位置の精度、深度の契約（3D のパネルに 2D のマークを深度付きで置く要件があるか）、カメラ、ビュー（3D のズームとパンが動かすもの）、K-7 のレイのヒットテスト、性能の仮説（P-11、八分木の構築時間、写しと節点の表のメモリ）。八分木（加法方式、シードつきの 128³ の格子の間引き、静止したときの描き足し）の節点の大きさと節点の表のメモリ、点の予算の単位と既定値、点の描き方（印のパイプラインと計算シェーダのラスタ化の試作）。ファセットの `Scales`（`Free` など）が z にも効くか。値は試作で確定する。行 K0 を分けた（Issue #470 の決定 2・4） | in-progress | [#470](https://github.com/YUKIKEDA/HyperPlot/issues/470)（設計）、[#477](https://github.com/YUKIKEDA/HyperPlot/issues/477)（試作） |
| K0b | 3D の線の単純化の設計（§13.4）: 区間の分け方と誤差の段、K-3 の線（1e8 頂点）の回転の目標。値は試作で確定する。行 K0 を分けた（Issue #470 の決定 2・4） | not-started | |
| K0c | 面の LOD の設計（§13.5）: 格子のタイルと解像度の段、隣のタイルとの段の違いで生じる隙間の方式、P-12 の目標。値は試作で確定する。行 K0 を分けた（Issue #470 の決定 2） | not-started | |
| K0d | 半透明とメッシュの設計（§13.5・§13.7）: 描く順によらない合成の方式の比較（要件 K-8）、メッシュの描き方、P-13 の目標。値は試作で確定する。行 K0 を分けた（Issue #470 の決定 2） | not-started | |
| K0e | ボリュームの設計（§13.6）: ブロックの大きさ（仮の値 64³）、P-14 の目標。ブロックの大きさ、P-14 の目標、段とゴーストの追加のメモリ（要件 P-8 の例外にするか）、隠れたブロックを送る転送の量は試作で確定する。オーナーが決めること: NaN の符号とその描き方、早い打ち切りのしきい値と段の境の継ぎ目（Engel ら 2006 を確かめてから）、WBOIT とレイマーチの合成の順（行 K0d と）。行 K0 を分けた（Issue #470 の決定 2） | in-progress | [#484](https://github.com/YUKIKEDA/HyperPlot/issues/484)（設計）、[#491](https://github.com/YUKIKEDA/HyperPlot/issues/491)（試作） |
| K1 | 3D の座標系・カメラ・軸の箱・3D のインタラクション、線分と点のパイプラインの深度と 3 成分の位置、4×4 の変換の公開の契約と `RenderList.SetTransform`（不変条件 12。行 R3 から、Issue #431 の決定 11）。設計は行 K0a | not-started | |
| K2 | 3D 散布（点群、八分木の LOD。行 C3a の、次元の数 D を引数に取る部品の上に作る、§3.4・§13.1）。設計は行 K0a | not-started | |
| K3 | 3D の線（軌跡）。設計は行 K0b（共通の部分は行 K0a） | not-started | |
| K4 | 面（z = f(x, y)、格子の LOD）。設計は行 K0c（共通の部分は行 K0a） | not-started | |
| K5 | メッシュと半透明の合成。設計は行 K0d（共通の部分は行 K0a） | not-started | |
| K6 | ボリューム（レイマーチ、断面、等値面）。設計は行 K0e（共通の部分は行 K0a） | not-started | |
| **v1 の後: ホストとバックエンド** | | | |
| A1 | Avalonia のホスト | not-started | |
| P1 | wgpu-native のバックエンド（Linux） | not-started | |
| P2 | Metal のバックエンド（macOS）。空間タイル索引の一時ファイルの macOS での作り方（それまで、macOS ではメモリマップの列の索引の構築が `PlatformNotSupportedException` で失敗する、§3.4） | not-started | |

## 意図的に対象外

要件定義書 §7 のとおり。
