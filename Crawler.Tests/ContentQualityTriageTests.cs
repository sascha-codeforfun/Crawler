using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQualityTriage.BuildGroups().
	/// The interactive Run() loop is Console-bound and not tested here.
	/// BuildGroups() is internal — accessible via InternalsVisibleTo.
	/// Uses Logger collection: LookUpUrlForFile calls Logger.LogError when cache is empty.
	/// </summary>
	[Collection("Logger")]
	public class ContentQualityTriageTests : IDisposable
	{
		private readonly string _tempDir;

		public ContentQualityTriageTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"cqt-test-{Guid.NewGuid()}");
			Directory.CreateDirectory(_tempDir);
		}

		public void Dispose()
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private string LogFile(params string[] rows)
		{
			var path = Path.Combine(_tempDir, "10-content-quality-issues.log");
			var lines = new List<string> { "Filename|IssueType|Detail|Context" };
			lines.AddRange(rows);
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private static List<ContentQualityTriage.TriageGroup> Groups(
			string logPath) =>
			ContentQualityTriage.BuildGroups(
				logPath, "10-content-quality-issues.log",
				new ContentQualityConfig(), string.Empty);

		// ── Empty / missing ───────────────────────────────────────────────────

		[Fact]
		public void BuildGroups_NonExistentFile_ReturnsEmpty()
		{
			// BuildGroups checks File.Exists — but it's called after Run() checks.
			// Create an empty log (header only) to test the empty-data path.
			var path = LogFile();
			var groups = Groups(path);
			Assert.Empty(groups);
		}

		[Fact]
		public void BuildGroups_HeaderOnly_ReturnsEmpty()
		{
			var path = LogFile();
			Assert.Empty(Groups(path));
		}

		// ── UNWANTED_PATTERN grouping ─────────────────────────────────────────

		[Fact]
		public void BuildGroups_UnwantedPattern_OneGroupPerPage()
		{
			// Three pages with same pattern → three groups (one per page, not grouped)
			var path = LogFile(
				"page1.html|UNWANTED_PATTERN|pattern: %(|...context1...",
				"page2.html|UNWANTED_PATTERN|pattern: %(|...context2...",
				"page3.html|UNWANTED_PATTERN|pattern: )%|...context3..."
			);
			var groups = Groups(path);
			// Three pages → three groups
			Assert.Equal(3, groups.Count(g => g.DisplayType == "UNWANTED_PATTERN"));
		}

		[Fact]
		public void BuildGroups_UnwantedPattern_CommentReferencesLogFilename()
		{
			var path = LogFile(
				"page1.html|UNWANTED_PATTERN|pattern: %(|...context..."
			);
			var groups = Groups(path);
			var group = groups.First(g => g.DisplayType == "UNWANTED_PATTERN");
			Assert.Contains("10-content-quality-issues.log", group.Comment);
		}

		[Fact]
		public void BuildGroups_UnwantedPattern_WordContainsPattern()
		{
			var path = LogFile(
				"page1.html|UNWANTED_PATTERN|pattern: %(|...context..."
			);
			var groups = Groups(path);
			var group = groups.First(g => g.DisplayType == "UNWANTED_PATTERN");
			Assert.Contains("pattern: %(", group.Word);
		}

		[Fact]
		public void BuildGroups_UnwantedPattern_IsTranslationFalse()
		{
			var path = LogFile(
				"page1.html|UNWANTED_PATTERN|pattern: %(|...context..."
			);
			var groups = Groups(path);
			Assert.False(groups.First().IsTranslation);
		}

		// ── Quote issue grouping ──────────────────────────────────────────────

		[Fact]
		public void BuildGroups_QuoteIssues_SameBlockGroupedTogether()
		{
			// Two quote issues from the same block (same context) → one group
			var path = LogFile(
				"page1.html|QUOTE_SYSTEM_MIX|Multiple systems: German-double, English-double|...excerpt...",
				"page1.html|QUOTE_UNMATCHED|Opener at position 5|...excerpt..."
			);
			var groups = Groups(path).Where(g => g.DisplayType == "QUOTE ISSUES").ToList();
			Assert.Single(groups);
		}

		[Fact]
		public void BuildGroups_QuoteIssues_DifferentBlocks_SeparateGroups()
		{
			// Two quote issues from different blocks (different context) → two groups
			var path = LogFile(
				"page1.html|QUOTE_SYSTEM_MIX|Multiple systems|...block1...",
				"page1.html|QUOTE_UNMATCHED|Opener at position 5|...block2..."
			);
			var groups = Groups(path).Where(g => g.DisplayType == "QUOTE ISSUES").ToList();
			Assert.Equal(2, groups.Count);
		}

		[Fact]
		public void BuildGroups_QuoteIssues_DifferentPages_SeparateGroups()
		{
			var path = LogFile(
				"page1.html|QUOTE_SYSTEM_MIX|Multiple systems|...excerpt...",
				"page2.html|QUOTE_SYSTEM_MIX|Multiple systems|...excerpt..."
			);
			var groups = Groups(path).Where(g => g.DisplayType == "QUOTE ISSUES").ToList();
			Assert.Equal(2, groups.Count);
		}

		[Fact]
		public void BuildGroups_QuoteIssues_DisplayLinesShowHumanNote()
		{
			var path = LogFile(
				"page1.html|QUOTE_SYSTEM_MIX|Multiple quote systems: German-double, English-double|...excerpt...",
				"page1.html|QUOTE_UNMATCHED|Opener at position 5 has no closer|...excerpt...",
				"page1.html|QUOTE_UNMATCHED|Opener at position 9 has no closer|...excerpt..."
			);
			var group = Groups(path).First(g => g.DisplayType == "QUOTE ISSUES");

			var note = group.DisplayLines.Single(l => l.StartsWith("Note : ", System.StringComparison.Ordinal));
			Assert.Contains("mixed German + English double quotes", note);
			Assert.Contains("2 unclosed openers", note);
			// The raw CONSTANT_CASE detector tokens no longer reach the console card.
			Assert.DoesNotContain(group.DisplayLines, l => l.Contains("QUOTE_UNMATCHED"));
			Assert.DoesNotContain(group.DisplayLines, l => l.Contains("QUOTE_SYSTEM_MIX"));
		}

		[Fact]
		public void SynthesizeQuoteNote_NamesSystemsAndCountsUnmatched()
		{
			var entries = new System.Collections.Generic.List<(string, string, string, string)>
			{
				("p.html", "QUOTE_SYSTEM_MIX", "Multiple quote systems: German-double, English-double", ""),
				("p.html", "QUOTE_UNMATCHED", "Opener at 971 has no closer", ""),
				("p.html", "QUOTE_UNMATCHED", "Opener at 981 has no closer", ""),
			};

			Assert.Equal(
				"mixed German + English double quotes; 2 unclosed openers",
				ContentQualityTriage.SynthesizeQuoteNote(entries));
		}

		[Fact]
		public void BuildGroups_QuoteIssues_WordContainsBothTypes()
		{
			var path = LogFile(
				"page1.html|QUOTE_SYSTEM_MIX|Multiple systems|...excerpt...",
				"page1.html|QUOTE_UNMATCHED|Opener at position 5|...excerpt..."
			);
			var group = Groups(path).First(g => g.DisplayType == "QUOTE ISSUES");
			Assert.Contains("QUOTE_SYSTEM_MIX", group.Word);
			Assert.Contains("QUOTE_UNMATCHED", group.Word);
		}

		// ── SPLIT_WORD_ANCHOR grouping ────────────────────────────────────────

		[Fact]
		public void BuildGroups_SplitWordAnchor_OneGroupPerPage()
		{
			var path = LogFile(
				"page1.html|SPLIT_WORD_ANCHOR|Stray char: 'r'|...excerpt...",
				"page2.html|SPLIT_WORD_ANCHOR|Stray char: 'r'|...excerpt..."
			);
			var groups = Groups(path).Where(g => g.DisplayType == "SPLIT_WORD_ANCHOR").ToList();
			Assert.Equal(2, groups.Count);
		}

		[Fact]
		public void BuildGroups_SplitWordAnchor_IsTranslationFalse()
		{
			var path = LogFile(
				"page1.html|SPLIT_WORD_ANCHOR|Stray char: 'r'|...excerpt..."
			);
			var group = Groups(path).First(g => g.DisplayType == "SPLIT_WORD_ANCHOR");
			Assert.False(group.IsTranslation);
		}

		// ── POTENTIAL_TRANSLATION grouping ────────────────────────────────────

		[Fact]
		public void BuildGroups_PotentialTranslation_OneGroupPerPage()
		{
			var path = LogFile(
				"page1.html|POTENTIAL_TRANSLATION|p[#text] (passes en dictionary)|English content here",
				"page1.html|POTENTIAL_TRANSLATION|h2[#text] (passes en dictionary)|More English content",
				"page2.html|POTENTIAL_TRANSLATION|p[#text] (passes en dictionary)|More English content"
			);
			var groups = Groups(path).Where(g => g.DisplayType == "POTENTIAL_TRANSLATION").ToList();
			Assert.Equal(2, groups.Count);
		}

		[Fact]
		public void BuildGroups_PotentialTranslation_IsTranslationTrue()
		{
			var path = LogFile(
				"page1.html|POTENTIAL_TRANSLATION|p[#text] (passes en dictionary)|English content"
			);
			var group = Groups(path).First(g => g.DisplayType == "POTENTIAL_TRANSLATION");
			Assert.True(group.IsTranslation);
		}

		[Fact]
		public void BuildGroups_PotentialTranslation_DisplayLinesContainCount()
		{
			var path = LogFile(
				"page1.html|POTENTIAL_TRANSLATION|p[#text] (passes en)|Content one",
				"page1.html|POTENTIAL_TRANSLATION|h2[#text] (passes en)|Content two",
				"page1.html|POTENTIAL_TRANSLATION|span[#text] (passes en)|Content three"
			);
			var group = Groups(path).First(g => g.DisplayType == "POTENTIAL_TRANSLATION");
			Assert.Contains(group.DisplayLines, l => l.Contains("3 element"));
		}

		// ── LIGATURE grouping ─────────────────────────────────────────────────

		[Fact]
		public void BuildGroups_Ligature_OneGroupPerPage()
		{
			var path = LogFile(
				"page1.html|LIGATURE|U+FB01|...fi ligature...",
				"page2.html|LIGATURE|U+FB02|...fl ligature..."
			);
			var groups = Groups(path).Where(g => g.DisplayType == "LIGATURE").ToList();
			Assert.Equal(2, groups.Count);
		}

		// ── ToIssueRecords (#332: 1:N promotion) ──────────────────────────────

		[Fact]
		public void ToIssueRecords_StatusNew_CorrectFields()
		{
			var path = LogFile(
				"page1.html|SPLIT_WORD_ANCHOR|Stray char: 'r'|...excerpt..."
			);
			var group = Groups(path).First();
			// Single-type group → exactly one record (1:1, unchanged behavior).
			var record = Assert.Single(group.ToIssueRecords("new", string.Empty));
			Assert.Equal("triage", record.Source);
			Assert.Equal("QUALITY", record.Type);
			Assert.Equal("new", record.Status);
			Assert.Equal("SPLIT_WORD_ANCHOR", record.Word);
		}

		[Fact]
		public void ToIssueRecords_UserCommentOverridesBuiltComment()
		{
			var path = LogFile(
				"page1.html|UNWANTED_PATTERN|pattern: %(|...context..."
			);
			var group = Groups(path).First();
			var record = group.ToIssueRecords("wontfix", "my custom comment").First();
			Assert.Equal("my custom comment", record.Comment);
		}

		[Fact]
		public void ToIssueRecords_EmptyUserComment_UsesBuiltComment()
		{
			var path = LogFile(
				"page1.html|UNWANTED_PATTERN|pattern: %(|...context..."
			);
			var group = Groups(path).First();
			var record = group.ToIssueRecords("new", string.Empty).First();
			// Built comment references the log filename and pattern
			Assert.Contains("10-content-quality-issues.log", record.Comment);
			Assert.Contains("pattern: %(", record.Comment);
		}

		// ── Mixed issue types ─────────────────────────────────────────────────

		[Fact]
		public void BuildGroups_MixedTypes_AllGroupsPresent()
		{
			var path = LogFile(
				"page1.html|UNWANTED_PATTERN|pattern: %(|...context...",
				"page2.html|QUOTE_SYSTEM_MIX|Multiple systems|...excerpt...",
				"page3.html|SPLIT_WORD_ANCHOR|Stray char: 'r'|...excerpt...",
				"page4.html|POTENTIAL_TRANSLATION|p[#text] (passes en)|Content",
				"page5.html|LIGATURE|U+FB01|...fi..."
			);
			var groups = Groups(path);
			Assert.Contains(groups, g => g.DisplayType == "UNWANTED_PATTERN");
			Assert.Contains(groups, g => g.DisplayType == "QUOTE ISSUES");
			Assert.Contains(groups, g => g.DisplayType == "SPLIT_WORD_ANCHOR");
			Assert.Contains(groups, g => g.DisplayType == "POTENTIAL_TRANSLATION");
			Assert.Contains(groups, g => g.DisplayType == "LIGATURE");
		}

		// ── #332: quote groups promote one record per per-type key ────────────

		[Fact]
		public void QuoteGroup_MultipleTypesOnePage_PromotesOneRecordPerType()
		{
			// A page-block with SYSTEM_MIX + two UNMATCHED collapses to ONE
			// triage group, but must promote TWO records (one per distinct
			// type) so each matches the detector's per-type key.
			var path = LogFile(
				"p.html|QUOTE_SYSTEM_MIX|Multiple systems|SHARED_BLOCK_CONTEXT",
				"p.html|QUOTE_UNMATCHED|Opener at 971|SHARED_BLOCK_CONTEXT",
				"p.html|QUOTE_UNMATCHED|Opener at 981|SHARED_BLOCK_CONTEXT"
			);
			var group = Assert.Single(Groups(path).Where(g => g.DisplayType == "QUOTE ISSUES"));
			var records = group.ToIssueRecords("pending", string.Empty).ToList();

			// Two DISTINCT types → two records (the duplicate UNMATCHED collapses).
			Assert.Equal(2, records.Count);
			Assert.Contains(records, r => r.Word == "QUOTE_SYSTEM_MIX");
			Assert.Contains(records, r => r.Word == "QUOTE_UNMATCHED");
			Assert.All(records, r => Assert.Equal("pending", r.Status));
		}

		[Fact]
		public void QuoteGroup_PromotedKeys_MatchDetectorPerTypeKeys()
		{
			// The crux of #332: a ticketed quote group's keys must equal the
			// keys the detector auto-promotes per type, so the round-trip
			// suppresses them. Composite "TYPE1+TYPE2" keys would match nothing.
			var path = LogFile(
				"p.html|QUOTE_SYSTEM_MIX|Multiple systems|CTX",
				"p.html|QUOTE_UNMATCHED|Opener at 971|CTX"
			);
			var group = Assert.Single(Groups(path).Where(g => g.DisplayType == "QUOTE ISSUES"));
			var keys = group.TrackingKeys("pending").ToList();

			// Per-type keys, NOT a composite. Url resolution falls back to the
			// filename when the cache isn't loaded (test mode), so match on the
			// type suffix.
			Assert.Equal(2, keys.Count);
			Assert.Contains(keys, k => k.EndsWith("|QUOTE_SYSTEM_MIX"));
			Assert.Contains(keys, k => k.EndsWith("|QUOTE_UNMATCHED"));
			Assert.DoesNotContain(keys, k => k.Contains("+"));   // no composite key
		}

		[Fact]
		public void NonQuoteGroup_PromotesExactlyOneRecord()
		{
			// Single-type groups keep 1:1 promotion — TrackingWords is null, so
			// EffectiveWords falls back to the single Word. Guards against the
			// 1:N change altering non-quote behavior.
			var path = LogFile(
				"p.html|UNWANTED_PATTERN|pattern: %(|ctx"
			);
			var group = Groups(path).First();
			var records = group.ToIssueRecords("pending", string.Empty).ToList();
			Assert.Single(records);
			Assert.Equal("UNWANTED_PATTERN:pattern: %(", records[0].Word);
		}

		// ── ComputeQuoteSpans ──────────────────────────────────────────────────

		[Fact]
		public void ComputeQuoteSpans_MarksEveryQuoteGlyph_AsContextByDefault()
		{
			// Two curly quotes; no triggers supplied → both are context, none trigger.
			var text = "say \u201Chi\u201D ok";
			var spans = ContentQualityTriage.ComputeQuoteSpans(text, []);
			Assert.Equal(2, spans.Count);
			Assert.All(spans, s => Assert.False(s.IsTrigger));
			Assert.All(spans, s => Assert.Equal(1, s.Length));
			Assert.Equal(text.IndexOf('\u201C'), spans[0].Start);
			Assert.Equal(text.IndexOf('\u201D'), spans[1].Start);
		}

		[Fact]
		public void ComputeQuoteSpans_StraightQuote_IsHighlightable_AndPaintsAsTrigger()
		{
			// D056: „Weiter" — German opener (context) + straight ASCII closer, which is
			// the QUOTE_MIXED_KIND offender. The straight quote must be a highlightable
			// glyph and, when supplied as a trigger, paint as the trigger — not stay
			// invisible while only the typographic partner shows.
			var text = "Klicken Sie auf \u201EWeiter\u0022.";
			var straightPos = text.IndexOf('\u0022');
			var spans = ContentQualityTriage.ComputeQuoteSpans(text, new[] { straightPos });

			Assert.Equal(2, spans.Count);                                       // „ and " both highlighted
			var straight = Assert.Single(spans, s => s.Start == straightPos);
			Assert.True(straight.IsTrigger);                                    // offender → red
			var opener = Assert.Single(spans, s => s.Start == text.IndexOf('\u201E'));
			Assert.False(opener.IsTrigger);                                     // context → blue
		}

		[Fact]
		public void ComputeQuoteSpans_MarksOnlyTriggerPosition_AsTrigger()
		{
			// Three U+2019: two possessive apostrophes + the flagged orphan. Only the
			// supplied trigger position is red; the others stay context (blue).
			var text = "users\u2019 and firms\u2019 data\u2019";
			var orphan = text.LastIndexOf('\u2019');
			var spans = ContentQualityTriage.ComputeQuoteSpans(text, new[] { orphan });
			Assert.Equal(3, spans.Count);
			Assert.Single(spans, s => s.IsTrigger);
			Assert.True(spans.Single(s => s.IsTrigger).Start == orphan);
		}

		[Fact]
		public void ComputeQuoteSpans_EmptyText_ReturnsEmpty()
		{
			Assert.Empty(ContentQualityTriage.ComputeQuoteSpans(string.Empty, new[] { 0 }));
		}

		// ── FormatQuoteOffenders (hex evidence line) ──────────────────────────

		[Fact]
		public void FormatQuoteOffenders_SingleOffender_ShowsCodepointAndGlyph()
		{
			// „STOXX”  — German opener + U+201D wrong closer (the STOXX case).
			var text = "(\u201ESTOXX\u201D)";
			var pos = text.IndexOf('\u201D');
			var line = ContentQualityTriage.FormatQuoteOffenders(text, new[] { pos });
			Assert.Equal("U+201D \u201D", line);
		}

		[Fact]
		public void FormatQuoteOffenders_DuplicateOffenders_CollapseWithCount()
		{
			// „123ab"  „55555"  — two straight U+0022 closers (the glossar case);
			// they dedupe to one entry with ×2.
			var text = "\u201E123ab\u0022 \u201E55555\u0022";
			var positions = new List<int>();
			for (int i = 0; i < text.Length; i++)
			{
				if (text[i] == '\u0022')
				{
					positions.Add(i);
				}
			}
			var line = ContentQualityTriage.FormatQuoteOffenders(text, positions);
			Assert.Equal("U+0022 \" \u00D72", line);
		}

		[Fact]
		public void FormatQuoteOffenders_DistinctOffenders_ListedInTextOrder()
		{
			// A U+201D then a U+0022 → two entries, comma-joined, left-to-right in
			// text order (matching the colourer's paint walk), regardless of the
			// order the positions are supplied.
			var text = "a\u201Db\u0022";
			var line = ContentQualityTriage.FormatQuoteOffenders(
				text, new[] { text.IndexOf('\u0022'), text.IndexOf('\u201D') });
			Assert.Equal("U+201D \u201D, U+0022 \"", line);
		}

		[Fact]
		public void FormatQuoteOffenders_SamePositionFlaggedTwice_CountedOnce()
		{
			// A glyph flagged by BOTH SYSTEM_MIX and UNMATCHED arrives as the same
			// position twice. The colourer de-dupes (HashSet) and reds it once; the
			// hex must agree — one glyph, no "×2". Regression: Czech [13] showed
			// "U+201C ×2" against a single red U+201C.
			var text = "a\u201Cb";
			var pos = text.IndexOf('\u201C');
			var line = ContentQualityTriage.FormatQuoteOffenders(text, new[] { pos, pos });
			Assert.Equal("U+201C \u201C", line);
		}

		[Fact]
		public void FormatQuoteOffenders_NoPositions_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, ContentQualityTriage.FormatQuoteOffenders("abc", []));
		}

		[Fact]
		public void FormatQuoteOffenders_OutOfRangePosition_Skipped()
		{
			Assert.Equal(string.Empty, ContentQualityTriage.FormatQuoteOffenders("ab", new[] { 99 }));
		}

		[Fact]
		public void FormatQuoteOffenders_AstralOffender_DecodesSurrogatePair()
		{
			// An offender outside the BMP arrives as a high+low surrogate pair. The
			// formatter must combine the pair into one code point (U+1Fxxx) and emit
			// the two-char glyph, not format the lone high surrogate. \U0001F600 is a
			// stand-in astral code point; the trigger sits on its high surrogate.
			var text = "x\U0001F600y";
			var pos = 1; // the astral code point begins at index 1 (its high surrogate)
			var line = ContentQualityTriage.FormatQuoteOffenders(text, new[] { pos });
			Assert.Equal("U+1F600 \U0001F600", line);
		}

		[Fact]
		public void SynthesizeQuoteNote_DifferingShapeSystems_FallsBackToGenericMix()
		{
			// When the mixed systems do not share a shape suffix (German-double has
			// shape "double"; Heavy has no hyphen, so no shape), the tidy
			// "mixed X + Y <shape> quotes" form does not apply and the note falls
			// back to the generic "mixed quote systems: …" listing.
			var entries = new System.Collections.Generic.List<(string, string, string, string)>
			{
				("p.html", "QUOTE_SYSTEM_MIX", "Multiple quote systems: German-double, Heavy", ""),
			};

			Assert.Equal(
				"mixed quote systems: German-double, Heavy",
				ContentQualityTriage.SynthesizeQuoteNote(entries));
		}

		// ── WORD_COLLISION grouping ───────────────────────────────────────────

		[Fact]
		public void BuildGroups_WordCollision_OneGroupPerPage()
		{
			var path = LogFile(
				"page1.html|WORD_COLLISION|Inline <span> abuts bare text without separator — words merge|<span class=\"h2\">Basismodul</span>Inhalte des Moduls",
				"page1.html|WORD_COLLISION|Inline <span> abuts bare text without separator — words merge|<span class=\"h2\">Wero</span>Lesen Sie hier",
				"page2.html|WORD_COLLISION|Inline <span> abuts bare text without separator — words merge|<span>Foo</span>Bar baz");
			var groups = Groups(path).Where(g => g.DisplayType == "WORD_COLLISION").ToList();
			Assert.Equal(2, groups.Count);
			Assert.All(groups, g => Assert.False(g.IsTranslation));
		}

		[Fact]
		public void BuildGroups_WordCollision_RecoversDocumentOrderFromRankPrefix()
		{
			// D047: the log can hold a page's collisions in reversed
			// (ConcurrentBag/LIFO) order. BuildGroups re-sorts by the detector's
			// "[N]" rank to page order (1-2-3) and takes the first-in-document
			// excerpt, not the log-first one.
			var path = LogFile(
				"p.html|WORD_COLLISION|[2] Inline <span> abuts bare text without separator|<span class=\"h2\">Third</span>Cee",
				"p.html|WORD_COLLISION|[1] Inline <span> abuts bare text without separator|<span class=\"h2\">Second</span>Bee",
				"p.html|WORD_COLLISION|[0] Inline <span> abuts bare text without separator|<span class=\"h2\">First</span>Aaa");
			var g = Groups(path).Single(x => x.DisplayType == "WORD_COLLISION");
			// Excerpt is first-in-document (rank 0), not log-first (rank 2).
			Assert.Contains("First", g.Excerpt);
			// Display lines are in document order.
			Assert.Equal(3, g.DisplayLines.Count);
			Assert.Contains("First", g.DisplayLines[0]);
			Assert.Contains("Second", g.DisplayLines[1]);
			Assert.Contains("Third", g.DisplayLines[2]);
		}

		[Fact]
		public void ComputeWordCollisionSpans_TrailingSeam_ThreeSpansInsideTagTail()
		{
			var html = "<span class=\"h2\">Basismodul</span>Inhalte des Moduls";
			var spans = ContentQualityTriage.ComputeWordCollisionSpans(html);
			Assert.Equal(3, spans.Count);

			var inside = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Inside);
			Assert.Equal("Basismodul", html.Substring(inside.Start, inside.Length));

			var tag = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Tag);
			Assert.Equal("</span>", html.Substring(tag.Start, tag.Length));

			var tail = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Tail);
			Assert.Equal("Inhalte", html.Substring(tail.Start, tail.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_SpaceAfterTag_NoSpans()
		{
			// A space after </span> means a clean boundary, not a collision.
			var spans = ContentQualityTriage.ComputeWordCollisionSpans("<span>Basismodul</span> Inhalte");
			Assert.Empty(spans);
		}

		[Fact]
		public void ComputeWordCollisionSpans_BrSpacerInsideSpan_TextInsideBrSeparate()
		{
			// <span>Foo<br></span>Bar — the <br> spacer abuse inside the span.
			// "Foo" stays Inside (light blue), the <br> becomes its own BrSpacer span.
			var html = "<span>Foo<br></span>Bar baz";
			var spans = ContentQualityTriage.ComputeWordCollisionSpans(html);

			var inside = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Inside);
			Assert.Equal("Foo", html.Substring(inside.Start, inside.Length));

			var br = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.BrSpacer);
			Assert.Equal("<br>", html.Substring(br.Start, br.Length));

			var tag = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Tag);
			Assert.Equal("</span>", html.Substring(tag.Start, tag.Length));

			var tail = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Tail);
			Assert.Equal("Bar", html.Substring(tail.Start, tail.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_AttributedBr_CapturedWhole()
		{
			// Real shape from the bline/workflow chrome: <br class="bterm">.
			var html = "<span>Foo<br class=\"bterm\"></span>Bar";
			var spans = ContentQualityTriage.ComputeWordCollisionSpans(html);
			var br = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.BrSpacer);
			Assert.Equal("<br class=\"bterm\">", html.Substring(br.Start, br.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_NonBrInnerTag_NotCarvedAsBrSpacer()
		{
			// A non-<br> inner tag (<b>) stays within Inside — only <br> is carved out.
			var html = "<span>Foo<b>X</b></span>Bar";
			var spans = ContentQualityTriage.ComputeWordCollisionSpans(html);
			Assert.DoesNotContain(spans, s => s.Kind == ConsoleUi.SplitSpanKind.BrSpacer);
		}

		[Fact]
		public void ComputeWordCollisionSpans_LeadingSeam_ThreeSpansWord1TagWord2()
		{
			// Leading seam: bare "Android" abuts opening <span><sup> whose text starts
			// uppercase ("TM"). The trailing path finds nothing (no </tag> before an
			// uppercase), so the leading fallback paints WORD1 / opening tag(s) / WORD2.
			var html = "Android<span class=\"small\"><sup>TM</sup></span>";
			var spans = ContentQualityTriage.ComputeWordCollisionSpans(html);
			Assert.Equal(3, spans.Count);

			var inside = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Inside);
			Assert.Equal("Android", html.Substring(inside.Start, inside.Length));

			var tag = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Tag);
			Assert.Equal("<span class=\"small\"><sup>", html.Substring(tag.Start, tag.Length));

			var tail = spans.Single(s => s.Kind == ConsoleUi.SplitSpanKind.Tail);
			Assert.Equal("TM", html.Substring(tail.Start, tail.Length));
		}

		[Fact]
		public void ComputeWordCollisionSpans_LeadingSeam_LowercaseAfterTag_NoSpans()
		{
			// Opening tag after a lowercase word, but the inside text is lowercase too —
			// no lower→Upper merge, so nothing is painted.
			var spans = ContentQualityTriage.ComputeWordCollisionSpans("wort<span>lower text");
			Assert.Empty(spans);
		}

		// —— CONTROL_CHARS_IN_CONTENT carding (D106) ———————————

		[Fact]
		public void BuildGroups_ControlChars_OneCardPerFinding()
		{
			var path = LogFile(
				"inv.html|CONTROL_CHARS_IN_CONTENT|Found ZWSP (U+200B) in <title> text|Specimen[INVISIBLE ZERO-WIDTH SPACE U+200B] title",
				"inv.html|CONTROL_CHARS_IN_CONTENT|Found ZWSP (U+200B) in meta[@name=\"description\"] content|A meta[INVISIBLE ZERO-WIDTH SPACE U+200B] desc"
			);
			var cc = Groups(path)
				.Where(g => g.DisplayType == "CONTROL_CHARS_IN_CONTENT")
				.ToList();
			// One card per finding — title and meta surface separately.
			Assert.Equal(2, cc.Count);
			// Word carries the per-finding Detail so each round-trips on its own key.
			Assert.All(cc, g => Assert.StartsWith("CONTROL_CHARS_IN_CONTENT:", g.Word));
			Assert.Contains(cc, g => g.Word.Contains("<title>"));
			Assert.Contains(cc, g => g.Word.Contains("meta"));
		}

		[Fact]
		public void ExtractControlCharMarkers_ReturnsDistinctMarkersInOrder()
		{
			var markers = ContentQualityTriage.ExtractControlCharMarkers(
				"a[INVISIBLE ZERO-WIDTH SPACE U+200B]b[CR]c[INVISIBLE ZERO-WIDTH SPACE U+200B]d");
			Assert.Equal(
				new[] { "[INVISIBLE ZERO-WIDTH SPACE U+200B]", "[CR]" },
				markers);
		}

		// ── SplitControlCharLocation (D111: amber the Detail location token) ──

		[Theory]
		[InlineData("Found ZWSP (U+200B) in <p> text", "Found ZWSP (U+200B) in ", "<p>", " text")]
		[InlineData("Found ZWSP (U+200B) in <h2> text", "Found ZWSP (U+200B) in ", "<h2>", " text")]
		[InlineData("Found ZWSP (U+200B) in <title> text", "Found ZWSP (U+200B) in ", "<title>", " text")]
		[InlineData("Found LF (U+000A) in meta[@name=\"description\"] content", "Found LF (U+000A) in ", "meta[@name=\"description\"]", " content")]
		public void SplitControlCharLocation_KnownShapes_IsolatesToken(
			string detail, string before, string token, string after)
		{
			var (b, t, a) = ContentQualityTriage.SplitControlCharLocation(detail);
			Assert.Equal(before, b);
			Assert.Equal(token, t);
			Assert.Equal(after, a);
		}

		[Theory]
		[InlineData("")]
		[InlineData("no recognisable shape here")]
		[InlineData("Found ZWSP (U+200B) in <p>")] // has \" in \" but no trailing text/content
		public void SplitControlCharLocation_UnrecognisedShape_EmptyToken(string detail)
		{
			var (_, token, _) = ContentQualityTriage.SplitControlCharLocation(detail);
			Assert.Equal(string.Empty, token);
		}

		// ── Decomposition spans / markers (D115) ────────────────────────────

		[Fact]
		public void ComputeDecompositionSpans_TwoLetters_TwoSpansAtBasePositions()
		{
			// f u◌̈ r   k o◌̈ nnen  — base+mark spans at the 'u' (1) and 'o' (6).
			var spans = ContentQualityTriage.ComputeDecompositionSpans("fu\u0308r ko\u0308nnen");
			Assert.Equal(2, spans.Count);
			Assert.Equal((1, 2), (spans[0].Start, spans[0].Length));
			Assert.Equal((6, 2), (spans[1].Start, spans[1].Length));
		}

		[Fact]
		public void ComputeDecompositionSpans_Composed_NoSpans()
		{
			// Precomposed ü is a single char, not base+mark — nothing to light.
			Assert.Empty(ContentQualityTriage.ComputeDecompositionSpans("f\u00FCr"));
		}

		[Fact]
		public void ExtractDecompositionMarkers_FindsCodepointMarkers()
		{
			var markers = ContentQualityTriage.ExtractDecompositionMarkers("f\u00FCr  |  fu[U+0308]r");
			Assert.Equal(new[] { "[U+0308]" }, markers);
		}
	}
}
