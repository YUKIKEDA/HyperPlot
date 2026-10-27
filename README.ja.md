# HyperPlot

[English](README.md)

C# / .NET 10 のプロットライブラリ（開発中）。目指すのは次の 3 つです。

- **NativeAOT**: コア・GoG・描画は trim / AOT の警告ゼロ
- **10 億点を対話的な速さで**: 10 億点の時系列・散布図を 60 fps でズーム・パンする。索引はフレームの外で一度だけ作る（チャンクの統計、min / max の LOD ピラミッドと M4 の抽出、Morton 順のタイル、密度タイル）。各フレームの仕事は、データの点数ではなく画面の大きさで決まる
- **Grammar of Graphics と高度なインタラクション**: レイヤー・スケール・座標系・ファセット・ガイド・選択・リンクしたビュー。API は型付きの C#

描画は Windows で Direct3D 11 を直接呼びます。ホストは WPF → XylemUI → v1 公開 → Avalonia の順に対応します。

> 状態: プレアルファ。設計は `docs/` に固定済みで、最初の実装の行に着手しています。API は変わります。

## 文書

| 内容 | 場所 |
| --- | --- |
| 要件定義 | [docs/requirements.ja.md](docs/requirements.ja.md) |
| 設計 | [docs/design.ja.md](docs/design.ja.md) |
| 却下した案 | [docs/design-decisions.md](docs/design-decisions.md) |
| ロードマップ | [docs/roadmap.md](docs/roadmap.md) |
| 開発規約 | [docs/conventions.md](docs/conventions.md) |
| 貢献の手順 | [CONTRIBUTING.md](CONTRIBUTING.md) |

## ビルド

.NET 10 SDK が要ります。Windows で NativeAOT の公開をするには MSVC（Visual Studio の「C++ によるデスクトップ開発」）も要ります。

```powershell
./build.ps1
```

## ライセンス

[MIT](LICENSE)
