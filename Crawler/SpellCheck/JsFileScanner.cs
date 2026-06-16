using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HtmlAgilityPack;

namespace Crawler.SpellCheck
{
	/// <summary>
	/// 659 — the per-bundle findings emitted out of the file scan, grouped so the eventual
	/// SCRIPT_SPELLING tickets can be routed. Reach decides routing: CLEAR (<see cref="IsBulk"/> false,
	/// reach ≤ threshold) → tickets carry the reached page(s); BULK (reach &gt; threshold) → tickets
	/// carry the stable, hash-stripped bundle key so they survive a re-deploy. The words are the distinct
	/// flagged tokens for this bundle (first excerpt kept). This is the payload; turning it into
	/// <c>IssueRecord</c>s and into the W→U triage loop is the next step, not done here.
	/// Namespace-level so it is shared across the scanner classes and tests.
	/// </summary>
	public readonly record struct ScriptBundleFindings(
		string BundlePath,
		string StableKey,
		string BundleUrl,
		int Reach,
		bool IsBulk,
		IReadOnlyList<string> Pages,
		IReadOnlyList<ScriptWordHit> Words);

	/// <summary>
	/// Opt-in diagnostic (ScanScriptFilesInDownload, default OFF): spell-checks the raw external .js
	/// files in the crawl directory — the big site bundles — reusing the inline scanner's core
	/// (<see cref="BulkScriptScanner.ScanText"/>). Unlike the inline pass there is no harvest/blob:
	/// each file IS its own source, so provenance is the file's path (relative to the crawl directory),
	/// not an injected header. Same dictionaries, same gates ON, pruning OFF. 656 — TokensToFilter is
	/// now wired here too (whole-literal, single-token-safe, so it can never hide a misspelling); it stays
	/// OFF only on the bulk-PAGE audit pass, whose job is to run raw and surface blind spots.
	///
	/// Encoding: JS has no reliable charset signal and bundlers concatenate mixed-encoding sources, so
	/// files are read UTF-8-with-replacement and any literal containing U+FFFD is skipped whole (see
	/// <see cref="BulkScriptScanner.ScanText"/>) — gaps are visible in the manifest, never mojibake.
	///
	/// 643 — minified vendor bundles are ~94% machine tokens, so the raw scan is a firehose. The
	/// per-literal PROSE-RATIO GATE (<see cref="ScriptProseRatioTau"/>) demotes any literal whose word
	/// tokens are mostly non-words (ids, events, glyph tables, kebab keys, framework dev-strings,
	/// runtime-split token lists) while keeping real sentences — so a real <c>title:</c> / <c>text:</c>
	/// / <c>infoText:</c> typo still surfaces. Output is now TWO logs:
	///   • log 30 (full / debug) — every scanned file, every surviving finding, AND a one-line note for
	///     each gated literal ("# gated (ratio …) · file · literal"). The audit source of truth.
	///   • log 31 (trimmed / triage) — the same manifest, but only the surviving findings. The list a
	///     human triages. Log 30 is the debug source from which log 31 is distilled.
	/// Both are written in the SAME single pass (no second scan).
	/// </summary>
	public static class JsFileScanner
	{
		public readonly record struct Result(int Files, int Findings, int SkippedUndecodable, int FilesTooLarge, int GatedLiterals, int SuppressedFindings, IReadOnlyList<ScriptBundleFindings> BundleFindings);

		// 659 — assemble one bundle's emitted findings: dedupe raw hits to distinct words (first excerpt
		// kept, original order), and bake the reach-based routing decision. Pure and side-effect-free so
		// the routing/dedupe contract is unit-testable without a spell checker or a crawl directory.
		internal static ScriptBundleFindings BuildBundleFindings(
			string bundlePath, string stableKey, string bundleUrl, int reach, int reachThreshold,
			IReadOnlyList<string> pages, IEnumerable<ScriptWordHit> rawHits)
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var words = new List<ScriptWordHit>();
			foreach (var hit in rawHits)
			{
				if (seen.Add(hit.Word))
				{
					words.Add(hit);
				}
			}

