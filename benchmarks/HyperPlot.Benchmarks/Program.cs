using BenchmarkDotNet.Running;

// Kernel benchmarks (design §9, .cursor/rules/bench.mdc). Run from the repository root:
// dotnet run -c Release --project benchmarks/HyperPlot.Benchmarks -- --filter "*"
BenchmarkSwitcher.FromAssembly(typeof(HyperPlot.Benchmarks.KernelData).Assembly).Run(args);
