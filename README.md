# HyperPlot

[日本語](README.ja.md)

A plotting library for C# / .NET 10, under development. It aims at three things:

- **NativeAOT**: the core, the grammar, and the renderer have zero trim / AOT warnings.
- **Billion-point data at interactive speed**: zoom and pan 1e9-point series and scatter plots at 60 fps. Indexes are built once off the frame path: chunk statistics, min/max LOD pyramids with M4 extraction, Morton-ordered tiles, and density tiles. Each frame does work bounded by the screen, not by the data.
- **A Grammar of Graphics with rich interaction**: layers, scales, coordinates, facets, guides, selections, and linked views. The API is typed C#.

Rendering calls Direct3D 11 directly on Windows. Hosts come in this order: WPF, then XylemUI, then the v1 release, then Avalonia.

> Status: pre-alpha. The design is fixed in `docs/`, and the first implementation rows are in progress. The API will change.

## Documents

| What | Where |
| --- | --- |
| Requirements (Japanese) | [docs/requirements.ja.md](docs/requirements.ja.md) |
| Design (Japanese) | [docs/design.ja.md](docs/design.ja.md) |
| Rejected alternatives | [docs/design-decisions.md](docs/design-decisions.md) |
| Roadmap | [docs/roadmap.md](docs/roadmap.md) |
| Conventions | [docs/conventions.md](docs/conventions.md) |
| Contributing | [CONTRIBUTING.md](CONTRIBUTING.md) |

## Build

Requires the .NET 10 SDK. A NativeAOT publish on Windows also needs MSVC (the Visual Studio "Desktop development with C++" workload).

```powershell
./build.ps1
```

## License

[MIT](LICENSE)
