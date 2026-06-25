namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;

	/// <summary>
	/// 661 — turns the file scan's emitted per-bundle findings (<see cref="ScriptBundleFindings"/>) into
	/// the <see cref="WordTicket"/> substrate the spell triage already consumes, so JS-file typos flow
	/// through the SAME W→U loop, gone-is-gone reconciliation, and rendering as page typos — no parallel
	/// machinery. Routing decides where the ticket points: BULK (wide reach) → the stable, hash-stripped
	/// bundle key, so the ticket survives a re-deploy; CLEAR (≤ threshold) → each reached page. Reach is
	/// surfaced in the occurrence label so the operator sees the spread while triaging.
	///
	/// 662 — Lorem-ipsum PLACEHOLDER collapse. Placeholder text shipped to production is a real defect,
	/// but as individual Latin words it floods triage (one block = lorem, ipsum, consectetuer, adipiscing,
	/// aenean, … each its own row). The two markers <c>lorem</c> + <c>ipsum</c> co-occurring in a bundle
	/// are a near-certain tell, so when both are present the distinctive Latin filler is ABSORBED into a
	/// single placeholder finding per target (page or bundle) instead of being emitted word-by-word.
	/// Absorption, not suppression: the filler is folded into a finding we DO raise, and only when the
	/// markers co-occur — a stray Latin token on its own still surfaces. Real (non-Latin) typos in the
	/// same bundle are untouched and still ticketed individually.
	/// </summary>
	public static class ScriptSpellingTickets
	{
		// The sentinel word for the collapsed placeholder finding. Stable, so gone-is-gone retires it
		// once the placeholder is removed from the page (SPELLING|url|word key).
		internal const string PlaceholderWord = "Lorem ipsum (placeholder text)";

		// Distinctive lorem-ipsum vocabulary — tokens that actually FLAG and are unambiguously filler.
		// Deliberately excludes ultra-short ambiguous Latin (et, ut, sed, sit, do, cum, dis, mus) that
		// could collide with real tokens; those rarely flag anyway. Only ever consulted once the
		// lorem+ipsum markers have already identified the bundle as carrying placeholder text.
		private static readonly HashSet<string> LatinFiller = new(StringComparer.OrdinalIgnoreCase)
		{
			"lorem", "ipsum", "dolor", "amet", "consectetur", "consectetuer", "adipiscing", "adipisci",
			"elit", "eiusmod", "tempor", "incididunt", "labore", "dolore", "aliqua", "aenean", "commodo",
			"ligula", "eget", "massa", "sociis", "natoque", "penatibus", "magnis", "parturient", "montes",
			"nascetur", "ridiculus", "vestibulum", "tincidunt", "pellentesque", "vivamus", "fringilla",
			"pulvinar", "ullamcorper", "rhoncus", "venenatis", "malesuada", "tristique", "sollicitudin",
		};

		internal static bool IsLatinFiller(string word) => !string.IsNullOrEmpty(word) && LatinFiller.Contains(word);

		// 662 — the contiguous span of the lorem-ipsum block within an excerpt: from the START of the
		// first distinctive filler token to the END of the last one, INCLUSIVE of any non-filler words
		// and punctuation between them. Short ambiguous Latin (et, sit, cum, dis, …) is excluded from
		// LatinFiller, so a strict token-by-token run would fragment; but the placeholder reads as one
		// block, so it is reported as one span — used by the triage UI to light the whole block in the
		// non-typo (WCAG) scheme. ASCII letter runs only: the filler vocabulary is ASCII and the excerpt
		// may be raw script source. Returns (-1, 0) when the excerpt carries no filler token.
		internal static (int Start, int Length) LocateFillerRun(string? excerpt)
		{
			if (string.IsNullOrEmpty(excerpt))
			{
				return (-1, 0);
			}

			int firstStart = -1;
			int lastEnd = -1;
			int i = 0;
			while (i < excerpt.Length)
			{
				char c = excerpt[i];
				if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
				{
					int start = i;
					while (i < excerpt.Length
						&& ((excerpt[i] >= 'a' && excerpt[i] <= 'z') || (excerpt[i] >= 'A' && excerpt[i] <= 'Z')))
					{
						i++;
					}

					if (IsLatinFiller(excerpt[start..i]))
					{
						if (firstStart < 0)
						{
							firstStart = start;
						}

						lastEnd = i;
					}
				}
				else
				{
					i++;
				}
			}

			return firstStart >= 0 && lastEnd > firstStart ? (firstStart, lastEnd - firstStart) : (-1, 0);
		}

		// 663 — the lorem/ipsum markers often DON'T flag: common Latin roots (ipsum, dolor, amet, elit,
		// aenean, massa…) match a loaded dictionary, so they never reach the findings. Detection therefore
		// keys on the markers appearing in a finding's EXCERPT (the surrounding literal), not on them being
		// flagged words. Both markers present in one excerpt → placeholder. This is the real 100% signal:
		// "lorem ipsum" in the rendered text, regardless of which tokens the dictionaries happened to miss.
		internal static bool ExcerptIsPlaceholder(string? excerpt) =>
			!string.IsNullOrEmpty(excerpt)
			&& excerpt.IndexOf("lorem", StringComparison.OrdinalIgnoreCase) >= 0
			&& excerpt.IndexOf("ipsum", StringComparison.OrdinalIgnoreCase) >= 0;

		// Placeholder when any finding's excerpt carries the lorem+ipsum markers.
		internal static bool LooksLikePlaceholder(IEnumerable<ScriptWordHit> words)
		{
			foreach (var w in words)
			{
				if (ExcerptIsPlaceholder(w.Excerpt))
				{
					return true;
				}
			}
			return false;
		}

		public static IReadOnlyList<WordTicket> FromBundleFindings(IReadOnlyList<ScriptBundleFindings> bundles)
		{
			var tickets = new List<WordTicket>();
			if (bundles == null)
			{
				return tickets;
			}

			foreach (var b in bundles)
			{
				// Where the ticket points. BULK → one stable bundle key. CLEAR → each reached page; if no
				// page resolved, fall back to the stable key (else the raw bundle path) so it is never empty.
				string fallback = NonEmpty(b.StableKey, b.BundlePath);
				IReadOnlyList<string> urls = b.IsBulk
					? new[] { fallback }
					: (b.Pages.Count > 0 ? b.Pages : new[] { fallback });

				string label = b.IsBulk
					? $"{b.BundlePath} · reach {b.Reach} (bulk)"
					: $"{b.BundlePath} · reach {b.Reach}";

				bool placeholder = LooksLikePlaceholder(b.Words);
				string? placeholderExcerpt = null;

				foreach (var hit in b.Words)
				{
					if (placeholder && IsLatinFiller(hit.Word))
					{
						// Absorbed into the single placeholder finding — keep a representative excerpt.
						placeholderExcerpt ??= hit.Excerpt;
						continue;
					}

					var occurrences = new List<TicketOccurrence>
					{
						new TicketOccurrence(RunSource.Script, label, hit.Excerpt),
					};
					foreach (var url in urls)
					{
						tickets.Add(new WordTicket(hit.Word, url, string.Empty, occurrences));
					}
				}

				// One collapsed placeholder finding per target (page or bundle key).
				if (placeholder && placeholderExcerpt != null)
				{
					var occurrences = new List<TicketOccurrence>
					{
						new TicketOccurrence(RunSource.Script, $"{label} (placeholder text)", placeholderExcerpt),
					};
					foreach (var url in urls)
					{
						tickets.Add(new WordTicket(PlaceholderWord, url, string.Empty, occurrences));
					}
				}
			}

			return tickets;
		}

		private static string NonEmpty(string primary, string fallback) =>
			string.IsNullOrEmpty(primary) ? fallback : primary;
	}
}
