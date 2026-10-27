# 参考資料

層を作る前に読むもの。ここにある資料の内容を設計書に書くときは、読んだ節・ファイルと行を設計書に書く（`.cursor/rules/design-docs.mdc`）。本書は「どこを読むか」の一覧で、読んだ結果ではない。

| 層 | 読むもの | 何のために |
| --- | --- | --- |
| 先行の POC | `D:\home\Programs\CSharpProjects\HighPerfPlotPOC`（`spec.md`、`HighPerfPlot.Core/Renderring/`、`Spatial/QuadTree.cs`、`Chart/InteractiveChartRenderer.cs`） | 層の差分描画、インスタンス描画、ホバーの経路。精度と軸のラベルの問題 |
| 同じ方針の GPU のコード | `D:\home\Programs\CSharpProjects\XylemUI`（`src/XylemUI.Rendering.D3D11/`、`docs/design.ja.md` §5） | TerraFX での D3D11 の呼び方、WARP のテスト、シェーダのビルド、デバイスの喪失 |
| 時系列の間引き（C2） | Jugel, Jerzak, Hackenbroich, Markl. "M4: A Visualization-Oriented Time Series Data Aggregation." PVLDB 7(10), 2014 | M4 が画素の結果を保つ条件 |
| 時系列の間引き（比較） | Steinarsson. "Downsampling Time Series for Visual Representation." 2013（LTTB）。`MinMaxLTTB`（Van Der Donckt ら 2023） | M4 を既定にした理由の比較 |
| 散布の密度（C3・R3） | datashader の文書（Pipeline: aggregation と shading） | 集計と色の写像を分ける構成、eq_hist |
| GoG（G1〜G6） | Wilkinson. *The Grammar of Graphics*. 2005。Wickham. *ggplot2: Elegant Graphics for Data Analysis* 3e。Vega-Lite の仕様（encoding、scale、selection / param、facet、concat） | 仕様モデル、チャネル、Stat と Position、selection |
| 目盛り（G2） | Talbot, Lin, Hanrahan. "An Extension of Wilkinson's Algorithm for Positioning Tick Labels on Axes." InfoVis 2010 | 線形の目盛りの選び方 |
| プロットの種類の拡充（G-14） | Plotly の文書（Python と JavaScript の figure reference の trace の一覧）、matplotlib の gallery、ggplot2 の reference の geom の一覧 | 次に足す種類の候補。足す前に、その種類の属性を読んだページを Issue に書く |
| 大規模のプロットの製品 | SciChart の文書（Resampling、Performance tips）。ScottPlot の文書（SignalPlot、DataStreamer） | 利用者が期待する機能と API の形。速さの主張は製品の文書であり、読んだページを書いてから比べる |
| WPF の組み込み（W1） | Microsoft Learn: `D3DImage`、「WPF and Direct3D9 Interoperation」。D3D11 の `GetSharedHandle` と D3D9Ex の `CreateTexture` の共有 | 共有テクスチャ、フロントバッファの喪失、ソフトウェア描画 |
| 3D の点群（K2） | Schütz. "Potree: Rendering Large Point Clouds in Web Browsers." 修士論文, TU Wien, 2016 | 八分木の節点ごとの代表の点、点の予算 |
| 3D の面（K4） | Losasso, Hoppe. "Geometry Clipmaps." SIGGRAPH 2004。Ulrich. "Rendering Massive Terrains using Chunked Level of Detail Control." 2002 | 格子の LOD と隙間の処理 |
| 3D の半透明（K5） | McGuire, Bavoil. "Weighted Blended Order-Independent Transparency." JCGT 2(2), 2013 | 描く順によらない合成 |
| ボリューム（K6） | Engel ら. *Real-Time Volume Graphics*. 2006。Lorensen, Cline. "Marching Cubes." SIGGRAPH 1987 | レイマーチ、伝達関数、等値面 |
| テキスト（T1） | Microsoft Learn: DirectWrite（`IDWriteTextLayout`、`IDWriteGlyphRunAnalysis`、フォールバック） | 整形とラスタライズ |
