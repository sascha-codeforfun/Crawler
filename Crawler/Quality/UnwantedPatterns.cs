namespace Crawler.Quality
{
	using System.Text.RegularExpressions;

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

		// The binding-identifier inner shape: a continuous run of letters, digits, '.' and '_'.
		private const string BindingIdentifier = "[A-Za-z0-9._]+";

		// Finds the first MALFORMED template binding for an OnlyFlagUnbalanced envelope, or null.
		// A binding is an identifier fenced by the envelope's delimiter chars. The leak signal
		// is the FULL doubled fence ("{{" or "}}"); a lone single brace is ambient noise (CSS,
		// "{n}" placeholders, prose) and must stay quiet or it buries real findings. So a finding
		// requires a full fence on exactly ONE side:
		//   leftFull && rightFull   → well-formed "{{foo}}" → SILENT.
		//   leftFull XOR rightFull  → one full fence, other side not full → MALFORMED → fires
		//                             ("{{foo}", "{foo}}", "{{foo", "foo}}").
		//   neither side full       → no doubled fence at all → ignore ("{n}", "{foo", "foo}",
		//                             bare words, any single-brace shape).
		//
		// The single regex anchors the identifier between a start/whitespace boundary and a
		// whitespace/end boundary (the "clean flank"), capturing the opener-char run on the left
		// and the closer-char run on the right. That clean flank is the false-positive guard:
		// structural brace runs in embedded JSON/CSS never qualify because a non-space, non-brace
		// neighbour sits against them — "display:none}}" (':' left), "…2}}," (',' right),
		// "…null}}'" (':'/'\''), "Weiter&#34;}}\"" ('\"').
		// Assumes a doubled-char fence (e.g. "{{"/"}}"), which is the OnlyFlagUnbalanced use case;
		// the opener/closer char is taken from the delimiter's first character.
		private static Match? FindMalformedBinding(string html, string opener, string closer)
		{
			if (string.IsNullOrEmpty(opener) || string.IsNullOrEmpty(closer))
			{
				return null;
			}

			string oc = Regex.Escape(opener[0].ToString());
			string cc = Regex.Escape(closer[0].ToString());
			string pattern = "(?:^|\\s)(" + oc + "*)(" + BindingIdentifier + ")(" + cc + "*)(?=\\s|$)";

			foreach (Match m in Regex.Matches(html, pattern))
			{
				bool leftFull = m.Groups[1].Length == opener.Length;
				bool rightFull = m.Groups[3].Length == closer.Length;
				if (leftFull != rightFull)
				{
					return m; // exactly one full fence — malformed → fires
				}
			}

			return null;
		}

		// <style>…</style> and <script>…</script> spans, non-greedy, across newlines. Used to
		// blank those regions for a pattern that opts out of scanning them (CheckStyle/CheckScript).
		private static readonly Regex StyleRegion =
			new("<style\\b[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
		private static readonly Regex ScriptRegion =
			new("<script\\b[^>]*>.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

		// The VALUE of an href attribute, either quote style. The attribute name must be preceded
		// by whitespace, '<', or a quote (an attribute boundary), so only a real href is caught —
		// NOT data-href, xlink:href, or any *-href where a name char or '-'/':' sits before it (a
		// plain \bhref boundary would wrongly match "data-|href"). Group 1 is the quoted value
		// (the part blanked); the href= and the quotes themselves are preserved so the surrounding
		// markup — and every offset outside the value — is unchanged. Used to blank link-URL
		// values for a pattern that opts out of scanning them (ExcludeHref).
		private static readonly Regex HrefValueRegion =
			new("(?<=[\\s<\"'])href\\s*=\\s*(\"[^\"]*\"|'[^']*')", RegexOptions.Singleline | RegexOptions.IgnoreCase);

		// Returns html with the requested regions replaced by EQUAL-LENGTH runs of spaces, so a
		// scan over the result yields no match inside them while every match position elsewhere
		// still indexes back into the original html (excerpts and Reference-fold ranges stay
		// correct). Masking the passes in sequence is safe: the spans do not overlap and each
		// replacement preserves length, so later passes see unchanged offsets.
		private static string MaskRegions(string html, bool maskStyle, bool maskScript, bool maskHref)
		{
			string result = html;
			if (maskStyle)
			{
				result = StyleRegion.Replace(result, BlankSpan);
			}

			if (maskScript)
			{
				result = ScriptRegion.Replace(result, BlankSpan);
			}

			if (maskHref)
			{
				// Blank only the quoted value (group 1), keeping href= and the quotes so the
				// span stays equal length and offsets outside the value are untouched.
				result = HrefValueRegion.Replace(result, MaskHrefValue);
			}

			return result;
		}

		private static string BlankSpan(Match m) => new string(' ', m.Length);

		// Rebuilds an href="…"/href='…' match with only the inside of the quotes blanked, so the
		// overall length is preserved (offset-safe) and the attribute structure stays intact.
		private static string MaskHrefValue(Match m)
		{
			string quoted = m.Groups[1].Value;           // includes the surrounding quotes
			char quote = quoted[0];
			int innerLen = quoted.Length - 2;            // value length between the quotes
			string blanked = quote + new string(' ', innerLen) + quote;
			// Prefix = the part of the match before the quoted value (the "href=" and any
			// whitespace). Compute the length as an int first, then slice, so the range
			// operator is not applied to the arithmetic.
			int prefixLen = m.Groups[1].Index - m.Index;
			return m.Value[..prefixLen] + blanked;
		}

		// ── ExcludeUrl classification ─────────────────────────────────────────
		// Separator that tags an occurrence's Detail as a url-type match, carrying a
		// locator and the url token so the triage layer can group url occurrences per
		// page and list each for the editor. Chosen to survive the ledger's field
		// sanitiser (no pipe, no control/zero-width chars) and to be unmistakable in a
		// Detail string. Frozen: renaming it would orphan any ticketed url summaries.
		internal const string UrlMarker = " \u00A6\u00A6URL\u00A6\u00A6 ";

		// Classifies the match at <paramref name="pos"/> as url-type or not, and returns
		// the url token plus a coarse locator when it is. A match is url-type when the
		// token it sits in is SLASH-BEARING. The token is the enclosing quoted value
		// (href/src/etc.) when the match is inside quotes — so trailing markup like " />"
		// after the closing quote is never part of it, and a whitespace INSIDE the value
		// (a defective url missing its percent-encoding) breaks the token, which then may
		// be slash-less and fall through as a non-url occurrence. When the match is not
		// quoted, the token is the whitespace/&lt;&gt;-bounded run. A slash-less token
		// (bare filename, comment slug, whitespace-broken fragment) is NOT url-type and
		// surfaces as its own occurrence — acceptable, and it also surfaces the underlying
		// defect. Detection is unaffected: this only classifies an already-found match.
		private static bool TryClassifyUrl(string html, int pos, out string locator, out string urlToken)
		{
			locator = string.Empty;
			urlToken = string.Empty;

			// Walk left to the nearest token boundary: a quote, whitespace, or angle bracket.
			int start = pos;
			while (start > 0)
			{
				char c = html[start - 1];
				if (c is '"' or '\'' or '<' or '>' || char.IsWhiteSpace(c))
				{
					break;
				}

				start--;
			}

			int end = pos;
			while (end < html.Length)
			{
				char c = html[end];
				if (c is '"' or '\'' or '<' or '>' || char.IsWhiteSpace(c))
				{
					break;
				}

				end++;
			}

			string token = html[start..end];
			if (!token.Contains('/'))
			{
				return false; // slash-less → not url-type
			}

			urlToken = token;
			locator = ClassifyLocator(html, start);
			return true;
		}

		// Coarse "where does this url live" tag for the editor, derived from the bytes
		// immediately before the token's opening quote/position. Best-effort: an unknown
		// context degrades to a generic tag rather than guessing.
		private static string ClassifyLocator(string html, int tokenStart)
		{
			// Look back a short, bounded window for the attribute name / structural cue.
			int from = Math.Max(0, tokenStart - 24);
			string before = html[from..tokenStart].ToLowerInvariant();

			if (before.Contains("href")) return "link";
			if (before.Contains("src")) return "src";
			if (before.Contains("action")) return "form-action";
			if (before.Contains("poster")) return "poster";
			if (before.Contains("cite")) return "cite";
			if (before.Contains("content") || before.Contains("url")) return "meta-url";
			return "url";
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

			// Per-pattern scan text: the raw html by default, or an offset-preserving masked
			// copy when the pattern opts out of <script>/<style>. Variants are cached so several
			// patterns sharing the same opt-out share one masked string.
			var maskedCache = new Dictionary<(bool MaskStyle, bool MaskScript, bool MaskHref), string>();
			string ScanText(ContentUnwantedPattern g)
			{
				bool maskStyle = !g.CheckStyle;
				bool maskScript = !g.CheckScript;
				bool maskHref = g.ExcludeHref;
				if (!maskStyle && !maskScript && !maskHref)
				{
					return html;
				}

				var key = (maskStyle, maskScript, maskHref);
				if (!maskedCache.TryGetValue(key, out var masked))
				{
					masked = MaskRegions(html, maskStyle, maskScript, maskHref);
					maskedCache[key] = masked;
				}

				return masked;
			}

			foreach (var group in patterns)
			{
				if (!group.IsConfigured)
				{
					continue;
				}

				var scanText = ScanText(group);
				var comparison = group.CaseSensitive
					? StringComparison.Ordinal
					: StringComparison.OrdinalIgnoreCase;

				if (group.GroupPatterns)
				{
					// OnlyFlagUnbalanced (envelope-only): TEMPLATE-BINDING mode. A well-formed
					// "{{foo}}" is fine and stays SILENT — the JS binds it at runtime. Only a
					// MALFORMED binding fires: a cleanly-flanked identifier whose fences are not
					// the full opener+closer — "{foo}}", "foo}}", "{{foo}", "{{foo" — because the
					// user would see the broken delimiter. The clean-flank requirement (the
					// identifier bounded by whitespace/edge on the outer side) is the false-
					// positive guard: structural brace runs in embedded JSON/CSS (…2}},,
					// display:none}}, …null}}') sit against a non-space, non-brace neighbour and
					// never qualify. When false (default), fall through to the "any occurrence
					// fires" behaviour below.
					if (group.OnlyFlagUnbalanced && group.Patterns.Count == 2)
					{
						var m = FindMalformedBinding(scanText, group.Patterns[0], group.Patterns[1]);
						if (m is null)
						{
							continue; // balanced binding or no binding — emit nothing
						}

						int leftLen = m.Groups[1].Length;
						int rightLen = m.Groups[3].Length;
						var binding = new List<(string Pattern, int Pos)>();
						if (leftLen > 0)
						{
							binding.Add((group.Patterns[0], m.Groups[1].Index));
						}

						if (rightLen > 0)
						{
							binding.Add((group.Patterns[1], m.Groups[3].Index));
						}

						atoms.Add((group, true, binding));
						continue;
					}

					var matched = new List<(string Pattern, int Pos)>();
					foreach (var pattern in group.Patterns)
					{
						if (string.IsNullOrEmpty(pattern))
						{
							continue;
						}

						var pos = scanText.IndexOf(pattern, comparison);
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
						while ((pos = scanText.IndexOf(pattern, pos, comparison)) >= 0)
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
					var detail = $"{set.Category}: {set.Name} — pattern: {pattern}";

					// ExcludeUrl: tag a url-type occurrence so the triage layer can collapse
					// this page's url occurrences into one summary finding while still listing
					// each. Non-url occurrences (text, comments) are left untagged and stay
					// one finding each. Position maps into the original html: href masking (if
					// also on) is equal-length, so surviving match offsets are unchanged.
					if (set.ExcludeUrl
						&& TryClassifyUrl(html, pos, out var locator, out var urlToken))
					{
						detail += $"{UrlMarker}[{locator}] {urlToken}";
					}

					issues.Add(new QualityIssue(
						filename,
						"UNWANTED_PATTERN",
						detail,
						Excerpt.Around(html, pos, config.ContentQualityExcerptRadius)));
				}
			}

			// Pass 4 — culprit url. Runs for a set with ExcludeHref or ExcludeUrl, to recover
			// the one signal that masking / grouping would otherwise lose: the page whose OWN
			// url carries the pattern is the source of the slug the masked links pointed at.
			// Without this, masking the repeated navigation links (the noise) would also hide
			// the culprit page itself. The page url is derived from the filename via the crawl
			// index; on a lookup miss ("error") nothing is emitted (fail-safe — never invent a
			// finding from an unresolvable url). When ExcludeUrl is on for the set, the culprit
			// is tagged as a "[page-url]" url occurrence so it folds into that page's url
			// summary — the lone occurrence if nothing else survives. When only ExcludeHref is
			// on, it emits the standalone "(in this page's url)" finding as before.
			string? culpritUrl = null;
			bool culpritResolved = false;
			foreach (var group in patterns)
			{
				if (!group.IsConfigured || (!group.ExcludeHref && !group.ExcludeUrl))
				{
					continue;
				}

				if (!culpritResolved)
				{
					var resolved = CrawlIndex.LookUpUrlForFile(filename);
					culpritUrl = resolved == "error" ? null : resolved;
					culpritResolved = true;
				}

				if (culpritUrl is null)
				{
					continue;
				}

				var comparison = group.CaseSensitive
					? StringComparison.Ordinal
					: StringComparison.OrdinalIgnoreCase;

				foreach (var pattern in group.Patterns)
				{
					if (string.IsNullOrEmpty(pattern)
						|| culpritUrl.IndexOf(pattern, comparison) < 0)
					{
						continue;
					}

					var detail = group.ExcludeUrl
						? $"{group.Category}: {group.Name} — pattern: {pattern}{UrlMarker}[page-url] {culpritUrl}"
						: $"{group.Category}: {group.Name} — pattern: {pattern} (in this page's url)";

					issues.Add(new QualityIssue(
						filename,
						"UNWANTED_PATTERN",
						detail,
						culpritUrl));
				}
			}

			return issues;
		}
	}
}
