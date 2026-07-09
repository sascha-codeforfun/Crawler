namespace Crawler.SpellCheck
{
	using System;
	using Crawler.Lexicon;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;
	using HtmlAgilityPack;
	using Crawler.Boilerplate;

	/// <summary>
	/// Drives the new spell pipeline over a crawl, emitting the three ParallelStore views. This
	/// is the primary spell path and owns spell findings (see AnalysisPipeline.Step_RunSpellCheck).
	/// Inputs (dictionaries, language overrides, prefixes, fugenelemente, per-file cross-language
	/// words) are passed in from the spell step — sourced via the shared dictionary/config
	/// machinery.
	///
	/// This runner does NO dictionary loading and NO language-list invention: the caller supplies
	/// the already-loaded bundles and config-derived lists. The only language logic mirrored here
	/// is the per-file override match + HTML-lang fallback (kept identical to the legacy resolver);
	/// AllDictionaries mode is delegated to the same Tools entry point.
	/// </summary>
	public static class NewSpellEngineRunner
	{
		/// <summary>
		/// Per-file inputs the runner needs, mirroring what the legacy per-file checker receives.
		/// </summary>
		public sealed class FileInput
		{
			public string Filename { get; init; } = string.Empty;
		}

		/// <summary>
		/// Runs the new engine over the given files and writes the three views. Returns nothing the
		/// pipeline depends on — the views are the deliverable for the oracle diff.
		/// </summary>
		public static IReadOnlyList<WordTicket> Run(
			IEnumerable<FileInput> files,
			string downloadDirectory,
			string uniqueViewPath,
			string sourcesViewPath,
			string locatedViewPath,
			string wordTicketsDiagnosticPath,
			string suppressionSuggestionsPath,
			SpellCheckEngineConfig engineConfig,
			IReadOnlyDictionary<string, Bundle> dictionaryBundles,
			Func<string, HtmlDocument, string> resolveLanguage,
			IReadOnlyList<string> prefixesToStrip,
			IReadOnlyList<string> fugenelemente,
			Func<string, string> lookUpUrlForFile,
			int maxDegreeOfParallelism,
			WordCollisionMatcher? wordCollisions = null,
			Crawler.Suppressions.AnchorSplitSpellSuppression? anchorSplit = null,
			Crawler.Suppressions.UnwantedPatternSpellSuppression? unwantedPattern = null,
			Crawler.Suppressions.AdjacentAnchorSpellSuppression? adjacentAnchor = null)
		{
			var resolver = new BoilerplateResolver(engineConfig.BoilerplateGroups);

			// Materialize once so pages can be slotted by their original position; flattening the
			// slots in index order reproduces the sequential document order EXACTLY, so the emitted
			// views are byte-identical regardless of how many threads ran (only the slot a page
			// lands in is fixed; which thread filled it is irrelevant). This is what lets the run go
			// parallel without sacrificing the linear diffability of the logs.
			var fileList = files as IReadOnlyList<FileInput> ?? files.ToList();
			var perFileTickets = new IReadOnlyList<WordTicket>?[fileList.Count];

			// Slot 27: per-file script-source suppression inputs, slotted in document order like the
			// tickets above so the merged stream is deterministic regardless of thread scheduling.
			var perFileScriptInputs = new List<ScriptSuppressionInput>?[fileList.Count];

			// Read-only shared collaborators (verified safe for concurrent reads): the bundle lookup,
			// the boilerplate resolver (all state built in its ctor), globalIgnore/knownDefects
			// matchers, and SpellChecker.Check itself — the legacy engine already calls it from a
			// Parallel.ForEach. Bundles are hoisted out of the loop since they never change per page.
			var bundlesConcrete = dictionaryBundles as Dictionary<string, Bundle>
				?? new Dictionary<string, Bundle>(dictionaryBundles, StringComparer.OrdinalIgnoreCase);

			// Skip-set = the non-prose attributes (operator override if declared, else the built-in
			// class/id/style default) PLUS the site-specific technical attributes. Case-insensitive.
			IEnumerable<string> nonProse =
				(IEnumerable<string>?)engineConfig.GlobalNonProseHtmlAttributesThatWillBeIgnored
				?? DomTraverser.DefaultNonProseAttributes;
			var skipAttributeNames = new HashSet<string>(nonProse, StringComparer.OrdinalIgnoreCase);
			// Boolean attributes (override or built-in default) — their values are non-prose by
			// definition; union into the same skip lookup. Separate concern, separate override.
			IEnumerable<string> boolAttrs =
				(IEnumerable<string>?)engineConfig.GlobalBooleanHtmlAttributesThatWillBeIgnored
				?? DomTraverser.DefaultBooleanAttributes;
			foreach (var name in boolAttrs)
			{
				skipAttributeNames.Add(name);
			}

			foreach (var name in engineConfig.GlobalAttributesToIgnore ?? Enumerable.Empty<string>())
			{
				skipAttributeNames.Add(name);
			}

			// Meta allowlist: declared list (even empty) REPLACES the default; absent (null) → built-in
			// default {description, keywords}. "Declare it, declare it right" — [] means check no meta.
			IReadOnlySet<string> metaContentNames = engineConfig.MetaContentNamesToSpellCheck is { } declared
				? new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase)
				: DomTraverser.DefaultMetaContentNames;

			// HTML element types whose subtree is removed before checking (svg/object/input/select/…).
			// Plain config list (absent = none), case-insensitive.
			var htmlTagsToIgnore = new HashSet<string>(
				engineConfig.GlobalHtmlTagsToIgnore ?? Enumerable.Empty<string>(),
				StringComparer.OrdinalIgnoreCase);

			// Site-specific non-prose script literals to filter (config: SpellCheckJavaScript.
			// TokensToFilter). Whole-literal, case-insensitive; only consulted for Script runs.
			var scriptTokensToFilter = new HashSet<string>(
				engineConfig.SpellCheckJavaScript.TokensToFilter ?? Enumerable.Empty<string>(),
				StringComparer.OrdinalIgnoreCase);

			// Optional script-only fallback dictionary (config: SpellCheckJavaScript.ScriptFallbackDictionary),
			// named by its loaded dictionary key (e.g. "en"). Honoured only when it resolves to a loaded
			// bundle; an empty or unknown value is ignored (no fallback). Passed to RunChecker, which
			// applies it to Script runs only.
			string? scriptFallbackLanguage = engineConfig.SpellCheckJavaScript.ScriptFallbackDictionary;
			if (string.IsNullOrWhiteSpace(scriptFallbackLanguage) || !dictionaryBundles.ContainsKey(scriptFallbackLanguage))
			{
				scriptFallbackLanguage = null;
			}

			// Known chrome/template language defects to mute (declared per source-path + text).
			var knownDefects = new KnownDefectMatcher(engineConfig.KnownChromeLanguageDefects);

			// Global technical-ignore: xpath selectors for non-content plumbing (tracking pixels etc.)
			// pruned on EVERY page including entry pages. Built once as an xpath-only BoilerplateMatcher
			// (reusing its node-or-ancestor xpath resolution) — orthogonal to per-group boilerplate.
			BoilerplateMatcher? globalIgnore = engineConfig.GlobalXpathToIgnore is { Count: > 0 } xpaths
				? new BoilerplateMatcher(xpaths.Select(x => new BoilerplateSelector("xpath", x)))
				: null;

			int total = 0, unreadable = 0, noBundle = 0, pagesWithFindings = 0;
			Logger.LogInfo(
				$"New spell engine: starting validation pass (boilerplate groups: {engineConfig.BoilerplateGroups.Count}).");

			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount,
			};

			Parallel.For(0, fileList.Count, parallelOptions, i =>
			{
				var file = fileList[i];
				Interlocked.Increment(ref total);
				string url = lookUpUrlForFile(file.Filename) ?? file.Filename;

				byte[] bytes;
				try
				{
					bytes = File.ReadAllBytes(Path.Combine(downloadDirectory, file.Filename));
				}
				catch
				{
					Interlocked.Increment(ref unreadable);
					return; // unreadable file — skip; legacy path logs its own errors
				}

				var doc = DomTraverser.Parse(bytes);
				string branchLanguage = resolveLanguage(file.Filename, doc);

				// Resolve the language SET: a PageLanguageOverrides prefix match (longest wins) yields an
				// explicit multi-language set, else the single branch language. Bundles for every override
				// language were verified at startup (PageLanguageResolver.ValidateBundles), so here we only
				// guard the branch-language single case (legacy resolver may name a language with no bundle).
				IReadOnlyList<string> languages =
					PageLanguageResolver.Resolve(url, branchLanguage, engineConfig.PageLanguageOverrides);

				if (languages.Any(lang => !dictionaryBundles.ContainsKey(lang)))
				{
					Interlocked.Increment(ref noBundle);
					return; // no bundle for (at least) the branch language
				}

				var (matcher, isCheckPage) = resolver.Resolve(url);

				// Per-element lang: when NO override governs this URL, each run is checked against the
				// dictionary for the language declared closest to it (nearest <… lang> ancestor, the
				// page language as floor) — so a <div lang="ar"> island is checked as Arabic, not as
				// the page's English. An override stays ground truth for the whole page (today's
				// behaviour). An island whose declared language has no loaded bundle falls back to the
				// page set, reproducing today's behaviour for those words (they surface as normal
				// findings, signalling the operator to load that dictionary). No run is ever checked
				// against a broader set than today; dictionary-backed islands get a narrower, correct one.
				bool overrideActive = Crawler.Html.PageLanguageSet.HasOverride(url, engineConfig.PageLanguageOverrides);
				IReadOnlyList<string> RunLanguagesFor(TextRun run)
				{
					if (overrideActive)
					{
						return languages;
					}

					string island = Crawler.Html.Language.NearestElementLanguage(run.Node, branchLanguage);
					return dictionaryBundles.ContainsKey(island) ? new[] { island } : languages;
				}

				// Primary bundle follows the language argument inside the checker, so any of the set's
				// languages is checkable from this one instance (see ToolsSpellChecker.Check). A fresh
				// checker per page keeps all per-page state thread-local.
				var checker = new ToolsSpellChecker(
					bundlesConcrete[languages[0]],
					bundlesConcrete,
					prefixesToStrip,
					fugenelemente);

				// Keep each finding paired with the run it came from: the aggregator below needs only
				// the findings (the sequence is identical to before, so log 13 is byte-unchanged), while
				// slot 27 needs the run's RawText — the whole decoded literal, the TokensToFilter unit —
				// for Script findings. Pairing here avoids carrying the literal through the rest of the
				// pipeline on the finding.
				var pairs = DomTraverser
					.Traverse(doc, matcher, isCheckPage, skipAttributeNames, engineConfig.SpellCheckJavaScript.Enabled, metaContentNames, globalIgnore, htmlTagsToIgnore)
					.SelectMany(run => RunChecker.Check(run, RunLanguagesFor(run), checker.Check, knownDefects, engineConfig.HeuristicNonProseDataAttributeSuppression, wordCollisions?.WordsForFile(file.Filename), scriptTokensToFilter, scriptFallbackLanguage, anchorSplitTails: anchorSplit?.ForFile(file.Filename), unwantedPatternWords: unwantedPattern?.WordsForFile(file.Filename), adjacentAnchorJoins: adjacentAnchor?.ForFile(file.Filename))
						.Select(finding => (run, finding)))
					.ToList();

				var findings = pairs.Select(p => p.finding).ToList();

				if (findings.Count == 0)
				{
					return;
				}

				// Slot 27: project the Script-source findings into suppression inputs (only when JS
				// checking is on — there are no Script findings otherwise).
				if (engineConfig.SpellCheckJavaScript.Enabled)
				{
					var scriptInputs = pairs
						.Where(p => p.run.Source == RunSource.Script)
						.Select(p => new ScriptSuppressionInput(url, p.finding.Word, p.run.RawText, p.finding.ExcerptText ?? string.Empty))
						.ToList();
					if (scriptInputs.Count > 0)
					{
						perFileScriptInputs[i] = scriptInputs;
					}
				}

				Interlocked.Increment(ref pagesWithFindings);
				perFileTickets[i] = FindingAggregator.Aggregate(url, findings, ExcerptBuilder.Build);
			});

			// Flatten the per-page slots in document order — identical to the sequential walk.
			var allTickets = new List<WordTicket>();
			foreach (var slot in perFileTickets)
			{
				if (slot != null)
				{
					allTickets.AddRange(slot);
				}
			}

			Logger.LogInfo(
				$"New spell engine: {total} file(s) processed, {pagesWithFindings} with findings, "
				+ $"{allTickets.Count} ticket(s). Skipped: {unreadable} unreadable, {noBundle} no-language-bundle.");

			WriteViews(uniqueViewPath, sourcesViewPath, locatedViewPath, wordTicketsDiagnosticPath, allTickets);

			// Slot 27: merge the per-file script suppression inputs in document order and write the
			// suggestion log — only when JS checking is on (else nothing is script-sourced and no file
			// is produced).
			if (engineConfig.SpellCheckJavaScript.Enabled)
			{
				var allScriptInputs = new List<ScriptSuppressionInput>();
				foreach (var slot in perFileScriptInputs)
				{
					if (slot != null)
					{
						allScriptInputs.AddRange(slot);
					}
				}

				// Slot 27 A/B signal. The checker surfaces no correction candidates (its "suggestions"
				// field carries the failed language, not near-words), so the real-word probe is done
				// here against the dictionaries the pipeline already loaded: a single-token literal
				// resembles a real word when its CAPITALIZED form is accepted by some bundle (e.g.
				// "aktien" → "Aktien"). Consulted only for single-token literals, on this one thread.
				Func<string, bool> resemblesRealWord = word =>
					!string.IsNullOrEmpty(word)
					&& Bundle.CheckAny(char.ToUpperInvariant(word[0]) + word.Substring(1), dictionaryBundles.Values);

				WriteView(suppressionSuggestionsPath, SuppressionSuggestionAnalyzer.Compose(allScriptInputs, resemblesRealWord));
			}

			return allTickets;
		}

		// Fixed output filenames, written into a hardcoded "logs" subfolder of the snapshot root.
		// Output placement is not configurable: a fixed location inside the snapshot keeps the new
		// views in a predictable home (separate from the legacy 11/12 in the snapshot root) and
		// removes any chance of misdirecting them into a pruned directory.
		private static void WriteViews(
			string uniqueViewPath,
			string sourcesViewPath,
			string locatedViewPath,
			string wordTicketsDiagnosticPath,
			IReadOnlyList<WordTicket> tickets)
		{
			// Written directly to the numbered run-folder logs (11/12/13/14). No 'logs'
			// subfolder is created — the views live alongside the other numbered logs.
			// 14 is the verbatim word-tickets diagnostic: what the engine flagged and where,
			// written every harvest run regardless of triage.
			WriteView(uniqueViewPath, ParallelStoreWriter.UniqueView(tickets));
			WriteView(sourcesViewPath, ParallelStoreWriter.SourcesView(tickets));
			WriteView(locatedViewPath, ParallelStoreWriter.LocatedView(tickets));
			WriteView(wordTicketsDiagnosticPath, WordTicketsDiagnostic.Compose(tickets));
		}

		private static void WriteView(string path, string content)
		{
			// Split the pre-composed view into lines and write via the shared lock-aware helper —
			// same write path, encoding (UTF-8 BOM) and line endings (CRLF via Environment.NewLine)
			// as the numbered logs (11/12) these views are diffed against, and the codebase's
			// standard retry-on-lock (so a held handle doesn't fail the write or the next sweep).
			string[] lines = content.Length == 0
				? []
				: content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

			string fileName = Path.GetFileName(path);
			bool ok = FileIo.WriteAllLinesWithRetry(path, lines, fileName);
			if (ok)
			{
				Logger.LogInfo($"New spell engine: wrote {lines.Length} line(s) to {path}.");
			}
		}
	}
}
