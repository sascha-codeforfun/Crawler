# Security Policy

## Intended use and threat model

Crawler is built to crawl a website **you own or control**. Point it at your
own site, and it captures a snapshot and reports what it finds.

The security hardening added in 1.1.0 — an admission gate over fetching and
writing — is **defense-in-depth for the realistic accidents of crawling your
own property**: a compromised page, an open redirect, an unexpected link
target. It is not a guarantee that the tool is safe to aim at arbitrary,
untrusted, or hostile sites. Crawling targets you don't trust is outside the
intended use, and that risk rests with the operator.

In normal use the practical risk surface is therefore dominated by deployment
and configuration choices — where it runs, what it is pointed at, and where its
output is opened — rather than by remote attackers.

## Supported versions

| Version | Supported |
| ------- | --------- |
| 1.1.x   | yes |
| 1.0.x   | no — superseded by 1.1.0; please upgrade |

Latest release: https://github.com/sascha-codeforfun/Crawler/releases/latest

## Reporting an issue

Security issues are handled in the open, like any other bug. **Please open a
regular [GitHub issue](https://github.com/sascha-codeforfun/Crawler/issues)**
and include:

- the version or commit affected,
- the component involved (fetching/redirects, file/snapshot writing, or
  exported CSV/log output),
- a clear description and minimal steps to reproduce,
- any relevant configuration, with real hostnames, paths, and credentials
  removed.

The most useful reports describe a **bypass of the 1.1.0 admission gate** —
for example a way to defeat the host allowlist, escape the snapshot write
root, or smuggle a formula into exported output. Those are genuine code
defects rather than configuration mistakes, and they are exactly what the
hardening is meant to stop.

This is a small, best-effort project with no formal SLA. Valid reports will be
acknowledged, assessed, and — when confirmed — fixed in a later release with
credit, unless you prefer otherwise.
