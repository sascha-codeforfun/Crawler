namespace Crawler
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using System.Text;
	using System.Text.RegularExpressions;
	using Crawler.SpellCheck;

	/// <summary>
	/// The CONTENT_BEFORE_DOCTYPE bag: a lossless triage rollup for spelling "findings" that are
	/// actually parse shrapnel, not typos.
	///   • Gate is the HTML sub-code MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE. Bytes before the
	///     doctype mean the parse is corrupt upstream of all content, so on such a page a spelling
	///     finding whose word contains a non-word character is debris. Only this sub-code is gated.
	///   • The word-side test is U+FFFD in an occurrence excerpt. The replacement character is what
	///     a decoder emits for bytes it cannot decode, so its presence means the spell check ran on
	///     text that did not decode cleanly — the "finding" is an artifact of the broken decode, not
	///     a typo, and cannot be safely judged. Bagging declines to claim on unreadable text rather
	///     than emitting a confident false typo. The finding is rolled up, not dropped — nothing is
	///     lost.
	///   • If diagnostic header extractors are configured, their (Label, Pattern) values are read
	///     to group the bagged pages for easier correlation; otherwise pages group by their set of
	///     cookie names.
	///
	/// This is a triage-layer aid: it only runs in interactive triage (the only place a human can
	/// action it), and the ticket it produces is recoverable, not a silent drop.
	/// </summary>
	public static class ContentBeforeDoctypeBag
	{
		/// <summary>Ledger marker and the name of the defect.</summary>
		public const string Marker = "CONTENT_BEFORE_DOCTYPE bag";

		private const string MalformedHtmlType = "MALFORMED_HTML";
		private const string SubCode = "CONTENT_BEFORE_DOCTYPE";

		// ── Predicate ───────────────────────────────────────────────────────────

		/// <summary>U+FFFD, the Unicode replacement character a conformant decoder emits for bytes
		/// it cannot decode. Its presence means the text was not decoded cleanly.</summary>
		private const char ReplacementChar = '\uFFFD';

		/// <summary>
		/// True when any of the ticket's occurrence excerpts contains U+FFFD. A replacement character
		/// means the spell check ran over text that did not decode cleanly, so the finding cannot be
		/// safely judged as a typo — had the bytes decoded correctly, the word might be perfectly
		/// valid. Whether the U+FFFD severed this exact word or a neighbour, the excerpt is corrupt
		/// and the finding is unsafe; bag it rather than show a confident card.
		/// </summary>
		public static bool ExcerptHasReplacementChar(WordTicket ticket)
		{
			foreach (var occ in ticket.Occurrences)
			{
				if (occ.Excerpt.IndexOf(ReplacementChar) >= 0)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Splits tickets into the CONTENT_BEFORE_DOCTYPE bag (page is in <paramref name="cbdUrls"/>
		/// AND an occurrence excerpt contains U+FFFD) and the rest (triaged normally). Order
		/// preserved; lossless — every ticket lands in exactly one list.
		/// </summary>
		public static (List<WordTicket> Bag, List<WordTicket> Remaining) Partition(
			IEnumerable<WordTicket> tickets, ISet<string> cbdUrls)
		{
			var bag = new List<WordTicket>();
			var rest = new List<WordTicket>();
			foreach (var t in tickets)
			{
				if (cbdUrls.Contains(t.Url) && ExcerptHasReplacementChar(t))
				{
					bag.Add(t);
				}
				else
				{
					rest.Add(t);
				}
			}

			return (bag, rest);
		}

		/// <summary>
		/// The set of URLs carrying MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE, parsed from this run's
		/// content-quality log lines (Filename|IssueType|Detail|Context). Generic: keys on the
		/// universal type + sub-code only. <paramref name="urlForFile"/> maps the log's filename to
		/// a URL (the same identity tickets carry).
		/// </summary>
		public static HashSet<string> BuildContentBeforeDoctypeUrls(
			IEnumerable<string> cqLogLines, Func<string, string> urlForFile)
		{
			var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var line in cqLogLines)
			{
				if (line.Length == 0 || line.StartsWith("Filename|", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var p = line.Split('|');
				if (p.Length < 3)
				{
					continue;
				}

				if (!string.Equals(p[1].Trim(), MalformedHtmlType, StringComparison.Ordinal)
					|| !string.Equals(p[2].Trim(), SubCode, StringComparison.Ordinal))
				{
					continue;
				}

				var url = urlForFile(p[0].Trim());
				if (!string.IsNullOrEmpty(url) && url != "error")
				{
					urls.Add(url);
				}
			}

			return urls;
		}

		// ── Sidecar diagnostics ─────────────────────────────────────────────────

		private static readonly Regex DateLine =
			new(@"^Date:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline);

		private static readonly Regex SetCookieLine =
			new(@"^Set-Cookie:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

		/// <summary>Per-page values read from one .header sidecar.</summary>
		public sealed record PageDiagnostics(
			string Url,
			string? DateRaw,
			DateTimeOffset? DateParsed,
			IReadOnlyList<string> CookieNames,
			IReadOnlyList<(string Label, string Value)> ExtractedTokens,
			bool SidecarFound);

		/// <summary>
		/// Parses the Date header value (RFC 7231 IMF-fixdate) to a sortable instant. Returns null
		/// when absent/unparseable — the caller counts these rather than dropping the page.
		/// </summary>
		public static DateTimeOffset? ParseHttpDate(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return null;
			}

			if (DateTimeOffset.TryParseExact(
					raw.Trim(), "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
					CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
			{
				return exact;
			}

			return DateTimeOffset.TryParse(
				raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var loose)
				? loose
				: null;
		}

		/// <summary>
		/// The sorted, distinct set of cookie NAMES from a sidecar's Set-Cookie lines (the name is
		/// the token before the first '='). Never returns values — they carry session/visitor
		/// tokens and never dedup across responses.
		/// </summary>
		public static IReadOnlyList<string> CookieNamesFromSidecar(string sidecarText)
		{
			var names = new SortedSet<string>(StringComparer.Ordinal);
			foreach (Match m in SetCookieLine.Matches(sidecarText))
			{
				var v = m.Groups[1].Value;
				var eq = v.IndexOf('=');
				var name = (eq > 0 ? v[..eq] : v).Trim();
				if (name.Length > 0)
				{
					names.Add(name);
				}
			}

			return names.ToList();
		}

		/// <summary>
		/// Applies each (Label, compiled Pattern) extractor to the full sidecar text; capture group
		/// 1 is the value recorded under Label.
		/// </summary>
		public static IReadOnlyList<(string Label, string Value)> ExtractTokens(
			string sidecarText, IReadOnlyList<(string Label, Regex Pattern)> extractors)
		{
			var found = new List<(string, string)>();
			foreach (var (label, pattern) in extractors)
			{
				var m = pattern.Match(sidecarText);
				if (m.Success && m.Groups.Count > 1)
				{
					found.Add((label, m.Groups[1].Value.Trim()));
				}
			}

			return found;
		}

		/// <summary>
		/// Reads one sidecar's full text into a <see cref="PageDiagnostics"/> (Date, cookie names,
		/// extractor tokens). Pure — file reading and the "(sidecar unavailable)" path are the caller's.
		/// </summary>
		public static PageDiagnostics DiagnosticsFromSidecarText(
			string url, string sidecarText, IReadOnlyList<(string Label, Regex Pattern)> extractors)
		{
			var dm = DateLine.Match(sidecarText);
			var dateRaw = dm.Success ? dm.Groups[1].Value.Trim() : null;
			return new PageDiagnostics(
				url,
				dateRaw,
				ParseHttpDate(dateRaw),
				CookieNamesFromSidecar(sidecarText),
				ExtractTokens(sidecarText, extractors),
				SidecarFound: true);
		}

		// ── Ticket text ─────────────────────────────────────────────────────────

		/// <summary>
		/// Builds the ticket text: a Date window, then the pages grouped by extractor token
		/// (when any extractor matched) else by cookie-name set, with cookie names shown only in the
		/// fallback grouping. <paramref name="findingCount"/> is the number of bagged spelling
		/// findings (words) behind the <paramref name="pages"/>.
		///
		/// COOKIE-NAME RENDER TOGGLE: cookie names are printed only for fallback (no-extractor)
		/// groups. To always show them, drop the <c>!hasExtractorGroup</c> guard at the marked line.
		/// </summary>
		public static string BuildTicketText(IReadOnlyList<PageDiagnostics> pages, int findingCount)
		{
			var sb = new StringBuilder();
			var pageCount = pages.Count;

			sb.Append(Marker).Append(" — ").Append(pageCount).Append(" page(s) with parse-corrupting content before <!doctype>\n\n");
			sb.Append("Detected via spelling: ").Append(findingCount)
				.Append(" finding(s) on these pages contain characters no real word does (parse\n")
				.Append("artifacts, not typos). The fix is the response emission, not the individual\n")
				.Append("words — do not triage them one by one.\n\n");

			// Window over parseable Dates; unparseable/absent counted, never dropped.
			var dated = pages.Where(p => p.DateParsed.HasValue).Select(p => p.DateParsed!.Value).ToList();
			var undated = pageCount - dated.Count;
			sb.Append("Response window (from .header sidecars):\n");
			if (dated.Count > 0)
			{
				sb.Append("  earliest: ").Append(FormatInstant(dated.Min())).Append('\n');
				sb.Append("  latest:   ").Append(FormatInstant(dated.Max())).Append('\n');
			}
			else
			{
				sb.Append("  (no parseable Date header on any page)\n");
			}

			if (undated > 0)
			{
				sb.Append("  (").Append(undated).Append(" page(s): Date header missing or unparseable)\n");
			}

			sb.Append('\n');

			// Group by extractor-token tuple when present; else by cookie-name set.
			static string ExtractorKey(PageDiagnostics p) =>
				p.ExtractedTokens.Count == 0
					? string.Empty
					: string.Join("  ", p.ExtractedTokens.OrderBy(t => t.Label, StringComparer.Ordinal)
						.Select(t => $"{t.Label}={t.Value}"));

			static string CookieKey(PageDiagnostics p) =>
				p.CookieNames.Count == 0 ? "(none — no cookie set)" : string.Join(", ", p.CookieNames);

			var groups = pages
				.GroupBy(p =>
				{
					var ek = ExtractorKey(p);
					return ek.Length > 0 ? "X|" + ek : "C|" + CookieKey(p);
				})
				.OrderByDescending(g => g.Count())
				.ToList();

			sb.Append("Affected pages, grouped by shared response signature:\n\n");
			foreach (var g in groups)
			{
				bool hasExtractorGroup = g.Key.StartsWith("X|", StringComparison.Ordinal);
				var header = g.Key[2..];
				sb.Append("  ── ").Append(hasExtractorGroup ? header : "cookies: " + header).Append('\n');
				foreach (var p in g.OrderBy(p => p.Url, StringComparer.Ordinal))
				{
					sb.Append("     ").Append(p.Url);
					if (!p.SidecarFound)
					{
						sb.Append("   (sidecar unavailable)");
					}

					sb.Append('\n');
				}

				sb.Append('\n');
			}

			sb.Append("Note: pages sharing an extracted token may share a common root cause; the\n");
			sb.Append("grouping surfaces this for correlation only and does not imply causality.\n");
			sb.Append("Investigate the emission path (early output before the document, BOM/whitespace\n");
			sb.Append("leakage, or a pre-document injection).\n");

			return sb.ToString();
		}

		private static string FormatInstant(DateTimeOffset dto) =>
			dto.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
	}
}