			bool isBulk = reach > reachThreshold;
			return new ScriptBundleFindings(bundlePath, stableKey, bundleUrl, reach, isBulk, pages, words);
		}

		// Pathological-file guard for the raw read. The site bundles we target are single-digit MB; this
		// only fences off a runaway file from OOMing the run. [REVIEW] promote to config if ever needed.
		private const long MaxFileBytes = 32L * 1024 * 1024;

		// 643 — prose-ratio gate threshold: a literal is demoted when (union-miss word tokens / total
		// word tokens) >= this. 0.4 cleanly separates the observed noise classes (≥0.50) from real
		// German prose carrying a typo (≤~0.25) on log 30's real distribution. [REVIEW] promote to a
		// config knob (e.g. SpellCheckJavaScript.ScriptProseRatioTau) if a site ever needs to retune.
		private const double ScriptProseRatioTau = 0.4;

		public static Result Run(
			string downloadDirectory,
			string findingsPath,
			string trimmedFindingsPath,
			string uniqueWordsPath,
			string routingPreviewPath,
			string pagePattern,
			int reachThreshold,
			Func<string, string> fileToUrl,
			IReadOnlyList<string> dictionaries,
			IReadOnlyDictionary<string, DictionaryBundle> bundles,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente,
			IReadOnlyList<string> tokensToFilter)
		{
			using var outw = new StreamWriter(findingsPath, append: false, new UTF8Encoding(false));
			using var trimw = new StreamWriter(trimmedFindingsPath, append: false, new UTF8Encoding(false));

			outw.WriteLine("# 30 — JS file spell-check (ScanScriptFilesInDownload) · FULL / debug");
			trimw.WriteLine("# 31 — JS file spell-check (ScanScriptFilesInDownload) · TRIMMED / triage");

			if (dictionaries == null || dictionaries.Count == 0 || !dictionaries.All(bundles.ContainsKey))
			{
				// Config validation halts the run before here on enabled+empty, so this is only reachable
				// if a named dictionary is not loaded. Note and no-op rather than crash.
				const string note = "# No usable ScriptBulkScanDictionaries configured (a named dictionary is not loaded) — nothing scanned.";
				outw.WriteLine(note);
				trimw.WriteLine(note);
				WriteUniqueWords(uniqueWordsPath, new HashSet<string>(StringComparer.Ordinal));
				WriteRoutingPreview(routingPreviewPath, new List<RoutingEntry>(), reachThreshold, 0, 0);
				return new Result(0, 0, 0, 0, 0, 0, System.Array.Empty<ScriptBundleFindings>());
			}

			string tauText = ScriptProseRatioTau.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
			// 656 — TokensToFilter is now wired into the file scan (it was off only because this path
			// inherited the audit-mode blob scanner; the file scan has no boilerplate pruning, and the
			// filter is whole-literal/single-token-safe, so it can never hide a misspelling). The bulk
			// PAGE audit pass stays raw by design.
			var siteTokensToFilter = new HashSet<string>(
				tokensToFilter ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
			string filterText = siteTokensToFilter.Count > 0 ? $"ON ({siteTokensToFilter.Count})" : "OFF";
			string header = $"# dictionaries: {string.Join(", ", dictionaries)} · gates ON · pruning OFF · TokensToFilter {filterText} · UTF-8 (replacement; undecodable literals skipped whole) · prose-ratio gate τ={tauText}";
			outw.WriteLine(header);
			outw.WriteLine("# A '# gated (...)' line marks a literal demoted by the prose-ratio gate (its findings are suppressed here and absent from log 31).");
			outw.WriteLine("# columns: word <TAB> file <TAB> context");
			outw.WriteLine();

			trimw.WriteLine(header);
			trimw.WriteLine($"# Trimmed view: only findings from literals that PASSED the prose-ratio gate. Full output incl. gated literals: {Path.GetFileName(findingsPath)}");
			trimw.WriteLine("# columns: word <TAB> file <TAB> context");
			trimw.WriteLine();

			var files = EnumerateJsFiles(downloadDirectory);

			var bundlesConcrete = bundles as Dictionary<string, DictionaryBundle>
				?? new Dictionary<string, DictionaryBundle>(bundles, StringComparer.OrdinalIgnoreCase);
			var checker = new ToolsSpellChecker(bundlesConcrete[dictionaries[0]], bundlesConcrete, prefixesToStrip, fugenelemente);
			var node = HtmlNode.CreateNode("<script></script>");

			int totalFindings = 0;
			int totalSkipped = 0;
			int tooLarge = 0;
			int totalGated = 0;
			int totalSuppressed = 0;

			// 646: flat unique-token view (log 32). Accumulated across every file from the SAME kept-
			// findings stream that feeds log 31, deduped exact (Ordinal — "Ausstatung" ≠ "ausstatung"),
			// written sorted at the end. No second scan; no influence on what is flagged.
			var uniqueWords = new HashSet<string>(StringComparer.Ordinal);

			// 647: reverse index (bundle → pages) so each file's findings can carry their REACH. Built
			// once from the saved page HTML. The routing decision below keys on reach alone — small
			// reach is locally fixable, large reach is a site-wide priority call. It deliberately does
			// NOT infer cross-copy DRIFT (same component diverging across page copies) from any
			// spelling signal: that is a separate similarity/diff problem and unsound to read from
			// spell findings (see ScriptPageIndex). 647 only PREVIEWS routing (log 33); the ticket
			// wiring consumes this in the next step.
			var pageIndex = ScriptPageIndex.BuildFromDownload(
				downloadDirectory, pagePattern, fileToUrl, out int pagesIndexed, out int pagesUnresolved);
			var routing = new List<RoutingEntry>();
			var bundleFindings = new List<ScriptBundleFindings>(); // 659: per-bundle emitted findings

			foreach (var file in files)
			{
				string rel = Relative(downloadDirectory, file);
				long size;
				try
				{
					size = new FileInfo(file).Length;
				}
				catch (Exception ex)
				{
					// Manifest stays parallel across both logs so either can prove the file was seen.
					WriteBoth(outw, trimw, $"# ── {rel} · UNREADABLE ({ex.Message}) ──");
					continue;
				}

				if (size > MaxFileBytes)
				{
					tooLarge++;
					WriteBoth(outw, trimw, $"# ── {rel} · {FormatSize(size)} · SKIPPED (exceeds {FormatSize(MaxFileBytes)} guard) ──");
					continue;
				}

				WriteBoth(outw, trimw, $"# ── {rel} · {FormatSize(size)} ──");

				string content;
				try
				{
					content = ReadJsFile(file);
				}
				catch (Exception ex)
				{
					WriteBoth(outw, trimw, $"#    (could not read: {ex.Message})");
					continue;
				}

				var fileHits = new List<ScriptWordHit>(); // 659: this bundle's emitted findings
				var outcome = BulkScriptScanner.ScanText(
					content,
					_ => rel,
					dictionaries,
					checker,
					node,
					outw,
					trimmedWriter: trimw,
					proseRatioGate: true,
					proseRatioTau: ScriptProseRatioTau,
					uniqueWords: uniqueWords,
					siteTokensToFilter: siteTokensToFilter,
					findingSink: fileHits);

				totalFindings += outcome.Findings;
				totalSkipped += outcome.Skipped;
				totalGated += outcome.GatedLiterals;
				totalSuppressed += outcome.SuppressedFindings;

				if (outcome.Skipped > 0)
				{
					WriteBoth(outw, trimw, $"#    ({outcome.Skipped} undecodable literal(s) skipped)");
				}

				// 647: only files that produced kept findings can become tickets, so only they need a
				// routing decision. Resolve the bundle's own URL → stable key → reach; reach decides
				// CLEAR (≤ threshold, per-page) vs BULK (> threshold, per-bundle). Captured for the
				// preview log; not yet emitted to the ledger.
				if (outcome.Findings > 0)
				{
					string url = SafeResolve(fileToUrl, Path.GetFileName(file));
					string key = ScriptUrlKey.StableKey(url);
					int reach = key.Length == 0 ? 0 : pageIndex.Reach(key);
					var pages = pageIndex.Pages(key);
					routing.Add(new RoutingEntry(rel, key, url, outcome.Findings, reach, pages));
					bundleFindings.Add(BuildBundleFindings(rel, key, url, reach, reachThreshold, pages, fileHits)); // 659
				}
			}

			string summary =
				$"# total: {totalFindings} kept finding(s) across {files.Count} file(s) · "
				+ $"{totalGated} literal(s) gated ({totalSuppressed} non-word occurrence(s) suppressed) · "
				+ $"{totalSkipped} undecodable literal(s) skipped · {tooLarge} file(s) skipped (too large)";

			outw.WriteLine();
			outw.WriteLine(summary);
			trimw.WriteLine();
			trimw.WriteLine(summary);

			WriteUniqueWords(uniqueWordsPath, uniqueWords);
			WriteRoutingPreview(routingPreviewPath, routing, reachThreshold, pagesIndexed, pagesUnresolved);

			return new Result(files.Count, totalFindings, totalSkipped, tooLarge, totalGated, totalSuppressed, bundleFindings);
		}

		// 646: log 32 — a plain, flat list of the unique kept-finding words, one per line, sorted
		// case-insensitively (mirrors log 11's unique view; no dictionary/lang column, which would be
		// noise on a 16-dictionary bulk scan). Dedup is exact (Ordinal); display order is culture-aware
		// case-insensitive so the list reads naturally for triage.
		private static void WriteUniqueWords(string path, HashSet<string> words)
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			var ordered = words.ToList();
			ordered.Sort(StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, ignoreCase: true));

