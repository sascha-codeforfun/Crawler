namespace Crawler.Boilerplate
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Text;
	using System.Threading.Tasks;
	using Crawler;
	using Crawler.Html;

	/// <summary>
	/// Produces the shared PRUNED HTML tree: each downloaded page with its operator-declared
	/// boilerplate removed, EXCEPT on a group's check pages where the boilerplate is KEPT WHOLE
	/// (so every chrome defect is still caught once, there). Boilerplate (nav/header/footer/…)
	/// is the bulk of the markup, so pruning it once on disk — instead of each consumer re-walking
	/// it on ~every page — gives a much lighter DOM and removes per-page duplication of chrome
	/// findings. The pruned tree is the single input both ContentQuality and the spell engine are
	/// meant to read (wired up in later legs).
	///
	/// Governance is delegated WHOLLY to <see cref="BoilerplateResolver"/> (longest-PathPrefix
	/// wins; check page = keep). This class only (a) converts the resolver's typed selectors to
	/// the xpath removal idiom and (b) calls the existing <see cref="MarkupFile.RemoveByXPath"/>
	/// primitive (read → parse → remove → write utf8-no-bom) — no new IO/parse code, no edits to
	/// Tools.
	///
	/// class → xpath conversion mirrors <see cref="BoilerplateMatcher"/> EXACTLY so the on-disk
	/// prune matches the matcher's in-memory prune: WHOLE-TOKEN, CASE-SENSITIVE (ordinal),
	/// AND-subset for multi-token values — via
	/// contains(concat(' ',normalize-space(@class),' '),' tok ') ANDed once per token. xpath
	/// selectors pass through verbatim. Unknown selector types contribute nothing (the matcher
	/// is silent on them too). class tokens are emitted unescaped, exactly as the matcher reads
	/// them (CSS identifiers carry no xpath-quoting concerns in practice).
	/// </summary>
	public static class BoilerplateSimplifier
	{
		/// <summary>
		/// Prunes every file matching <paramref name="filePattern"/> in
		/// <paramref name="sourceDirectory"/> into <paramref name="destinationDirectory"/>.
		/// File→URL mapping (for group resolution) is supplied by <paramref name="lookUpUrlForFile"/>.
		/// </summary>
		public static void Run(
			string sourceDirectory,
			string destinationDirectory,
			BoilerplateResolver resolver,
			Func<string, string> lookUpUrlForFile,
			string filePattern,
			int maxDegreeOfParallelism)
		{
			var files = Directory.GetFiles(sourceDirectory, filePattern);
			var parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = maxDegreeOfParallelism > 0
					? maxDegreeOfParallelism
					: Environment.ProcessorCount,
			};

			Parallel.ForEach(files, parallelOptions, file =>
			{
				string url = lookUpUrlForFile(Path.GetFileName(file)) ?? Path.GetFileName(file);
				var (selectors, isCheckPage) = resolver.ResolveSelectors(url);

				// Check page → keep boilerplate (empty removal list). Otherwise convert the
				// governing group's selectors. No governing group → empty selectors → nothing
				// removed (fail-loud: undeclared structure stays visible to the consumers).
				List<string> removals = isCheckPage
					? new List<string>()
					: ToXPathRemovals(selectors);

				// Even with an empty removal list this re-serialises the page through the same
				// reader/writer, so the whole pruned tree is uniformly formatted (utf8-no-bom).
				MarkupFile.RemoveByXPath(file, removals, destinationDirectory);
			});
		}

		/// <summary>
		/// Convert typed selectors to an xpath removal list mirroring <see cref="BoilerplateMatcher"/>:
		/// class → whole-token / AND-subset / case-sensitive contains idiom; xpath → passed through.
		/// Unknown/empty selectors contribute nothing.
		/// </summary>
		internal static List<string> ToXPathRemovals(IReadOnlyList<BoilerplateSelectorConfig> selectors)
		{
			var result = new List<string>();
			if (selectors == null)
			{
				return result;
			}

			foreach (var sel in selectors)
			{
				if (sel == null || string.IsNullOrWhiteSpace(sel.Value))
				{
					continue;
				}

				switch (sel.Type?.Trim().ToLowerInvariant())
				{
					case "class":
						var tokens = sel.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
						if (tokens.Length == 0)
						{
							break;
						}

						// Subset semantics: an element matches iff EVERY token is present, so AND
						// one whole-token contains() predicate per token onto a single element step.
						var sb = new StringBuilder("//*");
						foreach (var token in tokens)
						{
							sb.Append("[contains(concat(' ',normalize-space(@class),' '),' ")
								.Append(token)
								.Append(" ')]");
						}

						result.Add(sb.ToString());
						break;

					case "xpath":
						result.Add(sel.Value.Trim());
						break;

					// Unknown type: no removal (matches BoilerplateMatcher's silent skip).
				}
			}

			return result;
		}
	}
}
