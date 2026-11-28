# Changelog

All notable changes to HyperPlot are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). A 0.x minor
version may break the public API.

## [Unreleased]

### Changed

- A stacked area or band answers a hit test and the range statistics by each side's outer edge alone, as stacked bars do:
  series 2 · group for its positive side and 2 · group + 1 for its negative one. A band's inner edge, and a side with no
  height at the row found, no longer answer, so a stack of one sign also changes its numbers (#361).
