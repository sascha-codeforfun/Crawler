using HtmlAgilityPack;
using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQuality's CMS template authoring defects checks, introduced
	/// in fileset #291:
	///   - EMBEDDED_BOM_IN_BODY: UTF-8 BOM bytes (EF BB BF) found at any position
	///     other than file start. Leading BOM at offset 0 is legitimate; everything
	///     else is build-pipeline residue.
	///   - INVISIBLE_CHAR_IN_BODY: zero-widths, bidi controls, line/paragraph
	///     separators, C0/C1 controls in body text whose parent element is NOT
	///     in ContentQualityBlockElements (editor-prose tags).
	///
	/// Tests cover the four supporting helpers (FindUtf8BomOffsets,
	/// IsArchitectClassInvisible, NameArchitectInvisible, IsInsideArchitectScopeSkipAncestor,
	/// RenderTextWithInvisibleMarkers, BuildBomExcerpt) and the integrated
	/// CheckCmsTemplateAuthoringDefects entry point.
	/// </summary>
	public class CmsTemplateAuthoringDefectsTests
	{
		private static ContentQualityConfig MakeConfig() => new()
		{
			CheckCmsTemplateAuthoringDefects = true,
		};

		// ── FindUtf8BomOffsets ────────────────────────────────────────────

		[Fact]
		public void FindUtf8BomOffsets_NoBom_ReturnsEmpty()
		{
			var bytes = Encoding.ASCII.GetBytes("<html><body>hello</body></html>");

			var result = ContentQuality.FindUtf8BomOffsets(bytes);

			Assert.Empty(result);
		}

		[Fact]
		public void FindUtf8BomOffsets_LeadingBomOnly_ReturnsOffsetZero()
		{
			var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<', (byte)'a', (byte)'>' };

			var result = ContentQuality.FindUtf8BomOffsets(bytes);

			Assert.Single(result);
			Assert.Equal(0, result[0]);
		}

		[Fact]
		public void FindUtf8BomOffsets_EmbeddedBomNoLeading_ReturnsEmbeddedOffset()
		{
			// "<a>" + BOM + "<b>"
			var prefix = Encoding.ASCII.GetBytes("<a>");
			var bom = new byte[] { 0xEF, 0xBB, 0xBF };
			var suffix = Encoding.ASCII.GetBytes("<b>");
			var bytes = prefix.Concat(bom).Concat(suffix).ToArray();

			var result = ContentQuality.FindUtf8BomOffsets(bytes);

			Assert.Single(result);
			Assert.Equal(3, result[0]);
		}

		[Fact]
		public void FindUtf8BomOffsets_LeadingAndEmbedded_ReturnsBothOffsets()
		{
			var leading = new byte[] { 0xEF, 0xBB, 0xBF };
			var middle = Encoding.ASCII.GetBytes("<a>");
			var embedded = new byte[] { 0xEF, 0xBB, 0xBF };
			var bytes = leading.Concat(middle).Concat(embedded).Concat(Encoding.ASCII.GetBytes("<b>")).ToArray();

			var result = ContentQuality.FindUtf8BomOffsets(bytes);

			Assert.Equal(2, result.Count);
			Assert.Equal(0, result[0]);
			Assert.Equal(6, result[1]);
		}

		[Fact]
		public void FindUtf8BomOffsets_MultipleEmbedded_ConcatenationPattern()
		{
			// Simulates concatenation of 3 BOM-prefixed template fragments
			// without stripping: BOM + "<a>" + BOM + "<b>" + BOM + "<c>"
			var bom = new byte[] { 0xEF, 0xBB, 0xBF };
			var bytes = bom
				.Concat(Encoding.ASCII.GetBytes("<a>"))
				.Concat(bom)
				.Concat(Encoding.ASCII.GetBytes("<b>"))
				.Concat(bom)
				.Concat(Encoding.ASCII.GetBytes("<c>"))
				.ToArray();

			var result = ContentQuality.FindUtf8BomOffsets(bytes);

			Assert.Equal(3, result.Count);
		}

		[Fact]
		public void FindUtf8BomOffsets_ShortByteArray_ReturnsEmpty()
		{
			Assert.Empty(ContentQuality.FindUtf8BomOffsets([]));
			Assert.Empty(ContentQuality.FindUtf8BomOffsets([0xEF]));
			Assert.Empty(ContentQuality.FindUtf8BomOffsets([0xEF, 0xBB]));
		}

		[Fact]
		public void FindUtf8BomOffsets_NearBoundary_AvoidsOffByOne()
		{
			// BOM at the very end of the buffer.
			var bytes = Encoding.ASCII.GetBytes("xyz").Concat(new byte[] { 0xEF, 0xBB, 0xBF }).ToArray();

			var result = ContentQuality.FindUtf8BomOffsets(bytes);

			Assert.Single(result);
			Assert.Equal(3, result[0]);
		}

		// ── IsArchitectClassInvisible ────────────────────────────────────

		[Fact]
		public void IsArchitectClassInvisible_NormalChars_ReturnFalse()
		{
			Assert.False(ContentQuality.IsArchitectClassInvisible('a'));
			Assert.False(ContentQuality.IsArchitectClassInvisible('ä'));
			Assert.False(ContentQuality.IsArchitectClassInvisible('€'));
		}

		[Fact]
		public void IsArchitectClassInvisible_NormalWhitespace_ReturnFalse()
		{
			Assert.False(ContentQuality.IsArchitectClassInvisible('\r'));
			Assert.False(ContentQuality.IsArchitectClassInvisible('\n'));
			Assert.False(ContentQuality.IsArchitectClassInvisible('\t'));
			Assert.False(ContentQuality.IsArchitectClassInvisible(' '));
		}

		[Theory]
		[InlineData('\u200B')] // ZWSP
		[InlineData('\u200C')] // ZWNJ
		[InlineData('\u200D')] // ZWJ
		[InlineData('\u2060')] // WJ
		[InlineData('\uFEFF')] // BOM / ZWNBSP
		[InlineData('\u2028')] // LINE SEPARATOR
		[InlineData('\u2029')] // PARAGRAPH SEPARATOR
		[InlineData('\u202A')] // LRE (bidi)
		[InlineData('\u202E')] // RLO (bidi)
		[InlineData('\u2066')] // LRI (bidi isolate)
		[InlineData('\u2069')] // PDI (bidi isolate)
		[InlineData('\u0000')] // NUL (C0)
		[InlineData('\u0085')] // NEL (C1)
		public void IsArchitectClassInvisible_KnownInvisibles_ReturnTrue(char ch)
		{
			Assert.True(ContentQuality.IsArchitectClassInvisible(ch));
		}

		// ── NameArchitectInvisible ────────────────────────────────────────

		[Theory]
		[InlineData('\u200B', "ZWSP (U+200B)")]
		[InlineData('\u200C', "ZWNJ (U+200C)")]
		[InlineData('\uFEFF', "ZWNBSP/BOM (U+FEFF)")]
		[InlineData('\u2028', "LINE SEPARATOR (U+2028)")]
		public void NameArchitectInvisible_ProducesExpectedName(char ch, string expected)
		{
			Assert.Equal(expected, ContentQuality.NameArchitectInvisible(ch));
		}

		// ── IsInsideArchitectScopeSkipAncestor ────────────────────────────

		[Fact]
		public void IsInsideArchitectScopeSkipAncestor_BodyTextNode_ReturnsFalse()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html><body><div>text</div></body></html>");
			var div = doc.DocumentNode.SelectSingleNode("//div");

			Assert.False(ContentQuality.IsInsideArchitectScopeSkipAncestor(div));
		}

		[Fact]
		public void IsInsideArchitectScopeSkipAncestor_TitleNode_ReturnsTrue()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html><head><title>foo</title></head></html>");
			var title = doc.DocumentNode.SelectSingleNode("//title");

			Assert.True(ContentQuality.IsInsideArchitectScopeSkipAncestor(title));
		}

		[Fact]
		public void IsInsideArchitectScopeSkipAncestor_ScriptDescendant_ReturnsTrue()
		{
			var doc = new HtmlDocument();
			doc.LoadHtml("<html><body><script>var x;</script></body></html>");
			var script = doc.DocumentNode.SelectSingleNode("//script");

			Assert.True(ContentQuality.IsInsideArchitectScopeSkipAncestor(script));
		}

		// ── RenderTextWithInvisibleMarkers ────────────────────────────────

		[Fact]
		public void RenderTextWithInvisibleMarkers_NoInvisibles_ReturnsTrimmedOriginal()
		{
			var result = ContentQuality.RenderTextWithInvisibleMarkers("  hello world  ");

			Assert.Equal("hello world", result);
		}

		[Fact]
		public void RenderTextWithInvisibleMarkers_WithZwsp_RendersMarker()
		{
			var result = ContentQuality.RenderTextWithInvisibleMarkers("hello\u200Bworld");

			Assert.Equal("hello[ZWSP (U+200B)]world", result);
		}

		[Fact]
		public void RenderTextWithInvisibleMarkers_LongText_GetsTruncated()
		{
			var longText = new string('a', 300);

			var result = ContentQuality.RenderTextWithInvisibleMarkers(longText);

			Assert.True(result.Length <= 200);
			Assert.EndsWith("…", result);
		}

		// ── CheckCmsTemplateAuthoringDefects integration ─────────────────

		[Fact]
		public void Check_NoDefects_ReturnsEmpty()
		{
			var html = "<html><body><p>Clean content.</p></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			Assert.Empty(issues);
		}

		[Fact]
		public void Check_LeadingBomOnly_NotFlagged()
		{
			// Legitimate UTF-8 signature; not a defect.
			var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF };
			var content = Encoding.UTF8.GetBytes("<html><body><p>clean</p></body></html>");
			var bytes = bomBytes.Concat(content).ToArray();
			var html = Encoding.UTF8.GetString(bytes);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "EMBEDDED_BOM_IN_BODY");
		}

		[Fact]
		public void Check_EmbeddedBom_FlagsAsArchitectDefect()
		{
			// Two embedded BOMs simulating concatenation pattern.
			var bom = new byte[] { 0xEF, 0xBB, 0xBF };
			var bytes = Encoding.UTF8.GetBytes("<html><body><div>a</div>")
				.Concat(bom)
				.Concat(Encoding.UTF8.GetBytes("<div>b</div>"))
				.Concat(bom)
				.Concat(Encoding.UTF8.GetBytes("<div>c</div></body></html>"))
				.ToArray();
			var html = Encoding.UTF8.GetString(bytes);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			var bomIssue = Assert.Single(issues, i => i.IssueType == "EMBEDDED_BOM_IN_BODY");
			Assert.Contains("2 embedded", bomIssue.Detail);
			Assert.Contains("no leading BOM", bomIssue.Detail);
		}

		[Fact]
		public void Check_EmbeddedBom_WithLeadingBom_FlagsEmbeddedOnly()
		{
			var bom = new byte[] { 0xEF, 0xBB, 0xBF };
			var bytes = bom
				.Concat(Encoding.UTF8.GetBytes("<html><body><div>a</div>"))
				.Concat(bom)
				.Concat(Encoding.UTF8.GetBytes("<div>b</div></body></html>"))
				.ToArray();
			var html = Encoding.UTF8.GetString(bytes);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			var bomIssue = Assert.Single(issues, i => i.IssueType == "EMBEDDED_BOM_IN_BODY");
			Assert.Contains("1 embedded", bomIssue.Detail);
			Assert.Contains("legitimate leading BOM", bomIssue.Detail);
		}

		[Fact]
		public void Check_ZwspInDiv_FlagsArchitectInvisible()
		{
			// ZWSP inside <div> — parent is a container (not in block elements).
			var html = "<html><body><div>hello\u200Bworld</div></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, config)
				.ToList();

			var invIssue = Assert.Single(issues, i => i.IssueType == "INVISIBLE_CHAR_IN_BODY");
			Assert.Contains("ZWSP", invIssue.Detail);
			Assert.Contains("1 position", invIssue.Detail);
		}

		[Fact]
		public void Check_ZwspInP_NotFlagged_BecauseEditorScope()
		{
			// ZWSP inside <p> — editor scope, must NOT be flagged by architect check.
			var html = "<html><body><p>hello\u200Bworld</p></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, config)
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "INVISIBLE_CHAR_IN_BODY");
		}

		[Fact]
		public void Check_ZwspInLi_NotFlagged_BecauseEditorScope()
		{
			var html = "<html><body><ul><li>item\u200Btext</li></ul></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, config)
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "INVISIBLE_CHAR_IN_BODY");
		}

		[Fact]
		public void Check_MultipleZwspSameDiv_AggregatedAsOneFinding()
		{
			var html = "<html><body><div>a\u200Bb\u200Bc\u200Bd</div></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, config)
				.ToList();

			var invIssue = Assert.Single(issues, i => i.IssueType == "INVISIBLE_CHAR_IN_BODY");
			Assert.Contains("3 position", invIssue.Detail);
		}

		[Fact]
		public void Check_DifferentCodepoints_EachGetsOwnFinding()
		{
			var html = "<html><body><div>a\u200Bb\u200Cc</div></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, config)
				.ToList();

			var invIssues = issues.Where(i => i.IssueType == "INVISIBLE_CHAR_IN_BODY").ToList();
			Assert.Equal(2, invIssues.Count);
		}

		[Fact]
		public void Check_InvisibleInTitle_NotFlagged_BecauseHeadScope()
		{
			// Architect check skips head subtree — title with weird char is editor scope
			// (covered by CONTROL_CHARS_IN_CONTENT, not this check).
			var html = "<html><head><title>Hello\u200BWorld</title></head><body></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, config)
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "INVISIBLE_CHAR_IN_BODY");
		}

		[Fact]
		public void Check_InvisibleInScript_NotFlagged()
		{
			var html = "<html><body><script>var x = '\u200B';</script></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, config)
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "INVISIBLE_CHAR_IN_BODY");
		}

		// ── Real-world shape from the analysis that motivated #291 ─────────

		[Fact]
		public void RealWorld_BomCase()
		{
			// The case that surfaced the architect-defect class:
			// <div class="foo1 foo2">[BOM] <img.../></div>
			// BOM appears in body but the parser sees a text-node containing
			// just U+FEFF; under the new check, this is INVISIBLE_CHAR_IN_BODY
			// (parent is <div>, not in block elements).
			var bom = "\uFEFF";
			var html = $"<html><body><div class=\"foo1 foo2\">{bom} <img src=\"x.jpg\"/></div></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);
			var config = new ContentQualityConfig
			{
				CheckCmsTemplateAuthoringDefects = true,
				ContentQualityBlockElements = ["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th"],
			};

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("real-world.html", bytes, html, config)
				.ToList();

			// Expect EMBEDDED_BOM_IN_BODY (BOM bytes embedded inside body).
			Assert.Contains(issues, i => i.IssueType == "EMBEDDED_BOM_IN_BODY");

			// Expect INVISIBLE_CHAR_IN_BODY (same BOM as U+FEFF char in <div>).
			var invIssue = Assert.Single(issues, i => i.IssueType == "INVISIBLE_CHAR_IN_BODY");
			Assert.Contains("ZWNBSP/BOM", invIssue.Detail);
			Assert.Contains("foo1 foo2", invIssue.Context);
		}

		// ── WORD_SPLIT_BY_FORMATTING ──────────────────────────────────────
		// Per-letter formatting that fractures words. Distinct in name AND log
		// from the anchor family (SPLIT_WORD_ANCHOR) — verified untouched here.

		[Fact]
		public void WordSplitByFormatting_AcronymInitialsCluster_FlaggedOncePerBlock()
		{
			// Ground-truth markup: the IBAN/BIC paragraph whose per-letter bolding
			// produced the spelling fragments (nternational, ank, ccount, umber, …)
			// that the traverser's inline assembly eliminated. Two clusters (IBAN=4,
			// BIC=3) in ONE <p> => 7 matches => exactly ONE finding for the block.
			var html = "<html><body><p>IBAN steht f\u00fcr <b>I</b>nternational <b>B</b>ank<b> A</b>ccount "
				+ "<b>N</b>umber und der BIC (<b>B</b>ank <b>I</b>dentifier <b>C</b>ode)</p></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			var issue = Assert.Single(issues, i => i.IssueType == "WORD_SPLIT_BY_FORMATTING");
			Assert.Contains("<abbr>", issue.Detail);
			// The anchor family is a separate concern and must not appear.
			Assert.DoesNotContain(issues, i => i.IssueType == "SPLIT_WORD_ANCHOR");
		}

		[Fact]
		public void WordSplitByFormatting_ContextShowsReassembledWords()
		{
			var html = "<html><body><p>Term <b>I</b>nternational <b>B</b>ank <b>A</b>ccount here</p></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);

			var issue = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.Single(i => i.IssueType == "WORD_SPLIT_BY_FORMATTING");

			// InnerText glues the phrasing children back into the words a reader sees.
			Assert.Contains("International Bank Account", issue.Context);
			Assert.StartsWith("[<p", issue.Context);
		}

		[Fact]
		public void WordSplitByFormatting_BelowThreshold_NotFlagged()
		{
			// Only two single-letter spans — below the >=3 systemic-pattern threshold.
			var html = "<html><body><p>Only <b>T</b>wo <b>S</b>plit here</p></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "WORD_SPLIT_BY_FORMATTING");
		}

		[Fact]
		public void WordSplitByFormatting_MathSingleLetters_NotFlagged()
		{
			// Single-letter italics separated by spaces/operators — not glued to a
			// lowercase continuation, so this is math, not a fractured word.
			var html = "<html><body><p><i>x</i> + <i>y</i> + <i>z</i> equals the sum</p></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "WORD_SPLIT_BY_FORMATTING");
		}

		[Fact]
		public void WordSplitByFormatting_AnchorSplitsAreNotCounted()
		{
			// Words split by anchors are the SPLIT_WORD_ANCHOR concern (functional,
			// main log). Even three of them must NOT raise WORD_SPLIT_BY_FORMATTING —
			// the two families stay separate so each audience triages its own.
			var html = "<html><body><p>Giro<a href=\"#\">k</a>onto and <a href=\"#\">m</a>ore plus <a href=\"#\">e</a>ven</p></body></html>";
			var bytes = Encoding.UTF8.GetBytes(html);

			var issues = ContentQuality
				.CheckCmsTemplateAuthoringDefects("test.html", bytes, html, MakeConfig())
				.ToList();

			Assert.DoesNotContain(issues, i => i.IssueType == "WORD_SPLIT_BY_FORMATTING");
		}
	}
}
