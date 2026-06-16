namespace Crawler
{
	using System.Globalization;
	using System.Text;

	/// <summary>
	/// Renders the operator-facing ticket text (TicketText.log) — per-URL blocks
	/// with headline, page metadata, context and per-error detail. Spelling findings
	/// are sourced from the IssueTracking ledger (operator-raised 'pending' rows;
	/// 'wontfix' suppressed), 404 findings from the sources log. Depends on
	/// <see cref="SpellMetadataLookup"/> for per-URL ticket metadata.
	/// </summary>
	public static class TicketRenderer
	{
		// ── Ticket text (human-readable, copy-paste) ──────────────────────────────────

		/// <summary>
		/// Writes TicketText.log — one block per URL of actionable findings.
		/// Spelling is sourced from the IssueTracking ledger: only operator-raised
		/// SPELLING rows (status 'pending') appear; 'wontfix' is suppressed. A pending
		/// row whose DateFound is older than TicketGeneration.OverdueAfterDays renders
		/// as OVERDUE (escalated), otherwise as PENDING — the NEW/OVERDUE split that
		/// drove the action zone is now PENDING/OVERDUE, computed wall-clock at render
		/// time (SPELLING is exempt from the end-of-run Merge, so nothing else escalates
		/// it). 404 findings still come from the sources log. Uses configured templates
		/// with {Location}, {Url}, {Module}, {Errors} placeholders. Regenerated every run.
		///
		/// Prepends a header block documenting the metadata source (path, file
		/// date, age in days) so the operator reviewing tickets days later knows
		/// the provenance of the ticket data. Emits a WARNING line when the
		/// source was over MaxAgeDays at generation time. Stateless — recomputes
		/// freshness from disk; if the operator updates the CSV mid-run, the
		/// header reflects the newer state (acknowledged edge case).
		/// </summary>
		public static void WriteTicketText(
			string textFilePath,
			List<IssueTracking.IssueRecord> spellingRows,
			TicketGenerationConfig ticketConfig,
			CmsContentListConfig? cmsContentList,
			Func<string, SpellMetadataLookup.TicketMetadata> metadataLookup,
			string? errorSourcesLogPath = null,
			List<string>? allowedSubdomains = null,
			List<IssueTracking.IssueRecord>? qualityRows = null)
		{
			if (!ticketConfig.IsConfigured)
			{
				return;
			}

			// TicketText.log is now multi-type. Findings of every eligible
			// type are normalised to TicketFinding, keyed on the page whose
			// editor owns the fix (spelling: the page the word is on; 404: the
			// SOURCE page that carries the broken link, not the dead target).
			// One block per URL; within a block, sections render worst-first in
			// TicketIssueTypes order. Status (PENDING/OVERDUE for spelling, NEW for
			// 404) drives the operator action zone above the copy-paste body — it
			// never enters the body. wontfix/fixed/config are excluded: only
			// actionable findings produce tickets.
			var findings = new List<TicketFinding>();
			findings.AddRange(BuildSpellingFindings(spellingRows, ticketConfig));
			findings.AddRange(BuildQualityFindings(qualityRows ?? [], ticketConfig));
			if (!string.IsNullOrEmpty(errorSourcesLogPath))
			{
				findings.AddRange(BuildBrokenLinkFindings(errorSourcesLogPath));
			}

			// Severity order is driven solely by TicketIssueTypes. An
			// unconfigured list means no eligible types → no tickets emitted.
			var typeOrder = ticketConfig.TicketIssueTypes;
			var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < typeOrder.Count; i++)
			{
				orderIndex[typeOrder[i].Type] = i;
			}

			// Keep only eligible (listed) types and actionable statuses. Actionable =
			// new (404) / pending (ticketed spelling) / overdue (escalated spelling).
			// wontfix/fixed/config are not actionable. BuildSpellingFindings already
			// drops non-pending spelling, so this is the type/eligibility gate.
			findings = [.. findings.Where(f =>
				orderIndex.ContainsKey(f.Type)
				&& (f.Status == StatusNew || f.Status == StatusPending || f.Status == StatusOverdue))];

			if (findings.Count == 0)
			{
				FileIo.WriteAllTextWithRetry(textFilePath, string.Empty, Path.GetFileName(textFilePath));
				return;
			}

			var byUrl = findings
				.GroupBy(f => f.Url, StringComparer.OrdinalIgnoreCase)
				.OrderBy(g => g.Key, StringComparer.Ordinal);   // URL-ordered for stable diffs

			var sb = new StringBuilder();

			// Metadata-source provenance header: one
			// header for the whole file when a CSV was configured and present.
			var freshness = CmsContentListFreshnessCheck.Evaluate(cmsContentList);
			if (freshness.IsConfigured && freshness.FileExists)
			{
				sb.AppendLine(Divider.DoubleLine);
				sb.AppendLine("METADATA SOURCE");
				sb.AppendLine(Divider.DoubleLine);
				sb.AppendLine($"NOTE: The metadata for the following tickets was derived from");
				sb.AppendLine($"      {freshness.Path}");
				sb.AppendLine($"      File date: {freshness.FileDate:yyyy-MM-dd HH:mm} "
					+ $"({freshness.AgeDays} day(s) old)");
				if (freshness.IsStale)
				{
					sb.AppendLine();
					sb.AppendLine($"WARNING: Metadata file is older than MaxAgeDays ({freshness.MaxAgeDays}).");
				}
				sb.AppendLine();
			}

			bool firstBlock = true;
			foreach (var group in byUrl)
			{
				if (!firstBlock)
				{
					sb.AppendLine(Divider.Hash);
					sb.AppendLine();
				}
				firstBlock = false;

				AppendUrlBlock(sb, group.Key, [.. group], ticketConfig, metadataLookup, orderIndex, allowedSubdomains);
			}

			FileIo.WriteAllTextWithRetry(textFilePath, sb.ToString().TrimEnd(), Path.GetFileName(textFilePath));
		}

		// Internal finding-type tokens. SpellingType matches the legacy default
		// the SPELLING finding type; BrokenLinkType matches IssueTracking's "404".
		internal const string SpellingType = "SPELLING";
		internal const string BrokenLinkType = "404";
		internal const string QualityType = "QUALITY";

		// Actionable status tokens. 404 is always 'new'; spelling enters the ledger
		// as operator-raised 'pending' and escalates to 'overdue' by the wall-clock
		// OverdueAfterDays window (computed at render time, since SPELLING is exempt
		// from the end-of-run Merge that would otherwise flip the stored status).
		internal const string StatusNew = "new";
		internal const string StatusPending = "pending";
		internal const string StatusOverdue = "overdue";

		/// <summary>
		/// Neutral, type-agnostic ticket finding. Url is the OWNING page
		/// (the editor's page that gets the fix). PrimaryText is the word (spelling)
		/// or the dead target path (404). Context/Comment/Annotation are optional.
		/// </summary>
		internal sealed record TicketFinding(
			string Type,
			string Url,
			string PrimaryText,
			string Status,
			string Context = "",
			string SourceLabel = "",
			string Comment = "",
			string Language = "");

		/// <summary>
		/// Maps IssueTracking SPELLING rows to findings. Only 'pending' rows (operator
		/// tickets) are emitted; everything else (wontfix, etc.) is dropped. A pending
		/// row older than OverdueAfterDays (by DateFound, wall-clock) is escalated to
		/// OVERDUE; an unparseable DateFound is treated as not escalated.
		/// </summary>
		private static IEnumerable<TicketFinding> BuildSpellingFindings(
			List<IssueTracking.IssueRecord> spellingRows, TicketGenerationConfig ticketConfig)
		{
			var today = DateTime.UtcNow.Date;
			foreach (var r in spellingRows)
			{
				if (!r.Status.Equals(StatusPending, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var escalated =
					DateTime.TryParse(r.DateFound, CultureInfo.InvariantCulture, DateTimeStyles.None, out var found)
					&& found.Date.AddDays(ticketConfig.OverdueAfterDays) <= today;

				yield return new TicketFinding(
					Type: SpellingType,
					Url: r.Url,
					PrimaryText: r.Word,
					Status: escalated ? StatusOverdue : StatusPending,
					Context: r.Excerpt,
					SourceLabel: r.SourceLabel,
					Comment: r.Comment,
					Language: r.Language);
			}
		}

		/// <summary>
		/// Maps IssueTracking QUALITY rows to findings. Only 'pending' rows (content-
		/// quality items the operator ticketed in CQ triage) are emitted; auto-promoted
		/// 'new' rows and resolved (wontfix/config/fixed) rows are dropped. A pending row
		/// older than OverdueAfterDays escalates to OVERDUE, identical to spelling — the
		/// stored status never flips (gone-is-gone Merge keeps it verbatim), so escalation
		/// is computed wall-clock at render time. PrimaryText is the check name (Word);
		/// any pre-built per-type comment (e.g. the UNWANTED_PATTERN reference) renders
		/// under the '+' rule, exactly as a spelling triage comment does.
		/// </summary>
		private static IEnumerable<TicketFinding> BuildQualityFindings(
			List<IssueTracking.IssueRecord> qualityRows, TicketGenerationConfig ticketConfig)
		{
			var today = DateTime.UtcNow.Date;
			foreach (var r in qualityRows)
			{
				if (!r.Status.Equals(StatusPending, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var escalated =
					DateTime.TryParse(r.DateFound, CultureInfo.InvariantCulture, DateTimeStyles.None, out var found)
					&& found.Date.AddDays(ticketConfig.OverdueAfterDays) <= today;

				yield return new TicketFinding(
					Type: QualityType,
					Url: r.Url,
					PrimaryText: r.Word,
					Status: escalated ? StatusOverdue : StatusPending,
					Context: r.Excerpt,
					SourceLabel: r.SourceLabel,
					Comment: r.Comment,
					Language: r.Language);
			}
		}

		/// <summary>
		/// Parses 07-404-sources.log ({deadTarget}|{sourcePage}) into findings
		/// keyed on the SOURCE page (column 2) — the page whose editor owns the
		/// broken link. PrimaryText is the dead target (column 1). Every row is
		/// actionable (a 404 has no triage history yet), so Status = new.
		/// </summary>
		private static IEnumerable<TicketFinding> BuildBrokenLinkFindings(string logPath)
		{
			if (!File.Exists(logPath))
			{
				yield break;
			}

			foreach (var line in File.ReadAllLines(logPath, Encoding.UTF8))
			{
				if (line.Length == 0)
				{
					continue;
				}

				var idx = line.IndexOf('|');
				var deadTarget = idx >= 0 ? line[..idx].Trim() : line.Trim();
				var sourcePage = idx >= 0 ? line[(idx + 1)..].Trim() : string.Empty;
				if (string.IsNullOrEmpty(sourcePage) || string.IsNullOrEmpty(deadTarget))
				{
					continue;
				}

				yield return new TicketFinding(
					Type: BrokenLinkType,
					Url: sourcePage,
					PrimaryText: deadTarget,
					Status: StatusNew);
			}
		}

		private static string ResolveSectionIntro(
			TicketGenerationConfig cfg, string type, bool plural)
		{
			if (plural)
			{
				var p = cfg.TicketSectionIntros
					.FirstOrDefault(s => s.Type.Equals(type + "_PLURAL", StringComparison.OrdinalIgnoreCase));
				if (p != null && !string.IsNullOrEmpty(p.Text))
				{
					return p.Text;
				}
			}
			var hit = cfg.TicketSectionIntros
				.FirstOrDefault(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
			if (hit != null && !string.IsNullOrEmpty(hit.Text))
			{
				return hit.Text;
			}
			// Fallback: the type's configured label, else the raw token.
			var label = cfg.TicketIssueTypes
				.FirstOrDefault(t => t.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Label;
			return string.IsNullOrEmpty(label) ? type : label;
		}

		/// <summary>
		/// <summary>
		/// Returns the host of the first <paramref name="allowedSubdomains"/> base
		/// URL whose host equals the host of <paramref name="url"/>, or
		/// empty if none match / inputs are unusable. Compares host only (scheme
		/// and path ignored). Used to label the Herkunft of a page that has no
		/// CMS-CSV row but is in scope via an allowed subdomain. Defensive: never
		/// throws on a malformed URL — a ticket must always render.
		/// </summary>
		private static string MatchAllowedSubdomainHost(string url, List<string>? allowedSubdomains)
		{
			if (allowedSubdomains == null || allowedSubdomains.Count == 0)
			{
				return string.Empty;
			}

			if (!Uri.TryCreate(url, UriKind.Absolute, out var pageUri))
			{
				return string.Empty;
			}

			foreach (var entry in allowedSubdomains)
			{
				if (Uri.TryCreate(entry, UriKind.Absolute, out var subUri)
					&& subUri.Host.Equals(pageUri.Host, StringComparison.OrdinalIgnoreCase))
				{
					return subUri.Host;
				}
			}
			return string.Empty;
		}

		/// <summary>
		/// Internal entry point so the startup ticket preview
		/// (<see cref="SpellMetadataLookup"/>) renders the shell through the exact
		/// same logic as a real run, rather than duplicating it. Returns the
		/// rendered, collapsed shell (no trailing newline).
		/// </summary>
		internal static string RenderShellForPreview(
			string shellTemplate, string url, SpellMetadataLookup.TicketMetadata meta)
			=> RenderShell(shellTemplate, url, meta);

		/// Renders the page-level provenance shell once per ticket.
		/// Substitution is field-independent: each placeholder's emptiness is its
		/// own fact, with NO inference between fields (a page can be "vererbt"
		/// with no module, or have a module with any Herkunft — the two are
		/// unrelated). Collapse rules, applied per placeholder:
		///   • {Package} empty  → the immediately-preceding literal run up to and
		///       including its " : " label is removed (e.g. " - Modul: " before
		///       an empty {Package} disappears, leaving "Herkunft: vererbt").
		///   • {SpecialInfo} empty → its leading separator space is removed so no
		///       trailing space dangles after the module/Herkunft text.
		///   • {CmsLink} empty  → the whole line carrying it is dropped (with a
		///       directly-preceding blank line), since "CMS: " alone is noise.
		/// Any line left blank after substitution collapses with its neighbours
		/// (max one blank run). The operator template stays simple — it writes
		/// the natural "Herkunft: {Location} - Modul: {Package} {SpecialInfo}"
		/// and "CMS: {CmsLink}"; the renderer handles the sparse cases.
		/// </summary>
		private static string RenderShell(
			string shellTemplate, string url, SpellMetadataLookup.TicketMetadata meta)
		{
			if (string.IsNullOrEmpty(shellTemplate))
			{
				return string.Empty;
			}

			var rawLines = shellTemplate.Split("[LF]");
			var kept = new List<string>();
			foreach (var rawLine in rawLines)
			{
				var hadCmsLink = rawLine.Contains("{CmsLink}");

				// Drop a line whose only value-bearing placeholder is an empty
				// {CmsLink} (the "CMS: {CmsLink}" line for a page with no link).
				if (hadCmsLink && string.IsNullOrEmpty(meta.CmsLink))
				{
					while (kept.Count > 0 && kept[^1].Trim().Length == 0)
					{
						kept.RemoveAt(kept.Count - 1);
					}
					continue;
				}

				var line = rawLine;

				// {Package} empty → remove the literal label run immediately
				// preceding the placeholder (" - Modul: " → gone). We strip from
				// the start of that separator run through the placeholder token,
				// keying ONLY on Package emptiness — never on Herkunft.
				if (string.IsNullOrEmpty(meta.Package) && line.Contains("{Package}"))
				{
					line = RemoveEmptyLabeledFragment(line, "{Package}");
				}

				// {SpecialInfo} empty → drop a single separator space before it.
				if (string.IsNullOrEmpty(meta.SpecialInfo) && line.Contains("{SpecialInfo}"))
				{
					line = line.Replace(" {SpecialInfo}", "{SpecialInfo}");
				}

				var rendered = line
					.Replace("{Url}", url)
					.Replace("{Location}", meta.Location)
					.Replace("{Package}", meta.Package)
					.Replace("{CmsLink}", meta.CmsLink)
					.Replace("{SpecialInfo}", meta.SpecialInfo)
					.TrimEnd();

				kept.Add(rendered);
			}

			// Collapse runs of 2+ blank lines to a single blank.
			var outLines = new List<string>();
			bool prevBlank = false;
			foreach (var l in kept)
			{
				bool blank = l.Trim().Length == 0;
				if (blank && prevBlank)
				{
					continue;
				}
				prevBlank = blank;
				outLines.Add(l);
			}

			return string.Join(Environment.NewLine, outLines).TrimEnd();
		}

		/// <summary>
		/// Removes the labelled fragment ending in <paramref name="placeholder"/>
		/// when that placeholder is empty: scans back from the placeholder to the
		/// nearest preceding separator (" - ") and strips from there through the
		/// placeholder, so " - Modul: {Package}" → "". If no preceding " - "
		/// separator exists the placeholder is simply removed (leaving the rest of
		/// the line intact). Keyed purely on the placeholder; no field inference.
		/// </summary>
		private static string RemoveEmptyLabeledFragment(string line, string placeholder)
		{
			int ph = line.IndexOf(placeholder, StringComparison.Ordinal);
			if (ph < 0)
			{
				return line;
			}

			int sep = line.LastIndexOf(" - ", ph, StringComparison.Ordinal);
			int start = sep >= 0 ? sep : ph;
			return line[..start] + line[(ph + placeholder.Length)..];
		}

		/// <summary>
		/// Appends one URL's ticket: action zone (status advice, fenced) → shell
		/// (provenance once) → worst-first per-type sections. Spelling sections
		/// reuse the earlier bullet rendering; 404 sections list the dead
		/// target paths.
		/// </summary>
		private static void AppendUrlBlock(
			StringBuilder sb,
			string url,
			List<TicketFinding> findings,
			TicketGenerationConfig ticketConfig,
			Func<string, SpellMetadataLookup.TicketMetadata> metadataLookup,
			Dictionary<string, int> orderIndex,
			List<string>? allowedSubdomains = null)
		{
			var meta = metadataLookup(url);

			// When the metadata lookup found no row (empty Location) but the page
			// is from an explicitly-allowed subdomain, surface that as the
			// Herkunft value instead of an empty label — it tells the operator the
			// page is in scope via UrlSubdomainsAllowed, not missing from the CSV
			// by mistake. Keyed purely on host match; no site-specific literals.
			if (string.IsNullOrEmpty(meta.Location))
			{
				var sub = MatchAllowedSubdomainHost(url, allowedSubdomains);
				if (!string.IsNullOrEmpty(sub))
				{
					meta = meta with { Location = sub };
				}
			}

			int overdueCount = findings.Count(f => f.Status == StatusOverdue);
			int totalCount = findings.Count;
			// "Current" = actionable but not yet escalated: 404 (new) + ticketed
			// spelling (pending). {NewCount} keeps its placeholder meaning = current.
			int currentCount = totalCount - overdueCount;

			// ── Action zone (operator-only; never copied into the ticket) ──
			// No fence here: '#' is reserved as the BETWEEN-ticket separator
			// (emitted by the caller), so the action advice renders as plain
			// lines above the '=' ticket frame, not boxed in its own '#' rule.
			if (!string.IsNullOrEmpty(ticketConfig.ActionAdviceHeader))
			{
				string Fill(string s) => s
					.Replace("{NewCount}", currentCount.ToString())
					.Replace("{OverdueCount}", overdueCount.ToString())
					.Replace("{ShownCount}", totalCount.ToString())
					.Replace("{TotalCount}", totalCount.ToString());

				sb.AppendLine(ticketConfig.ActionAdviceHeader);
				var advice = overdueCount > 0 ? ticketConfig.ActionAdviceOverdue : ticketConfig.ActionAdviceNew;
				if (!string.IsNullOrEmpty(advice))
				{
					sb.AppendLine(Fill(advice));
				}
			}

			// ── Ticket body (copy-paste) ──
			sb.AppendLine(Divider.DoubleLine);
			sb.AppendLine($"URL: {url}");
			sb.AppendLine(Divider.Underscore);

			var headline = RenderHeadline(url, ticketConfig, findings, orderIndex);
			if (!string.IsNullOrEmpty(headline))
			{
				sb.AppendLine(headline);
				sb.AppendLine(Divider.Underscore);
			}

			var shell = RenderShell(ticketConfig.TicketShellTemplate, url, meta);
			if (!string.IsNullOrEmpty(shell))
			{
				sb.AppendLine(shell);
				sb.AppendLine();
			}

			// Sections worst-first per configured severity order.
			var typesPresent = findings
				.Select(f => f.Type)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(t => orderIndex.TryGetValue(t, out var ix) ? ix : int.MaxValue);

			foreach (var type in typesPresent)
			{
				var ofType = findings.Where(f => f.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
				bool plural = ofType.Count > 1;
				sb.AppendLine(ResolveSectionIntro(ticketConfig, type, plural));
				AppendBullets(sb, type, ofType, ticketConfig);
				sb.AppendLine();
			}
		}

		/// <summary>
		/// Renders the bullet list for one type's findings. Spelling reuses the
		/// exact earlier layout (60-dash framing, "* word [source] — annotation",
		/// indented Context, optional triage Comment under a '+' rule). 404 lists
		/// the dead target as "* {deadTarget}  [{label}]" with no Context line.
		/// </summary>
		private static void AppendBullets(
			StringBuilder sb, string type, List<TicketFinding> findings, TicketGenerationConfig cfg)
		{
			var dash = Divider.Of('-', 60);

			if (type.Equals(SpellingType, StringComparison.OrdinalIgnoreCase)
				|| type.Equals(QualityType, StringComparison.OrdinalIgnoreCase))
			{
				bool isSpelling = type.Equals(SpellingType, StringComparison.OrdinalIgnoreCase);
				var perError = findings.Select(e =>
				{
					var annotation = isSpelling ? ResolveAnnotation(e.Language, cfg) : "";
					// Quality check names can carry a leading "TYPE:" token (composite Word,
					// e.g. "UNWANTED_PATTERN:Security: …"); strip it for display. Spelling words
					// never carry one.
					var primary = isSpelling ? e.PrimaryText : StripTypePrefix(e.PrimaryText);
					// Suppress the [source] tag when it merely repeats the displayed check name
					// (e.g. ADJACENT_ANCHOR [ADJACENT_ANCHOR]).
					var source = !string.IsNullOrEmpty(e.SourceLabel)
						&& !e.SourceLabel.Equals(primary, StringComparison.OrdinalIgnoreCase)
						? $" [{e.SourceLabel}]"
						: "";
					var headLine = string.IsNullOrEmpty(annotation)
						? $"* {primary}{source}"
						: $"* {primary}{source} — {annotation}";

					var (cleanedExcerpt, _) = IssueLogWriter.SanitizeField(e.Context, '\uFFFF');
					var contextLine = string.IsNullOrEmpty(cleanedExcerpt)
						? "  Context: (none)"
						: $"  Context: {cleanedExcerpt}";

					var block = headLine + Environment.NewLine + contextLine;

					var (cleanedComment, _) = IssueLogWriter.SanitizeField(e.Comment, '\uFFFF');
					if (!string.IsNullOrEmpty(cleanedComment))
					{
						block += Environment.NewLine + Divider.Of('+', 30)
							+ Environment.NewLine + cleanedComment;
					}
					return block;
				});

				sb.AppendLine(dash);
				sb.AppendLine(string.Join(Environment.NewLine + dash + Environment.NewLine, perError));
				sb.AppendLine(dash);
				return;
			}

			// Generic (404 and any future type without special rendering): one
			// bullet per finding, dead target as the primary text.
			var label = cfg.TicketIssueTypes
				.FirstOrDefault(t => t.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Label ?? type;
			sb.AppendLine(dash);
			var bullets = findings.Select(f => $"* {f.PrimaryText}  [{label}]");
			sb.AppendLine(string.Join(Environment.NewLine + dash + Environment.NewLine, bullets));
			sb.AppendLine(dash);
		}

		/// <summary>
		/// Renders the headline template with all placeholders substituted.
		/// Empty placeholders collapse their surrounding " - " separators
		/// cleanly so a missing Prefix doesn't leave a leading dash. Order:
		/// substitute → collapse adjacent empty separators → trim trailing
		/// separators.
		///
		/// Placeholders supported:
		///   {Prefix}        — TicketPrefix value
		///   {IssueType}     — worst-two TicketIssueTypes labels present, joined with '/'
		///   {PathIndicator} — URL stripped of domain + query, then shortened
		///                     via PathShortenSegments
		/// </summary>
		internal static string RenderHeadline(
			string url,
			TicketGenerationConfig ticketConfig,
			List<TicketFinding>? findings = null,
			Dictionary<string, int>? orderIndex = null)
		{
			if (string.IsNullOrEmpty(ticketConfig.TicketHeadlineTemplate))
			{
				return string.Empty;
			}

			var pathIndicator = ShortenPath(StripDomainAndQuery(url), ticketConfig.PathShortenSegments);

			// {IssueType} composition: the headline names the WORST TWO
			// finding types present on this URL (ticket-system title fields can't
			// hold more), labelled per TicketIssueTypes and joined with '/'. With
			// no findings (the helper's pure-unit-test path) {IssueType} is empty
			// and the headline's separator-collapse removes the gap.
			string issueType;
			if (findings != null && findings.Count > 0 && ticketConfig.TicketIssueTypes.Count > 0)
			{
				var labelOf = ticketConfig.TicketIssueTypes
					.ToDictionary(t => t.Type, t => t.Label, StringComparer.OrdinalIgnoreCase);
				var worstTwo = findings
					.Select(f => f.Type)
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.OrderBy(t => orderIndex != null && orderIndex.TryGetValue(t, out var ix) ? ix : int.MaxValue)
					.Take(2)
					.Select(t => labelOf.TryGetValue(t, out var lbl) ? lbl : t);
				issueType = string.Join("/", worstTwo);
			}
			else
			{
				issueType = string.Empty;
			}

			var rendered = ticketConfig.TicketHeadlineTemplate
				.Replace("{Prefix}", ticketConfig.TicketPrefix ?? string.Empty)
				.Replace("{IssueType}", issueType)
				.Replace("{PathIndicator}", pathIndicator ?? string.Empty);

			// Collapse runs of empty-around-separator. The template's natural
			// shape is "{A} - {B} - {C}"; when any placeholder resolves to
			// empty the resulting "  -  - " noise should be collapsed. We
			// collapse common patterns: " -  - " → " - ", multi-space → single
			// space. Then trim leading/trailing " - " from empty edge
			// placeholders. The goal: a missing Prefix should not produce a
			// leading "- IssueType - ..." in the headline.
			while (rendered.Contains(" -  - "))
			{
				rendered = rendered.Replace(" -  - ", " - ");
			}

			while (rendered.Contains("  "))
			{
				rendered = rendered.Replace("  ", " ");
			}

			rendered = rendered.Trim();
			// Strip stray leading/trailing separator tokens left by empty
			// edge placeholders (e.g. empty {Prefix} leaves "- IssueType ...").
			while (rendered.StartsWith("- "))
			{
				rendered = rendered[2..];
			}

			while (rendered.EndsWith(" -"))
			{
				rendered = rendered[..^2];
			}

			return rendered;
		}

		/// <summary>
		/// Strips the scheme + domain + query string from a URL, leaving only
		/// the absolute path. Used to build {PathIndicator} for ticket
		/// headlines that span multiple client domains — same path on
		/// different domains is the same logical page for grouping purposes.
		///
		/// "https://www.example.com/de/home/page.html?x=1" → "/de/home/page.html"
		///
		/// Returns the input unchanged (minus query) if it doesn't parse as a
		/// URI — defensive fallback. Ticket-text generation should never crash
		/// on a malformed URL.
		/// </summary>
		internal static string StripDomainAndQuery(string url)
		{
			if (string.IsNullOrEmpty(url))
			{
				return string.Empty;
			}

			if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				return uri.AbsolutePath;
			}

			var qIdx = url.IndexOf('?');
			return qIdx >= 0 ? url[..qIdx] : url;
		}

		/// <summary>
		/// Replaces path segments matching any entry in <paramref name="segments"/>
		/// with "...". Match scope: complete segment between two slashes —
		/// "/foo/" matches but "/foo-bar/" or "/foo.html" do not. Each match
		/// becomes its own "/.../" — consecutive matches do NOT collapse, so
		/// the count of "..." in the output equals the number of segments
		/// dropped (preserves depth as a structural signal).
		///
		/// Pre-validated: ValidatePathShortenSegments rejects entries ≤ 3
		/// chars at config load (interactive halt or silent skip), so by the
		/// time this runs every entry actually shortens.
		///
		/// "/de/home/privatkunden/altersvorsorge/page.html" with
		/// ["privatkunden","altersvorsorge"] → "/de/home/.../.../page.html"
		///
		/// Filename protection emerges naturally from the between-slashes
		/// matching rule: the last segment of a URL has no trailing slash,
		/// so "page.html" cannot match "/page.html/" anywhere in the URL.
		/// </summary>
		internal static string ShortenPath(string path, List<string> segments)
		{
			if (string.IsNullOrEmpty(path))
			{
				return path ?? string.Empty;
			}

			if (segments == null || segments.Count == 0)
			{
				return path;
			}

			// Split on '/' keeping all parts. Leading slash → parts[0] is
			// empty. Trailing slash → last part is empty. Filename without
			// trailing slash → last part is the filename, which we must not
			// shorten (its position alone protects it: it's not surrounded
			// by two slashes).
			var parts = path.Split('/');
			var lastIndex = parts.Length - 1;
			var hasTrailingSlash = lastIndex > 0 && parts[lastIndex].Length == 0;
			var filenameIndex = hasTrailingSlash ? -1 : lastIndex;  // -1 = no filename to protect

			var matchSet = new HashSet<string>(segments, StringComparer.Ordinal);

			for (int i = 1; i < parts.Length; i++)
			{
				if (i == filenameIndex)
				{
					continue;  // never touch filename
				}

				if (parts[i].Length == 0)
				{
					continue;  // empty segment from //
				}

				if (matchSet.Contains(parts[i]))
				{
					parts[i] = "...";
				}
			}
			return string.Join("/", parts);
		}

		/// <summary>
		/// Resolves the annotation for a spelling error based on the language tag.
		/// "de,en" or "all" → AnnotationUnknownInAll (fails every dictionary)
		/// Single non-page language → AnnotationForeignLanguageWord
		/// Single page language → no annotation (straightforward error)
		/// </summary>
		private static string ResolveAnnotation(string language, TicketGenerationConfig config)
		{
			if (string.IsNullOrEmpty(language))
			{
				return "";
			}

			// Fails all dictionaries.
			if (language.Contains(',') || language.Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				return config.AnnotationUnknownInAll;
			}

			// Single language that is not the primary page language (e.g. "en" on a "de" page).
			// We can't know the page language here so we treat any non-"de" single language
			// as a foreign word signal — adjust if your primary language differs.
			if (!language.Equals("de", StringComparison.OrdinalIgnoreCase))
			{
				return config.AnnotationForeignLanguageWord;
			}

			return "";
		}

		/// <summary>
		/// Strips a leading "TYPE:" token from a composite quality check name so the
		/// bullet shows the human-readable part. Only an all-caps/underscore/digit token
		/// immediately followed by ':' is removed (e.g. "UNWANTED_PATTERN:Security: …" →
		/// "Security: …"); names without such a prefix (e.g. "ADJACENT_ANCHOR", or a
		/// description that merely contains a colon) are returned unchanged.
		/// </summary>
		private static string StripTypePrefix(string text)
		{
			int colon = text.IndexOf(':');
			if (colon <= 0)
			{
				return text;
			}

			for (int i = 0; i < colon; i++)
			{
				char c = text[i];
				if (!(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
				{
					return text;
				}
			}

			return text[(colon + 1)..];
		}

	}
}
