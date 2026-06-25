namespace Crawler.Quality
{
	internal static class UnwantedPatterns
	{
		// Max characters an OPEN envelope's region may span when no closing delimiter
		// bounds it — stops a missing-closer placeholder from fusing with distant text
		// on the same line. The whitespace/'<' edge below almost always fires first.
		private const int EnvelopeRegionMaxChars = 120;

		/// <summary>
		/// Right edge of an OPEN envelope's region — used when the closing delimiter is
		/// absent, so there is nothing to bound "inside the placeholder" with. Returns the
		/// first whitespace or '&lt;' at or after <paramref name="start"/>, capped at
		/// <see cref="EnvelopeRegionMaxChars"/>. The token-run boundary is what makes the
		/// rule generic: a leaked CMS variable, URL slug, or any unbroken run reads as one
		/// region, while a space or a tag boundary (e.g. <c>%(institut.name)&lt;/title&gt;</c>)
		/// ends it so following content is never swallowed.
		/// </summary>
		private static int FindEnvelopeRegionEnd(string html, int start)
		{
			int limit = Math.Min(html.Length, start + EnvelopeRegionMaxChars);
			for (int i = start; i < limit; i++)
			{
				char c = html[i];
				if (char.IsWhiteSpace(c) || c == '<')
				{
					return i;
				}
			}

			return limit;
		}

		internal static IEnumerable<QualityIssue> Check(
			string filename,
			string html,
			IReadOnlyList<ContentUnwantedPattern> patterns,
			ContentQualityConfig config)
		{
			// Pass 1 — collect every atomic match (the rows we WOULD emit pre-coalescing),
			// each tagged with its source set and char position(s). A grouped set yields one
			// atom (first occurrence per pattern); an ungrouped set yields one atom per
			// occurrence. Nothing is emitted yet — coalescing needs the whole picture first.
			var atoms = new List<(ContentUnwantedPattern Set, bool Grouped, List<(string Pattern, int Pos)> Matched)>();
			foreach (var group in patterns)
			{
				if (!group.IsConfigured)
				{
					continue;
				}

				var comparison = group.CaseSensitive
					? StringComparison.Ordinal
					: StringComparison.OrdinalIgnoreCase;

				if (group.GroupPatterns)
				{
					var matched = new List<(string Pattern, int Pos)>();
					foreach (var pattern in group.Patterns)
					{
						if (string.IsNullOrEmpty(pattern))
						{
							continue;
						}

						var pos = html.IndexOf(pattern, comparison);
						if (pos >= 0)
						{
							matched.Add((pattern, pos));
						}
					}

					if (matched.Count > 0)
					{
						atoms.Add((group, true, matched));
					}
				}
				else
				{
					foreach (var pattern in group.Patterns)
					{
						if (string.IsNullOrEmpty(pattern))
						{
							continue;
						}

						int pos = 0;
						while ((pos = html.IndexOf(pattern, pos, comparison)) >= 0)
						{
							atoms.Add((group, false, [(pattern, pos)]));
							pos += pattern.Length;
						}
					}
				}
			}

			// Pass 2 — coalesce the clear case: a BROKEN (open) envelope plus the hint
			// patterns it References, sitting inside the envelope's region, collapse into
			// ONE finding. Generic by design — the trigger is the structural fact "open
			// delimiter pair whose Reference set has hits in range", never a literal string.
			// Purely SUBTRACTIVE: folding happens only on that corroborated case; every other
			// atom emits exactly as before, so the worst case equals the pre-feature output.
			var consumed = new HashSet<int>();
			var merges = new List<(int EnvIdx, int OpenerPos, string Opener, string Closer, List<int> Folded)>();
			for (int i = 0; i < atoms.Count; i++)
			{
				if (consumed.Contains(i))
				{
					continue;
				}

				var (set, grouped, matched) = atoms[i];
				// Envelope = a grouped opener/closer pair that names (via Reference) the set
				// expected inside it. Only an OPEN envelope — opener present, closer absent —
				// is the broken case we coalesce; a balanced pair is left to emit as today.
				if (!grouped || set.Patterns.Count != 2 || string.IsNullOrEmpty(set.Reference))
				{
					continue;
				}

				var opener = set.Patterns[0];
				var closer = set.Patterns[1];
				var openerEntry = matched.FirstOrDefault(m => m.Pattern == opener);
				bool openerMatched = openerEntry.Pattern != null;
				bool closerMatched = matched.Any(m => m.Pattern == closer);
				if (!openerMatched || closerMatched)
				{
					continue;
				}

				int openerPos = openerEntry.Pos;
				int regionEnd = FindEnvelopeRegionEnd(html, openerPos + opener.Length);

				var folded = new List<int>();
				for (int j = 0; j < atoms.Count; j++)
				{
					if (j == i || consumed.Contains(j))
					{
						continue;
					}

					var other = atoms[j];
					if (!other.Grouped
						&& string.Equals(other.Set.Name, set.Reference, StringComparison.Ordinal)
						&& other.Matched[0].Pos > openerPos
						&& other.Matched[0].Pos < regionEnd)
					{
						folded.Add(j);
					}
				}

				if (folded.Count == 0)
				{
					// Booster, not gate: an uncorroborated open envelope still fires — as today.
					continue;
				}

				consumed.Add(i);
				foreach (var j in folded)
				{
					consumed.Add(j);
				}

				merges.Add((i, openerPos, opener, closer, folded));
			}

			// Pass 3 — emit. Merged findings first, then every non-consumed atom unchanged.
			var issues = new List<QualityIssue>();

			foreach (var (envIdx, openerPos, opener, closer, folded) in merges)
			{
				var env = atoms[envIdx];
				// Highlight list = opener plus each folded hint pattern in document order,
				// de-duplicated. Kept as the trailing "— patterns: …" segment so the existing
				// ExtractHighlightPatterns marks all of them on the card AND in the ticket,
				// and so suppression round-trips on one stable composite key.
				var byPos = new List<(string Pattern, int Pos)> { (opener, openerPos) };
				foreach (var j in folded)
				{
					byPos.Add(atoms[j].Matched[0]);
				}

				var ordered = byPos
					.OrderBy(x => x.Pos)
					.Select(x => x.Pattern)
					.Distinct(StringComparer.Ordinal)
					.ToList();

				var detail =
					$"{env.Set.Category}: {env.Set.Name} — open placeholder, missing closing '{closer}'" +
					$" — patterns: {string.Join(", ", ordered)}";
				issues.Add(new QualityIssue(
					filename, "UNWANTED_PATTERN", detail,
					Excerpt.Around(html, openerPos, config.ContentQualityExcerptRadius)));
			}

			for (int i = 0; i < atoms.Count; i++)
			{
				if (consumed.Contains(i))
				{
					continue;
				}

				var (set, grouped, matched) = atoms[i];
				if (grouped)
				{
					// [KEEP] Grouped mode — at most ONE issue per page per named group. All
					// patterns together indicate a single defect (e.g. a full CMS variable
					// — fix is CMS-side, not per pattern). Word = group Name for
					// a stable IssueTracking identity key across runs.
					var matchedPatterns = matched.Select(m => m.Pattern).ToList();
					var detail = $"{set.Category}: {set.Name}" +
						(matchedPatterns.Count > 1
							? $" — patterns: {string.Join(", ", matchedPatterns)}"
							: $" — pattern: {matchedPatterns[0]}");
					issues.Add(new QualityIssue(
						filename, "UNWANTED_PATTERN", detail,
						Excerpt.Around(html, matched[0].Pos, config.ContentQualityExcerptRadius)));
				}
				else
				{
					// Ungrouped mode — one issue per pattern occurrence per page (default).
					var (pattern, pos) = matched[0];
					issues.Add(new QualityIssue(
						filename,
						"UNWANTED_PATTERN",
						$"{set.Category}: {set.Name} — pattern: {pattern}",
						Excerpt.Around(html, pos, config.ContentQualityExcerptRadius)));
				}
			}

			return issues;
		}
	}
}
