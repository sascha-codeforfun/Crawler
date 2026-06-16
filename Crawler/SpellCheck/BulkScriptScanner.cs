namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Text;
	using System.Text.RegularExpressions;
	using HtmlAgilityPack;

	/// <summary>
	/// 659 — one kept finding emitted out of a scan: the flagged word and its surrounding excerpt.
	/// Collected per scanned text (one bundle) via the optional findingSink so callers can route the
	/// findings (file-scan → triage), distinct from <c>uniqueWords</c> which is the flat log-32 set.
	/// Namespace-level so it is shared across the scanner classes and tests.
	/// </summary>
	public readonly record struct ScriptWordHit(string Word, string Excerpt);

	/// <summary>
	/// Delivery 633 — <c>SpellCheckJavaScript.BulkScanPageScript</c>: a SEPARATE, opt-in diagnostic
	/// pass, deliberately isolated from the proven per-page spell engine so it can never perturb the
	/// live path (and so the feature can be switched off wholesale). It runs in two stages:
	///
	///   1. HARVEST — walk every downloaded HTML file, take each INLINE &lt;script&gt; body (no src),
	///      and append it to one blob (log 28), each block preceded by a provenance header written as a
	///      JS <c>//</c> line comment: "// ===== {url} · script #N =====". Inline bodies only this pass;
	///      on* / attribute JS is not harvested.
	///
	///   2. SCAN — read the blob back OFF DISK and run the lexer over the WHOLE stream in one pass
	///      (this is the aggregation / big-file test, and a rehearsal of a future external-.js scan),
	///      checking each literal through <see cref="RunChecker"/> with the proven structural gates ON
	///      and NO config-driven suppression (no boilerplate pruning — there is no DOM here — and no
	///      TokensToFilter), so config blind spots surface. Findings go to log 29, each tagged with the
	///      page from its preceding header.
	///
	/// The headers cannot themselves produce findings on two independent grounds: the lexer skips
	/// <c>//</c> comments entirely (so a header never enters literal extraction), and the source label is
	/// stripped of quote characters (so even a mis-lexed header carries no string literal).
	///
	/// 643 — <see cref="ScanText"/> gained an OPTIONAL prose-ratio gate and an OPTIONAL second
	/// (trimmed) writer, used by the external-.js file scan (<see cref="JsFileScanner"/>) to split a
	/// firehose log into a full debug log + a triage log. The inline-blob scan (log 29) leaves both
	/// off, so its behaviour is byte-for-byte unchanged from 642.
	/// </summary>
	public static class BulkScriptScanner
	{
		private const int ContextRadius = 80;

		// The gated literal's body is collapsed and capped for the one-line audit note in the full
		// (debug) log. Long enough to recognise the literal, short enough to keep the note a line.
		private const int GateNoteLiteralMax = 120;

		public readonly record struct Result(int Blocks, int Findings);

		/// <summary>
		/// Outcome of one <see cref="ScanText"/> pass. <c>Findings</c> is the count of KEPT findings
		/// (those written to the trimmed log when the gate runs; all findings when it does not).
		/// <c>GatedLiterals</c> and <c>SuppressedFindings</c> report the gate's work: how many literals
		/// were demoted, and how many union-miss occurrences inside them were suppressed.
		/// </summary>
		public readonly record struct ScanOutcome(int Findings, int Skipped, int GatedLiterals, int SuppressedFindings);

		public static Result Run(
			string downloadDirectory,
			string filePattern,
			string blobPath,
			string findingsPath,
			IReadOnlyList<string> dictionaries,
			IReadOnlyDictionary<string, DictionaryBundle> bundles,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente,
			Func<string, string> fileToUrl)
		{
			int blocks = HarvestBlob(downloadDirectory, filePattern, blobPath, fileToUrl);
			int findings = ScanBlob(blobPath, findingsPath, dictionaries, bundles, prefixesToStrip, fugenelemente);
			return new Result(blocks, findings);
		}

		// ── Stage 1: harvest inline <script> bodies into the blob (log 28) ──
		private static int HarvestBlob(
			string downloadDirectory,
			string filePattern,
			string blobPath,
			Func<string, string> fileToUrl)
		{
			int blocks = 0;
			using var writer = new StreamWriter(blobPath, append: false, new UTF8Encoding(false));
			writer.WriteLine("// 28 — bulk inline-<script> blob (BulkScanPageScript). Headers are // comments, inert when re-scanned.");
			writer.WriteLine();

			foreach (var file in Directory.GetFiles(downloadDirectory, filePattern).OrderBy(f => f, StringComparer.Ordinal))
			{
				HtmlDocument doc;
				try
				{
					doc = DomTraverser.Parse(File.ReadAllBytes(file));
				}
				catch
				{
					continue; // unparseable file — skip, never abort the pass
				}

				var scripts = doc.DocumentNode.SelectNodes("//script");
				if (scripts == null)
				{
					continue;
				}

				string filename = Path.GetFileName(file);
				string resolved = fileToUrl(filename);
				string source = SanitizeForComment(string.IsNullOrEmpty(resolved) ? filename : resolved);

				int n = 0;
				foreach (var s in scripts)
				{
					// Inline only: an external reference (src=…) has no body to harvest here.
					if (!string.IsNullOrEmpty(s.GetAttributeValue("src", string.Empty)))
					{
						continue;
					}

					string body = s.InnerText;
					if (string.IsNullOrWhiteSpace(body))
					{
						continue;
					}

					n++;
					blocks++;
					writer.WriteLine($"// ===== {source} · script #{n} =====");
					writer.WriteLine(body);
					writer.WriteLine();
				}
			}

			return blocks;
		}

		// The provenance label rides inside a // comment, so it is already inert. Stripping quotes and
		// newlines is the belt-and-suspenders guard: even if a header were ever mis-lexed, it carries no
		// string literal and spans no extra lines, so it can contribute no finding.
		private static string SanitizeForComment(string s) => s
			.Replace('\n', ' ')
			.Replace('\r', ' ')
			.Replace("\"", string.Empty)
			.Replace("'", string.Empty)
			.Replace("`", string.Empty);

		// ── Stage 2: read the blob off disk and scan it as ONE stream (log 29) ──
		private static int ScanBlob(
			string blobPath,
			string findingsPath,
			IReadOnlyList<string> dictionaries,
			IReadOnlyDictionary<string, DictionaryBundle> bundles,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente)
		{
			using var outw = new StreamWriter(findingsPath, append: false, new UTF8Encoding(false));
			outw.WriteLine("# 29 — bulk page-script scan findings (BulkScanPageScript)");

			if (dictionaries == null || dictionaries.Count == 0 || !dictionaries.All(bundles.ContainsKey))
			{
				outw.WriteLine("# No usable ScriptBulkScanDictionaries configured (empty, or a named dictionary is not loaded) — nothing scanned.");
				return 0;
			}

			outw.WriteLine($"# Source: {Path.GetFileName(blobPath)} · dictionaries: {string.Join(", ", dictionaries)} · gates ON · boilerplate pruning OFF · TokensToFilter OFF");
			outw.WriteLine("# columns: word <TAB> page <TAB> context");
			outw.WriteLine();

			string blob = File.ReadAllText(blobPath);
			var headers = HeaderOffsets(blob);

			var bundlesConcrete = bundles as Dictionary<string, DictionaryBundle>
				?? new Dictionary<string, DictionaryBundle>(bundles, StringComparer.OrdinalIgnoreCase);
			var checker = new ToolsSpellChecker(bundlesConcrete[dictionaries[0]], bundlesConcrete, prefixesToStrip, fugenelemente);
			var node = HtmlNode.CreateNode("<script></script>");

			// ONE lexer pass over the entire blob — this is the aggregation / big-file test. Provenance
			// is resolved per literal from the injected headers; the inline blob is clean UTF-8, so the
			// undecodable-skip below never fires here (it exists for the external .js-file path). The
			// prose-ratio gate stays OFF here: the inline blob mirrors the live per-page run, which is
			// left untouched in 643, so gating it would diverge the two on purpose-built parity.
			var outcome = ScanText(
				blob,
				offset => SourceForOffset(headers, offset),
				dictionaries,
				checker,
				node,
				outw);

			if (outcome.Findings == 0)
			{
				outw.WriteLine("  (no findings)");
			}

			return outcome.Findings;
		}

		/// <summary>
		/// The shared scan core: ONE lexer pass over <paramref name="text"/>, each string literal gated
		/// through <see cref="RunChecker"/> (ClassifyScriptLiteral + markup + property-access ON; no
		/// knownDefects, no TokensToFilter, no fallback — raw, so config blind spots surface) and written
		/// as "  word &lt;TAB&gt; source &lt;TAB&gt; context". Provenance is supplied per literal by
		/// <paramref name="sourceFor"/> (offset → label): the inline blob looks it up from headers, the
		/// per-file scan returns a constant filename. Returns a <see cref="ScanOutcome"/>. A literal
		/// containing U+FFFD — the replacement char produced when bytes did not decode as UTF-8, e.g. a
		/// Windows-1252 or ANSI segment concatenated into a bundle — is skipped WHOLE and counted, never
		/// tokenized: its decoding is untrustworthy, so guessing would only mint mojibake false positives.
		///
		/// 643 — when <paramref name="proseRatioGate"/> is true (the external-.js file scan), each
		/// literal additionally passes the prose-ratio gate (<paramref name="proseRatioTau"/>): a literal
		/// whose word tokens are mostly non-words is demoted whole and emits no findings, getting instead
		/// a one-line audit note ("# gated (ratio …, miss/total) · source · literal") in
		/// <paramref name="outw"/> so the full log stays the debug source of truth. Surviving findings are
		/// written to BOTH <paramref name="outw"/> and the optional <paramref name="trimmedWriter"/> (the
		/// triage log). With the gate off and no trimmed writer, this is the 642 behaviour exactly.
		/// </summary>
		internal static ScanOutcome ScanText(
			string text,
			Func<int, string> sourceFor,
			IReadOnlyList<string> dictionaries,
			ToolsSpellChecker checker,
			HtmlNode node,
			TextWriter outw,
			TextWriter? trimmedWriter = null,
			bool proseRatioGate = false,
			double proseRatioTau = 0.0,
			ICollection<string>? uniqueWords = null,
			IReadOnlySet<string>? siteTokensToFilter = null,
			ICollection<ScriptWordHit>? findingSink = null)
		{
			int count = 0;
			int skipped = 0;
			int gatedLiterals = 0;
			int suppressedFindings = 0;

			foreach (var literal in JsStringLiteralExtractor.Extract(text))
			{
				if (literal.Text.IndexOf('\uFFFD') >= 0)
				{
					skipped++;
					continue;
				}

				var run = new TextRun(node, RunSource.Script, "bulk-script", literal.Text)
				{
					ScriptContext = ContextWindow(text, literal.RawStart, literal.RawLength),
				};

				string source = sourceFor(literal.RawStart);

				if (proseRatioGate)
				{
					var gate = new RunChecker.ScriptGateInfo();
					var findings = RunChecker.Check(
						run,
						dictionaries,
						checker.Check,
						scriptTokensToFilter: siteTokensToFilter,
						scriptProseRatioGate: true,
						scriptProseRatioTau: proseRatioTau,
						scriptGateInfo: gate).ToList();

					if (gate.Gated)
					{
						// Demoted literal — full log gets the audit note (the trimmed log never does),
						// so log 30 remains the debug source from which log 31 is the distilled view.
						gatedLiterals++;
						suppressedFindings += gate.MissTokens;
						outw.WriteLine($"# gated (ratio {gate.Ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}, {gate.MissTokens}/{gate.TotalTokens}) · {source} · {TrimLiteral(literal.Text)}");
						continue;
					}

					foreach (var finding in findings)
					{
						count++;
						uniqueWords?.Add(finding.Word); // 646: feeds the flat unique-token view (log 32)
						string ctx = (finding.ExcerptText ?? string.Empty).Replace('\n', ' ').Replace('\t', ' ').Trim();
						findingSink?.Add(new ScriptWordHit(finding.Word, ctx)); // 659: per-bundle finding for routing
						string line = $"  {finding.Word}\t{source}\t{ctx}";
						outw.WriteLine(line);
						trimmedWriter?.WriteLine(line);
					}
				}
				else
				{
					foreach (var finding in RunChecker.Check(run, dictionaries, checker.Check))
					{
						count++;
						string ctx = (finding.ExcerptText ?? string.Empty).Replace('\n', ' ').Replace('\t', ' ').Trim();
						outw.WriteLine($"  {finding.Word}\t{source}\t{ctx}");
					}
				}
			}

			return new ScanOutcome(count, skipped, gatedLiterals, suppressedFindings);
		}

		// One-line form of a gated literal for the audit note: whitespace collapsed, capped with an
		// ellipsis. Tabs/newlines removed so the note can never break the TSV-shaped log.
		internal static string TrimLiteral(string s)
		{
			string one = Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();
			return one.Length <= GateNoteLiteralMax ? one : one.Substring(0, GateNoteLiteralMax) + "…";
		}

		// Offsets of every provenance header ("// ===== {source} · script #N ====="), ascending, with
		// the parsed source label. Used to attribute each literal to the page it came from.
		internal static List<(int Offset, string Source)> HeaderOffsets(string blob)
		{
			const string prefix = "// ===== ";
			const string marker = " · script";
			var list = new List<(int, string)>();

			int i = 0;
			while (i < blob.Length)
			{
				int nl = blob.IndexOf('\n', i);
				int lineEnd = nl < 0 ? blob.Length : nl;
				string line = blob.Substring(i, lineEnd - i).TrimEnd('\r');

				if (line.StartsWith(prefix, StringComparison.Ordinal))
				{
					string src = line.Substring(prefix.Length);
					int m = src.IndexOf(marker, StringComparison.Ordinal);
					if (m >= 0)
					{
						src = src.Substring(0, m);
					}

					list.Add((i, src.Trim()));
				}

				if (nl < 0)
				{
					break;
				}

				i = nl + 1;
			}

			return list;
		}

		internal static string SourceForOffset(List<(int Offset, string Source)> headers, int offset)
		{
			string source = "(unknown)";
			foreach (var h in headers)
			{
				if (h.Offset <= offset)
				{
					source = h.Source;
				}
				else
				{
					break; // headers are ascending — no later header precedes this offset
				}
			}

			return source;
		}

		// A single-line window of <= ContextRadius chars either side of the literal, whitespace-collapsed.
		internal static string ContextWindow(string blob, int start, int length)
		{
			if (blob.Length == 0)
			{
				return string.Empty;
			}

			if (start < 0)
			{
				start = 0;
			}

			if (start > blob.Length)
			{
				start = blob.Length;
			}

			int end = Math.Min(blob.Length, start + Math.Max(0, length));

			int left = Math.Max(0, start - ContextRadius);
			for (int k = start - 1; k >= left; k--)
			{
				if (blob[k] == '\n')
				{
					left = k + 1;
					break;
				}
			}

			int right = Math.Min(blob.Length, end + ContextRadius);
			for (int k = end; k < right; k++)
			{
				if (blob[k] == '\n')
				{
					right = k;
					break;
				}
			}

			return Regex.Replace(blob.Substring(left, right - left), @"\s+", " ").Trim();
		}
	}
}
