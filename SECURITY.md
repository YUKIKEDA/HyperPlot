# Security Policy

If you find a vulnerability, **do not write it in a public Issue**.

Report it through [GitHub Security Advisories](https://github.com/YUKIKEDA/HyperPlot/security/advisories/new). No personal email address is published.

Response targets (best effort):

- Acknowledgement: within a few days
- Plan for a fix: depending on severity

HyperPlot reads column data from memory-mapped files and calls native GPU APIs from unsafe code. A data file, a chart spec, or a sequence of API calls that makes it read or write outside a buffer, allocate without bound, or hang the process is a vulnerability. Report it here. Do not include private or production data in reproduction steps; a generated file that shows the same behavior is enough.
