# S3: ゴールデン画像の許容差

行 S3（Issue #70）の記録。設計書 §10.1 の 2 場面のゴールデン画像を、手元の WARP で基準に書き、CI の WARP で比べて、許容差を決めた。

結論: 手元と CI の WARP は版が違うが、2 場面とも画素まで同じだった。許容差は `ImageTolerance.Exact`（各チャネル 0、超えてよい画素 0）にする。

## 手元と CI の差

| | 手元 | CI（windows-latest） |
| --- | --- | --- |
| WARP（`d3d10warp.dll` のファイルの版） | 10.0.26100.9818 | 10.0.26100.33648 |
| `hello.png`（1,280 × 720、`HelloPlot` の場面を 1e5 点） | 基準を書いた | `Exact` で合う（超えた画素 0） |
| `lines.png`（320 × 240、R2 の線の種類） | 基準を書いた | `Exact` で合う（超えた画素 0） |

- CI の版は、CI の手順「WARP version」が毎回出す。CI の回は 40808604937（コミット acad58e）
- 基準は手元で `HYPERPLOT_UPDATE_GOLDEN=1` を立てて書いた。基準を書く前の回は、基準を作らずに失敗し、実際の画像だけを `golden-output/` に書いた（設計 §10.1 のとおり）
- 予想（Issue #70）: 現実的な範囲（同じ版なら 0）の内。版が違っても 0 だったので、予想より差が小さい

### `Exact` にした理由

測った差が 0 なので、許容差を広げる理由がない。許容差を広げると、線の切れや端の 1 画素の変化を見落とす（設計 §10.1 の「比べ方」）。CI の WARP の版が上がって画素が変わったときは、テストが落ち、CI が実際の画像と差の画像を artifact（`golden-output`）として上げる。そのときに差を測り、基準を書き直すか許容差を広げるかを、その差を書いた PR で決める（`.cursor/rules/testing.mdc`）。

## テストの時間

WARP、Release、手元。TRX の報告の時間:

| テスト | 時間 |
| --- | --- |
| `HelloScene_Warp_MatchesBaseline`（1e5 点 × 4 系列のピラミッドと M4、1,280 × 720 の描画と比較） | 0.158 s（基準を書く前の回は 0.171 s） |
| `LineKinds_Warp_MatchBaseline`（320 × 240） | 0.012 s（同 0.013 s） |

予想（現実的な範囲 0.1〜0.5 s、最悪 2 s）の内。

## `HelloPlot` の場面を出す前と後

`HelloPlot`（1e6 点）の PNG は、場面を `samples/Shared/HelloScene.cs` に出す前と後で、ファイルのハッシュまで同じだった（WARP とハードウェアの両方、JIT の Release）。NativeAOT で発行した `HelloPlot.exe`（警告 0、1,670,656 バイト。出す前は PR #63 の発行で 1,670,144 バイト）の WARP の PNG も、出す前の PNG と同じだった。

## 再現の手順

```powershell
# 基準を書く（書いた PNG は PR の差分で見る）
$env:HYPERPLOT_UPDATE_GOLDEN = '1'; dotnet test --project tests/HyperPlot.Rendering.D3D11.Tests -c Release -- --filter-class "*GoldenImageTests"; Remove-Item Env:HYPERPLOT_UPDATE_GOLDEN
# 比べる
dotnet test --project tests/HyperPlot.Rendering.D3D11.Tests -c Release -- --filter-class "*GoldenImageTests"
```
