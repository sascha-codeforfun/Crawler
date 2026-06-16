using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the #320 ticket-headline helpers in SpellTracking:
	///   * StripDomainAndQuery — URL → absolute path, query stripped.
	///   * ShortenPath          — segments between slashes → "..." per list.
	///   * RenderHeadline       — placeholder substitution with empty-collapse.
	///
	/// Pure helpers — no file I/O, no shared state. Each test builds a minimal
	/// TicketGenerationConfig fixture and asserts the rendered string.
	/// </summary>
	public class TicketHeadlineTests
	{
		// ── StripDomainAndQuery ─────────────────────────────────────────────

		[Fact]
		public void StripDomainAndQuery_AbsoluteUrl_ReturnsPathOnly()
		{
			Assert.Equal("/de/home/page.html",
				TicketRenderer.StripDomainAndQuery("https://www.example.com/de/home/page.html"));
		}

		[Fact]
		public void StripDomainAndQuery_AbsoluteUrlWithQuery_StripsQuery()
		{
			Assert.Equal("/de/home/page.html",
				TicketRenderer.StripDomainAndQuery("https://www.example.com/de/home/page.html?x=1&y=2"));
		}

		[Fact]
		public void StripDomainAndQuery_AbsoluteUrlWithFragment_DropsFragment()
		{
			// Uri.AbsolutePath strips fragments — same canonicalization the
			// rest of the crawler uses.
			Assert.Equal("/de/home/page.html",
				TicketRenderer.StripDomainAndQuery("https://www.example.com/de/home/page.html#section"));
		}

		[Fact]
		public void StripDomainAndQuery_DifferentDomainsSamePath_ProduceSamePath()
		{
			// The cross-domain dedup property: same logical page on two
			// client domains becomes the same {PathIndicator}.
			var a = TicketRenderer.StripDomainAndQuery("https://www.example.com/de/home/page.html");
			var b = TicketRenderer.StripDomainAndQuery("https://www.other-client.de/de/home/page.html");
			Assert.Equal(a, b);
		}

		[Fact]
		public void StripDomainAndQuery_RelativeWithQuery_StripsQueryOnly()
		{
			// Defensive fallback when input isn't a valid absolute URI.
			Assert.Equal("/de/home/page.html",
				TicketRenderer.StripDomainAndQuery("/de/home/page.html?x=1"));
		}

		[Fact]
		public void StripDomainAndQuery_EmptyInput_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, TicketRenderer.StripDomainAndQuery(""));
		}

		// ── ShortenPath ─────────────────────────────────────────────────────

		[Fact]
		public void ShortenPath_NoMatches_ReturnsOriginal()
		{
			var result = TicketRenderer.ShortenPath(
				"/de/home/page.html",
				new List<string> { "privatkunden", "altersvorsorge" });
			Assert.Equal("/de/home/page.html", result);
		}

		[Fact]
		public void ShortenPath_SingleMatch_ReplacesSegment()
		{
			var result = TicketRenderer.ShortenPath(
				"/de/home/privatkunden/page.html",
				new List<string> { "privatkunden" });
			Assert.Equal("/de/home/.../page.html", result);
		}

		[Fact]
		public void ShortenPath_ConsecutiveMatches_StayAsSeparateDots()
		{
			// Path depth is preserved — each match becomes its own "/.../",
			// no collapse. The reader can count "..." occurrences to know how
			// many segments were dropped.
			var result = TicketRenderer.ShortenPath(
				"/de/home/privatkunden/altersvorsorge/page.html",
				new List<string> { "privatkunden", "altersvorsorge" });
			Assert.Equal("/de/home/.../.../page.html", result);
		}

		[Fact]
		public void ShortenPath_FilenameMatchesListEntry_NotShortened()
		{
			// The between-slashes rule protects the filename naturally: it
			// has no trailing slash, so "page.html" cannot match "/page.html/"
			// anywhere in the URL. Operator can list "page.html" if they
			// want; entry is inert.
			var result = TicketRenderer.ShortenPath(
				"/de/home/page.html",
				new List<string> { "page.html" });
			Assert.Equal("/de/home/page.html", result);
		}

		[Fact]
		public void ShortenPath_SegmentEndsLikeFile_NotMatched()
		{
			// "privatkunden" should NOT match "privatkunden.html" or
			// "privatkunden-seite" — the match is on the COMPLETE segment
			// between slashes, not a substring.
			var result = TicketRenderer.ShortenPath(
				"/de/home/privatkunden.html/seite/privatkunden-info",
				new List<string> { "privatkunden" });
			Assert.Equal("/de/home/privatkunden.html/seite/privatkunden-info", result);
		}

		[Fact]
		public void ShortenPath_EmptyList_ReturnsOriginal()
		{
			var result = TicketRenderer.ShortenPath("/de/home/page.html", new List<string>());
			Assert.Equal("/de/home/page.html", result);
		}

		[Fact]
		public void ShortenPath_NullList_ReturnsOriginal()
		{
			var result = TicketRenderer.ShortenPath("/de/home/page.html", null!);
			Assert.Equal("/de/home/page.html", result);
		}

		[Fact]
		public void ShortenPath_EmptyPath_ReturnsEmpty()
		{
			var result = TicketRenderer.ShortenPath("", new List<string> { "anything" });
			Assert.Equal(string.Empty, result);
		}

		[Fact]
		public void ShortenPath_PathWithTrailingSlash_LastSegmentIsEmpty()
		{
			// Trailing slash → last "segment" is the empty string after the
			// slash, not a filename. Segment before the trailing slash is a
			// regular segment and CAN be shortened if listed.
			var result = TicketRenderer.ShortenPath(
				"/de/home/privatkunden/",
				new List<string> { "privatkunden" });
			Assert.Equal("/de/home/.../", result);
		}

		[Fact]
		public void ShortenPath_CaseSensitive_MatchesExactCaseOnly()
		{
			// URLs are case-sensitive per RFC 3986; the match must respect
			// the operator's casing in the list.
			var result = TicketRenderer.ShortenPath(
				"/de/home/PrivatKunden/page.html",
				new List<string> { "privatkunden" });
			Assert.Equal("/de/home/PrivatKunden/page.html", result);
		}

		[Fact]
		public void ShortenPath_RealWorldExample_ShortensAsDesigned()
		{
			// The motivating example from the design discussion.
			var result = TicketRenderer.ShortenPath(
				"/de/home/privatkunden/altersvorsorge/privatrente-vgh/lightboxes/maximale-flexibiilitaet.html",
				new List<string> { "privatkunden", "altersvorsorge" });
			Assert.Equal(
				"/de/home/.../.../privatrente-vgh/lightboxes/maximale-flexibiilitaet.html",
				result);
		}

		// ── RenderHeadline ──────────────────────────────────────────────────

		private static TicketGenerationConfig HeadlineConfig(
			string template = "{Prefix} - {IssueType} - {PathIndicator}",
			string prefix = "WEBSITE",
			string issueType = "SPELLING",
			List<string>? shortenSegments = null)
		{
			// #462: {IssueType} is composed from TicketIssueTypes (worst-two
			// labels). For headline unit tests we register a single type whose
			// Label is the desired text; an empty issueType registers no type, so
			// {IssueType} resolves empty and its separator collapses.
			var issueTypes = string.IsNullOrEmpty(issueType)
				? new List<TicketIssueTypeEntry>()
				: [new TicketIssueTypeEntry { Type = "SPELLING", Label = issueType }];

			return new TicketGenerationConfig
			{
				TicketHeadlineTemplate = template,
				TicketPrefix = prefix,
				TicketIssueTypes = issueTypes,
				PathShortenSegments = shortenSegments ?? new List<string>(),
			};
		}

		// Drives RenderHeadline through the production composition path: a single
		// SPELLING finding so {IssueType} resolves to the configured label (or
		// empty when no type is registered).
		private static string Headline(TicketGenerationConfig cfg, string url)
		{
			var findings = new List<TicketRenderer.TicketFinding>
			{
				new(Type: "SPELLING", Url: url, PrimaryText: "x", Status: "new"),
			};
			var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < cfg.TicketIssueTypes.Count; i++)
			{
				orderIndex[cfg.TicketIssueTypes[i].Type] = i;
			}
			return TicketRenderer.RenderHeadline(url, cfg, findings, orderIndex);
		}

		[Fact]
		public void RenderHeadline_AllPlaceholdersPopulated_RendersCleanly()
		{
			var cfg = HeadlineConfig();
			var result = Headline(cfg, "https://www.example.com/de/home/page.html");
			Assert.Equal("WEBSITE - SPELLING - /de/home/page.html", result);
		}

		[Fact]
		public void RenderHeadline_EmptyPrefix_CollapsesLeadingSeparator()
		{
			var cfg = HeadlineConfig(prefix: "");
			var result = Headline(cfg, "https://www.example.com/de/home/page.html");
			Assert.Equal("SPELLING - /de/home/page.html", result);
		}

		[Fact]
		public void RenderHeadline_EmptyIssueType_CollapsesMiddleSeparator()
		{
			var cfg = HeadlineConfig(issueType: "");
			var result = Headline(cfg, "https://www.example.com/de/home/page.html");
			Assert.Equal("WEBSITE - /de/home/page.html", result);
		}

		[Fact]
		public void RenderHeadline_OnlyPathIndicator_NoStrayDashes()
		{
			var cfg = HeadlineConfig(prefix: "", issueType: "");
			var result = Headline(cfg, "https://www.example.com/de/home/page.html");
			Assert.Equal("/de/home/page.html", result);
		}

		[Fact]
		public void RenderHeadline_EmptyTemplate_ReturnsEmpty()
		{
			var cfg = HeadlineConfig(template: "");
			var result = Headline(cfg, "https://www.example.com/de/home/page.html");
			Assert.Equal(string.Empty, result);
		}

		[Fact]
		public void RenderHeadline_StripsDomainAndQueryFromUrl()
		{
			var cfg = HeadlineConfig();
			var result = Headline(cfg, "https://different-client.de/de/home/page.html?x=1#frag");
			Assert.Equal("WEBSITE - SPELLING - /de/home/page.html", result);
		}

		[Fact]
		public void RenderHeadline_AppliesShortening()
		{
			var cfg = HeadlineConfig(
				shortenSegments: new List<string> { "privatkunden", "altersvorsorge" });
			var result = Headline(cfg, "https://www.example.com/de/home/privatkunden/altersvorsorge/page.html");
			Assert.Equal("WEBSITE - SPELLING - /de/home/.../.../page.html", result);
		}

		[Fact]
		public void RenderHeadline_CustomTemplate_OperatorChoosesShape()
		{
			// Operators on the public repo may want non-default shapes. The
			// template field accepts any string with the documented placeholders.
			var cfg = HeadlineConfig(template: "[{IssueType}] {PathIndicator} ({Prefix})");
			var result = Headline(cfg, "https://www.example.com/de/home/page.html");
			Assert.Equal("[SPELLING] /de/home/page.html (WEBSITE)", result);
		}

		[Fact]
		public void RenderHeadline_LocalizedIssueType_RendersChosenLabel()
		{
			// [#323→#462] The issue label is free-text via TicketIssueTypes, fully operator-controlled.
			// A German shop sets "Schreibfehler"; the headline reflects it
			// verbatim. There is nothing special about the default "SPELLING".
			var cfg = HeadlineConfig(issueType: "Schreibfehler");
			var result = Headline(cfg, "https://www.example.com/de/home/page.html");
			Assert.Equal("WEBSITE - Schreibfehler - /de/home/page.html", result);
		}

		// ── #323: headline placement in WriteTicketText ──────────────────────

		// A fresh actionable ticket: a 'pending' SPELLING row first-seen recently
		// (well inside OverdueAfterDays), so it renders but is not escalated. Date is
		// relative to today so the test never becomes a wall-clock time-bomb.
		private static IssueTracking.IssueRecord NewEntry(string url, string word = "Fehlerr")
			=> new()
			{
				Type = "SPELLING",
				Word = word,
				Url = url,
				Status = "pending",
				DateFound = DateTime.UtcNow.Date.AddDays(-5).ToString("yyyy-MM-dd"),
				Language = "de",
			};

		[Fact]
		public void WriteTicketText_HeadlineBelowUrlBlock_WithUnderscoreDividers()
		{
			// [#325] Per-ticket block layout:
			//   ================================  DoubleLine (top frame)
			//   URL: <url>
			//   ________________________________  Underscore
			//   <headline>
			//   ________________________________  Underscore
			//   <body>
			var tmp = Path.Combine(Path.GetTempPath(), $"tickettext-{Guid.NewGuid():N}.log");
			try
			{
				var ticketConfig = new TicketGenerationConfig
				{
					TicketShellTemplate = "{Url}",
					TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "SPELLING" }],
					TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Error on page:" }],
					TicketHeadlineTemplate = "{Prefix} - {IssueType} - {PathIndicator}",
					TicketPrefix = "WEBSITE",
				};
				var url = "https://www.example.com/de/home/page.html";
				var entries = new List<IssueTracking.IssueRecord> { NewEntry(url) };

				TicketRenderer.WriteTicketText(
					tmp, entries, ticketConfig, null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""));

				var content = File.ReadAllText(tmp);
				var nl = Environment.NewLine;
				var headline = "WEBSITE - SPELLING - /de/home/page.html";

				// Headline appears below the URL line.
				Assert.Contains(headline, content);
				Assert.Contains($"URL: {url}", content);
				Assert.True(
					content.IndexOf(headline, StringComparison.Ordinal)
						> content.IndexOf($"URL: {url}", StringComparison.Ordinal),
					"Headline should render below the URL line, not above it.");

				// DoubleLine frames the top of the block, immediately above URL.
				Assert.Contains($"{Divider.DoubleLine}{nl}URL: {url}", content);

				// URL is followed by an Underscore divider, then the headline,
				// then a second Underscore divider:
				//   URL: <url>
				//   ____________
				//   <headline>
				//   ____________
				Assert.Contains(
					$"URL: {url}{nl}{Divider.Underscore}{nl}{headline}{nl}{Divider.Underscore}",
					content);

				// No DoubleLine appears immediately under the URL anymore — the
				// old #323 layout used '=' there; #325 uses '_'.
				Assert.DoesNotContain($"URL: {url}{nl}{Divider.DoubleLine}", content);
			}
			finally
			{
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
			}
		}

		[Fact]
		public void WriteTicketText_EmptyHeadlineTemplate_UnderscoreUnderUrlStays_NoHeadline()
		{
			// [#325] When the headline template is empty, the headline line and
			// its trailing Underscore both drop, but the Underscore directly
			// under the URL STAYS (visual consistency with headline-enabled
			// blocks). Degraded layout:
			//   ================================  DoubleLine
			//   URL: <url>
			//   ________________________________  Underscore
			//   <body>
			var tmp = Path.Combine(Path.GetTempPath(), $"tickettext-{Guid.NewGuid():N}.log");
			try
			{
				var ticketConfig = new TicketGenerationConfig
				{
					TicketShellTemplate = "{Url}",
					TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "SPELLING" }],
					TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Error on page:" }],
					TicketHeadlineTemplate = "",   // suppressed
				};
				var url = "https://www.example.com/de/home/page.html";
				var entries = new List<IssueTracking.IssueRecord> { NewEntry(url) };

				TicketRenderer.WriteTicketText(
					tmp, entries, ticketConfig, null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""));

				var content = File.ReadAllText(tmp);
				var nl = Environment.NewLine;

				// URL is still followed by exactly one Underscore divider.
				Assert.Contains($"URL: {url}{nl}{Divider.Underscore}", content);

				// No headline placeholder leaked, and no double-Underscore
				// (which would indicate an empty headline still got framed).
				Assert.DoesNotContain($"{Divider.Underscore}{nl}{Divider.Underscore}", content);
				Assert.DoesNotContain("{Prefix}", content);
				Assert.DoesNotContain("{IssueType}", content);
				Assert.DoesNotContain("{PathIndicator}", content);
			}
			finally
			{
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
			}
		}

		[Fact]
		public void WriteTicketText_MultipleErrorsOnePage_SingleHeadlineSingleBlock()
		{
			// [#325] Multiple spelling errors on one page produce ONE block:
			// one DoubleLine/URL/Underscore/headline/Underscore wrapper, and all
			// errors as bullets inside the single body. The headline (which
			// points at the page) appears exactly once regardless of error count.
			var tmp = Path.Combine(Path.GetTempPath(), $"tickettext-{Guid.NewGuid():N}.log");
			try
			{
				var ticketConfig = new TicketGenerationConfig
				{
					TicketShellTemplate = "{Url}",
					TicketIssueTypes = [new TicketIssueTypeEntry { Type = "SPELLING", Label = "SPELLING" }],
					TicketSectionIntros = [new TicketSectionIntro { Type = "SPELLING", Text = "Error on page:" }],
					TicketHeadlineTemplate = "{Prefix} - {IssueType} - {PathIndicator}",
					TicketPrefix = "WEBSITE",
				};
				var url = "https://www.example.com/de/home/page.html";
				var entries = new List<IssueTracking.IssueRecord>
				{
					NewEntry(url, "Fehlerr"),
					NewEntry(url, "Vorsoge"),
					NewEntry(url, "Kontk"),
				};

				TicketRenderer.WriteTicketText(
					tmp, entries, ticketConfig, null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""));

				var content = File.ReadAllText(tmp);
				var headline = "WEBSITE - SPELLING - /de/home/page.html";

				// Headline appears exactly once for the page.
				var firstIdx = content.IndexOf(headline, StringComparison.Ordinal);
				Assert.True(firstIdx >= 0, "headline present");
				Assert.Equal(-1,
					content.IndexOf(headline, firstIdx + headline.Length, StringComparison.Ordinal));

				// Exactly one block-opening DoubleLine for this single page.
				var doubleLineCount =
					content.Split(Divider.DoubleLine).Length - 1;
				// One for the per-ticket block. (No metadata header here because
				// cmsContentList is null → freshness not configured.)
				Assert.Equal(1, doubleLineCount);
			}
			finally
			{
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
			}
		}

		// ── #326: status sections + triage comment ──────────────────────────

		// A SPELLING ledger row. ageDays sets how long ago it was first seen, relative
		// to today: small (default 5) stays within OverdueAfterDays → renders as pending;
		// large (e.g. 400) is past the window → BuildSpellingFindings escalates it to
		// OVERDUE at render time. status other than "pending" (wontfix/fixed/config) is
		// suppressed entirely. Date is relative to today to keep the test time-stable.
		private static IssueTracking.IssueRecord EntryWith(
			string url, string word, string status, string comment, int ageDays = 5)
			=> new()
			{
				Type = "SPELLING",
				Word = word,
				Url = url,
				Status = status,
				DateFound = DateTime.UtcNow.Date.AddDays(-ageDays).ToString("yyyy-MM-dd"),
				Comment = comment,
				Language = "de",
				SourceLabel = "meta[@name=description]",
				Excerpt = "Some context excerpt",
			};

		private static TicketGenerationConfig SectionConfig()
			=> new()
			{
				TicketShellTemplate = "{Url}",
				TicketIssueTypes =
				[
					new TicketIssueTypeEntry { Type = "SPELLING", Label = "SPELLING" },
					new TicketIssueTypeEntry { Type = "QUALITY", Label = "QUALITY" },
				],
				TicketSectionIntros =
				[
					new TicketSectionIntro { Type = "SPELLING", Text = "Error:" },
					new TicketSectionIntro { Type = "QUALITY", Text = "Quality issue:" },
				],
				TicketHeadlineTemplate = "{Prefix} - {IssueType} - {PathIndicator}",
				TicketPrefix = "WEBSITE",
			};

		private static string WriteAndRead(List<IssueTracking.IssueRecord> entries)
		{
			var tmp = Path.Combine(Path.GetTempPath(), $"tickettext-{Guid.NewGuid():N}.log");
			try
			{
				TicketRenderer.WriteTicketText(
					tmp, entries, SectionConfig(), null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""));
				return File.ReadAllText(tmp);
			}
			finally
			{
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
			}
		}

		// A ticketed CQ row: QUALITY, Word = the check name, Excerpt = evidence snippet.
		// ageDays drives render-time escalation exactly like spelling (small = pending,
		// large = overdue). Comment is the pre-built per-type value ([T] never prompts).
		private static IssueTracking.IssueRecord QualityEntry(
			string url, string check, string status, int ageDays = 5, string comment = "", string excerpt = "snippet")
			=> new()
			{
				Type = "QUALITY",
				Word = check,
				Url = url,
				Status = status,
				DateFound = DateTime.UtcNow.Date.AddDays(-ageDays).ToString("yyyy-MM-dd"),
				Comment = comment,
				SourceLabel = check,
				Excerpt = excerpt,
			};

		private static string WriteAndReadQuality(List<IssueTracking.IssueRecord> qualityRows)
		{
			var tmp = Path.Combine(Path.GetTempPath(), $"tickettext-q-{Guid.NewGuid():N}.log");
			try
			{
				TicketRenderer.WriteTicketText(
					tmp, [], SectionConfig(), null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""),
					qualityRows: qualityRows);
				return File.ReadAllText(tmp);
			}
			finally
			{
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
			}
		}

		[Fact]
		public void WriteTicketText_QualityPending_RendersCheckAndContext()
		{
			var content = WriteAndReadQuality(new()
			{
				QualityEntry("https://x/p.html", "ADJACENT_ANCHOR", "pending", excerpt: "<a>And</a><a>roid</a>"),
			});

			Assert.Contains("https://x/p.html", content);
			Assert.Contains("* ADJACENT_ANCHOR", content);
			Assert.Contains("Context: <a>And</a><a>roid</a>", content);
		}

		[Fact]
		public void WriteTicketText_QualityNonPending_NeverAppears()
		{
			// Only ticketed (pending) CQ reaches TicketText; auto 'new' and 'wontfix' do not.
			var content = WriteAndReadQuality(new()
			{
				QualityEntry("https://x/n.html", "ADJACENT_ANCHOR", "new"),
				QualityEntry("https://x/w.html", "ADJACENT_ANCHOR", "wontfix"),
			});

			Assert.Equal(string.Empty, content);
		}

		[Fact]
		public void WriteTicketText_QualityComment_RendersUnderPlusDivider()
		{
			// Pre-built per-type comment (e.g. UNWANTED_PATTERN's pattern reference)
			// renders under the '+' rule, like a spelling triage comment.
			var content = WriteAndReadQuality(new()
			{
				QualityEntry("https://x/u.html", "UNWANTED_PATTERN", "pending",
					comment: "See 10-content-quality-issues.log — pattern '%('"),
			});

			var nl = Environment.NewLine;
			var plusRule = Divider.Of('+', 30);
			Assert.Contains("See 10-content-quality-issues.log — pattern '%('", content);
			Assert.Contains($"{plusRule}{nl}See 10-content-quality-issues.log", content);
		}

		[Fact]
		public void WriteTicketText_SpellingAndQuality_BothRenderOnSamePage()
		{
			var tmp = Path.Combine(Path.GetTempPath(), $"tickettext-sq-{Guid.NewGuid():N}.log");
			try
			{
				TicketRenderer.WriteTicketText(
					tmp,
					new() { EntryWith("https://x/m.html", "Fehlerr", "pending", "", ageDays: 5) },
					SectionConfig(), null,
					_ => new SpellMetadataLookup.TicketMetadata("", "", "", ""),
					qualityRows: new() { QualityEntry("https://x/m.html", "ADJACENT_ANCHOR", "pending") });

				var content = File.ReadAllText(tmp);
				Assert.Contains("* Fehlerr", content);
				Assert.Contains("* ADJACENT_ANCHOR", content);
			}
			finally
			{
				if (File.Exists(tmp))
				{
					File.Delete(tmp);
				}
			}
		}

		[Fact]
		public void WriteTicketText_PendingAndOverdue_RenderAsBlocksWontfixSuppressed()
		{
			// New model: spelling reaches TicketText only as operator-raised 'pending'
			// rows; a pending row past OverdueAfterDays escalates to OVERDUE. wontfix is
			// suppressed. Here a (pending, recent) and c (pending, aged → overdue) each
			// produce a block; b (wontfix) is dropped.
			var content = WriteAndRead(new()
			{
				EntryWith("https://x/a.html", "Aaa", "pending", "",                ageDays: 5),
				EntryWith("https://x/b.html", "Bbb", "wontfix", "deliberate",      ageDays: 5),
				EntryWith("https://x/c.html", "Ccc", "pending", "fix Ccc → Cccc",  ageDays: 400),
			});

			// Both actionable URLs appear; the wontfix URL does not.
			Assert.Contains("https://x/a.html", content);
			Assert.Contains("https://x/c.html", content);
			Assert.DoesNotContain("https://x/b.html", content);

			// Old top-level status section labels are gone.
			Assert.DoesNotContain("NEW —", content);
			Assert.DoesNotContain("PENDING —", content);
			Assert.DoesNotContain("OVERDUE —", content);

			// Two actionable blocks → exactly one '#' separator between them.
			var hashCount = content.Split(Divider.Hash).Length - 1;
			Assert.Equal(1, hashCount);
		}

		[Fact]
		public void WriteTicketText_SingleActionableUrl_NoSeparatorNoStatusLabels()
		{
			var content = WriteAndRead(new()
			{
				EntryWith("https://x/a.html", "Aaa", "pending", "", ageDays: 5),
			});

			Assert.Contains("https://x/a.html", content);
			// No old status-section labels.
			Assert.DoesNotContain("NEW —", content);
			Assert.DoesNotContain("PENDING —", content);
			Assert.DoesNotContain("OVERDUE —", content);
			// Single block → no '#' separator.
			Assert.DoesNotContain(Divider.Hash, content);
		}

		[Fact]
		public void WriteTicketText_OverdueComment_RendersUnderBulletWithPlusDivider()
		{
			// An aged pending row escalates to OVERDUE and carries its triage comment
			// under the short '+' rule (behaviour preserved).
			var content = WriteAndRead(new()
			{
				EntryWith("https://x/page.html", "Anpssung", "pending",
					"Schreibfehler für: Anpssung (de) - richtig wäre: Anpassung", ageDays: 400),
			});

			var nl = Environment.NewLine;
			var plusRule = Divider.Of('+', 30);

			Assert.Contains("Schreibfehler für: Anpssung (de) - richtig wäre: Anpassung", content);
			Assert.Contains($"{plusRule}{nl}Schreibfehler für: Anpssung", content);
		}

		[Fact]
		public void WriteTicketText_PendingNoComment_NoPlusDivider()
		{
			var content = WriteAndRead(new()
			{
				EntryWith("https://x/page.html", "Aaa", "pending", "", ageDays: 5),
			});

			// No triage comment → no '+' divider.
			Assert.DoesNotContain(Divider.Of('+', 30), content);
		}

		[Fact]
		public void WriteTicketText_NonPendingStatuses_NeverAppear()
		{
			// Only 'pending' is actionable. wontfix/fixed/config never appear.
			var content = WriteAndRead(new()
			{
				EntryWith("https://x/f.html", "Fff", "fixed",   "was fixed",  ageDays: 5),
				EntryWith("https://x/w.html", "Www", "wontfix", "deliberate", ageDays: 5),
				EntryWith("https://x/g.html", "Ggg", "config",  "attr gap",   ageDays: 5),
			});

			// No actionable entries → empty file.
			Assert.Equal(string.Empty, content);
		}

		[Fact]
		public void WriteTicketText_QualityTag_SuppressedWhenSourceEqualsCheck()
		{
			// QualityEntry sets SourceLabel = check, so the bullet would read
			// "* ADJACENT_ANCHOR [ADJACENT_ANCHOR]" — the redundant tag is suppressed.
			var content = WriteAndReadQuality(new()
			{
				QualityEntry("https://x/p.html", "ADJACENT_ANCHOR", "pending"),
			});

			Assert.Contains("* ADJACENT_ANCHOR", content);
			Assert.DoesNotContain("[ADJACENT_ANCHOR]", content);
		}

		[Fact]
		public void WriteTicketText_QualityCompositeWord_TypePrefixStripped()
		{
			// A composite check name carries a leading "TYPE:" token; it is stripped for
			// display, while the type tag (a distinct SourceLabel) is kept.
			var content = WriteAndReadQuality(new()
			{
				new IssueTracking.IssueRecord
				{
					Type = "QUALITY",
					Word = "UNWANTED_PATTERN:Security: CMS-Parameter-Leak — pattern: %(",
					Url = "https://x/u.html",
					Status = "pending",
					DateFound = DateTime.UtcNow.Date.AddDays(-5).ToString("yyyy-MM-dd"),
					SourceLabel = "UNWANTED_PATTERN",
					Excerpt = "snippet",
				},
			});

			Assert.Contains("* Security: CMS-Parameter-Leak — pattern: %(", content);
			Assert.DoesNotContain("* UNWANTED_PATTERN:", content);
			Assert.Contains("[UNWANTED_PATTERN]", content);
		}
	}
}
