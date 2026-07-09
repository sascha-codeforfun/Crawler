# Testing notes — what we test, and what we deliberately don't

_Last updated: 2026-06-15_

This document explains the testing approach for the Crawler codebase, and in
particular **why certain code is intentionally left without unit tests**. It
exists so that the uncovered lines in a coverage report read as deliberate
engineering decisions rather than as an open backlog — and so nobody later
"fixes" the coverage number with tests that add ceremony but no protection.

## Guiding principle

We test where a regression can actually bite **and** where a unit test is the
thing that would catch it. Coverage percentage is treated as a diagnostic, not
a goal. A test earns its place when a plausible future change could silently
break the behaviour and ship — not because a line is currently red.

Concretely, the value of covering a line drops sharply depending on what kind
of line it is:

- **High value** — a method with real branching logic and no existing test.
  A bug there changes output correctness and nothing else would notice.
- **Medium value** — an edge arm of logic whose core is already tested. A test
  pins a real behaviour, but the behaviour is mostly already exercised.
- **Low value / ceremony** — auto-generated property accessors, state-machine
  plumbing, fault-only `catch` blocks, and generated code. Covering these
  asserts that the compiler did its job, or mocks a failure to reach a branch
  that only logs and continues.

The lines documented below sit firmly in that last tier, or are reachable only
through integration (real network, real console, real filesystem faults).

## What IS tested

The high-value surface has real coverage of its actual logic: the analyzers
(canonical, redirect, resource-bloat, asset/PDF quality, self-link, SEO), the
extractors (URL, Base64 asset, JS string literal), the validators (character,
config migration guards), the log/line writers, and the dictionary / spell
support types. These are the components where a silent correctness bug would
corrupt findings, tickets, or output, and where a deterministic unit test is
the right safety net.

## What we deliberately do NOT unit-test, and why

### Generated code

`RegexGenerator.g.cs` is emitted by the `[GeneratedRegex]` source generator.
Testing it tests the compiler, not our code. Excluded by category.

### Console / interactive I/O

`ConsoleUi`, `InteractiveTriage`, `ConsoleTriage`, and the interactive portions
of `ContentQualityTriage` and `SpellTriage` are driven by
`Console.ReadKey(intercept: true)`. This cannot be redirected under the xUnit
test host, and low-level keyboard interception can additionally trip behavioural
checks on hardened / EDR systems — so we will not fake-cover it with brittle
console injection. The non-interactive helpers inside these files (formatting,
parsing, row composition) are tested where they carry logic. `Logger` is a
console/file sink with negligible branching; its behaviour is I/O, not logic.

### Orchestration and entry points

`CrawlOrchestrator`, `AnalysisPipeline`, `Program`, `Crawler.cs`, and
`CrawlHistoryDiagnostic` are top-level wiring that composes already-tested
units. Their value is in integration behaviour — the order things run, how the
pieces connect — which a unit test does not capture. Testing them in isolation
would mostly assert that mocks were called in the order the test itself
specified.

### Network

`CrawlAsset`'s download core is built on a static `HttpClient` with no injection
seam, so exercising it requires a live or stubbed HTTP server — integration
territory, not a unit test. Its `Initialize` (proxy/handler setup) is the only
unit-shaped part and is low-value in isolation. `ConnectivityCheck` is covered
to its testable ceiling: the identity-gathering and pre-network failure paths
are tested; the actual socket send / HEAD round-trip is integration.

### Fault-injection-only paths

The retry loops and I/O `catch` blocks in `FileIo` (and the equivalent
write/read catches in the asset/log writers) are reachable only by forcing a
disk fault mid-operation. These are flaky to trigger reliably across platforms,
and the catch bodies simply log and continue. The happy paths and input-shape
branches of these methods are tested; the fault arms are left as documented
residual.

### Plumbing

Record auto-property setters/getters that are never invoked by name,
compiler-generated `MoveNext` state-machine lines, and record copy-constructors.
Covering these asserts that the language generated the accessor it was asked to.

### Already-protected residual (real logic, edge arms only)

Several files show a handful of uncovered lines despite having dedicated, solid
test suites: `IssueTracking` promoters, `ConfigDeltaWriter` comment matchers,
`RedirectAnalyzer`, `UrlExtractor`, `JsStringLiteralExtractor`,
`ScriptPageIndex`, `Config.ValidateConfig`, `DomTraverser`, and `RunChecker`.
In each case the **core logic is covered**; the remaining lines are deep or
defensive branches (e.g. a near-duplicate of an already-tested code path, or a
guard that is effectively unreachable through the public API). A test there
would restate an existing guarantee. This is past the value/ceremony line, so
we stop.

## Reference: largest intentionally-uncovered files

Approximate uncovered-line counts (not-covered + partial) at the time of
writing. These are illustrative of the categories above, not a target list.

| File | ~Uncovered | Category |
|------|-----------:|----------|
| `RegexGenerator.g.cs` | 1440 | Generated |
| `CrawlOrchestrator.cs` | 920 | Orchestration |
| `ConsoleUi.cs` | 830 | Console / interactive |
| `AnalysisPipeline.cs` | 620 | Orchestration |
| `ContentQualityTriage.cs` | 610 | Console / interactive |
| `SpellTriage.cs` | 490 | Console / interactive |
| `CrawlHistoryDiagnostic.cs` | 480 | Orchestration |
| `Program.cs` | 325 | Entry point |
| `Crawler.cs` | 285 | Orchestration |
| `InteractiveTriage.cs` | 275 | Console / interactive |
| `ConsoleTriage.cs` | 190 | Console / interactive |
| `Logger.cs` | 70 | Console / file sink |
| `ConnectivityCheck.cs` | (HTTP path) | Network |
| `CrawlAsset.cs` | (download core) | Network |

The bulk of the remaining uncovered lines — well over two-thirds — falls into
the generated, console, and orchestration categories above.

## Guidance for future changes

- When a change adds **new branching logic** to a tested unit, add a test for
  the new behaviour.
- When a change touches an **orchestration / console / network** file, prefer an
  integration test (or manual verification) over contorting a unit test; if the
  change extracts a piece of pure logic out of that file, unit-test the extracted
  piece.
- Do **not** add tests purely to raise a coverage number against the categories
  above. If a line is uncovered because it is generated, plumbing, fault-only,
  or interactive, that is by design — see the relevant section here before
  treating it as a gap.
- If you find yourself mentally reframing a hard-to-reach line as "worth a quick
  test," check whether the test would catch a real regression or merely assert
  an existing guarantee. Prefer the former; skip the latter.