			using var w = new StreamWriter(path, append: false, new UTF8Encoding(false));
			w.WriteLine("# 32 — JS file spell-check (ScanScriptFilesInDownload) · UNIQUE tokens");
			w.WriteLine($"# Flat, sorted, de-duplicated list of every distinct kept-finding word ({ordered.Count}). One per line, no context.");
			w.WriteLine();
			foreach (var word in ordered)
			{
				w.WriteLine(word);
			}
		}

		// 647: one bundle that produced findings, with its resolved URL, stable key, and reach.
		private readonly record struct RoutingEntry(
			string Rel, string Key, string Url, int Findings, int Reach, IReadOnlyList<string> Pages);

		// Resolve a disk filename to its source URL via the injected authoritative lookup, swallowing
		// any failure to an empty string so an unresolved file shows as a visible gap, not a crash.
		private static string SafeResolve(Func<string, string> fileToUrl, string filename)
		{
			try
			{
				return fileToUrl(filename) ?? string.Empty;
			}
			catch
			{
				return string.Empty;
			}
		}

		// 647: log 33 — routing PREVIEW. For every bundle that produced kept findings, shows the reach
		// (how many pages load it) and the resulting routing — CLEAR (reach ≤ threshold → would become
		// per-page findings) vs BULK (reach > threshold → would become one per-bundle finding). This is
		// the verification artifact: it makes the join and the reach visible BEFORE any of it is wired
		// into the ticket ledger, so the numbers can be checked against reality first. A bundle whose
		// URL did not resolve (reach 0, no key) is flagged as UNRESOLVED — a gap to investigate, never
		// silently dropped.
		private static void WriteRoutingPreview(
			string path, List<RoutingEntry> routing, int reachThreshold, int pagesIndexed, int pagesUnresolved)
		{
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			using var w = new StreamWriter(path, append: false, new UTF8Encoding(false));
			w.WriteLine("# 33 — JS file spell-check (ScanScriptFilesInDownload) · routing PREVIEW");
			w.WriteLine($"# Reverse index built from {pagesIndexed} page(s); {pagesUnresolved} page(s) could not be resolved to a URL.");
			w.WriteLine($"# reach ≤ {reachThreshold} → CLEAR (per-page findings); reach > {reachThreshold} → BULK (one per-bundle finding).");
			w.WriteLine("# Preview only — nothing here is written to the ticket ledger yet.");
			w.WriteLine();

			foreach (var e in routing.OrderByDescending(r => r.Reach).ThenBy(r => r.Rel, StringComparer.OrdinalIgnoreCase))
			{
				if (e.Key.Length == 0 || e.Reach == 0)
				{
					w.WriteLine($"UNRESOLVED  findings={e.Findings}  file={e.Rel}  (url='{e.Url}')");
					continue;
				}

				string route = e.Reach <= reachThreshold ? "CLEAR" : "BULK";
				w.WriteLine($"{route,-5}  reach={e.Reach}  findings={e.Findings}  {e.Key}");
				if (route == "CLEAR")
				{
					foreach (var page in e.Pages)
					{
						w.WriteLine($"        page: {page}");
					}
				}
			}

			if (routing.Count == 0)
			{
				w.WriteLine("# (no bundles produced findings)");
			}
		}

		// Write one line to both logs, keeping their per-file manifests identical so either log alone
		// proves a clean run actually scanned every file.
		private static void WriteBoth(TextWriter a, TextWriter b, string line)
		{
			a.WriteLine(line);
			b.WriteLine(line);
		}

		// .js under the crawl directory, EXCLUDING the base64assets/ subtree (those are stripped copies
		// the bloat analyzer may emit — scanning both would double-count). The "*.js" mask can also match
		// .json via legacy 8.3 short names, so the real extension is re-checked. Sorted for stable logs.
		internal static List<string> EnumerateJsFiles(string downloadDirectory)
		{
			if (string.IsNullOrEmpty(downloadDirectory) || !Directory.Exists(downloadDirectory))
			{
				return new List<string>();
			}

			return Directory.EnumerateFiles(downloadDirectory, "*.js", SearchOption.AllDirectories)
				.Where(p => Path.GetExtension(p).Equals(".js", StringComparison.OrdinalIgnoreCase))
				.Where(p => !PathHasSegment(p, "base64assets"))
				.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}

		internal static bool PathHasSegment(string path, string segment) =>
			path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				.Any(s => s.Equals(segment, StringComparison.OrdinalIgnoreCase));

		private static string Relative(string root, string file)
		{
			try
			{
				return Path.GetRelativePath(root, file);
			}
			catch
			{
				return Path.GetFileName(file);
			}
		}

		// [REVIEW] JS files carry no reliable charset signal and bundlers concatenate mixed-encoding
		// sources into one file. Decode the WHOLE file as UTF-8 with replacement: clean UTF-8 (the common
		// case) decodes fully; bytes from a non-UTF-8 segment (Windows-1252 / ANSI) become U+FFFD rather
		// than plausible mojibake letters, and ScanText skips any literal carrying U+FFFD. Encoding.UTF8
		// (the shared instance) uses replacement, not exception, fallback and never throws; the reader is
		// BOM-aware. If real per-segment detection is ever needed, change only this seam.
		internal static string ReadJsFile(string path) => File.ReadAllText(path, Encoding.UTF8);

		internal static string FormatSize(long bytes)
		{
			if (bytes >= 1024L * 1024)
			{
				return FormattableString.Invariant($"{bytes / (1024.0 * 1024.0):0.0} MB");
			}

			if (bytes >= 1024)
			{
				return FormattableString.Invariant($"{bytes / 1024.0:0.0} KB");
			}

			return FormattableString.Invariant($"{bytes} B");
		}
	}
}
