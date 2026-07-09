using Xunit;
using Crawler.Quality;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for IssueSuppressions — pure-helper filtering of content-quality
	/// findings against operator-configured rules. Introduced in #304.
	///
	/// Scope: all matching semantics (Type, Value substring, Pages glob,
	/// Enabled toggle, first-match hit attribution) and the GlobToRegex
	/// helper. No I/O in this class — Apply() is a pure function.
	/// </summary>
	public class IssueSuppressionsTests
	{
		// ── Test helpers ─────────────────────────────────────────────────

		private static QualityIssue Issue(
			string filename = "page.html",
			string issueType = "BARE_TEXT_IN_CONTAINER",
			string detail = "Text directly inside <div> without block wrapper",
			string context = "[<div class=\"h2\">] some text")
			=> new(filename, issueType, detail, context);

		private static IssueSuppressionRule Rule(
			string type = "BARE_TEXT_IN_CONTAINER",
			string value = "",
			List<string>? pages = null,
			bool enabled = true,
			string comment = "")
			=> new()
			{
				Type = type,
				Value = value,
				Pages = pages ?? [],
				Enabled = enabled,
				Comment = comment,
			};

		// ── Empty / null rules ───────────────────────────────────────────

		[Fact]
		public void Apply_NullRules_EmitsAll()
		{
			var input = new[] { Issue(), Issue(filename: "other.html") };
			var result = IssueSuppressions.Apply(input, null);
			Assert.Equal(2, result.Emitted.Count);
			Assert.Empty(result.RuleHits);
		}

		[Fact]
		public void Apply_EmptyRules_EmitsAll()
		{
			var input = new[] { Issue() };
			var result = IssueSuppressions.Apply(input, []);
			Assert.Single(result.Emitted);
		}

		// ── Type-only matching ───────────────────────────────────────────

		[Fact]
		public void Apply_TypeOnly_SuppressesAllOfThatType()
		{
			var input = new[]
			{
				Issue(issueType: "BARE_TEXT_IN_CONTAINER"),
				Issue(issueType: "BARE_TEXT_IN_CONTAINER"),
				Issue(issueType: "UNWANTED_PATTERN"),
			};
			var result = IssueSuppressions.Apply(input, [Rule()]);
			Assert.Single(result.Emitted);
			Assert.Equal("UNWANTED_PATTERN", result.Emitted[0].IssueType);
			Assert.Equal(2, result.RuleHits[0]);
		}

		[Fact]
		public void Apply_TypeMismatch_DoesNotMatch()
		{
			var input = new[] { Issue(issueType: "UNWANTED_PATTERN") };
			var result = IssueSuppressions.Apply(input,
				[Rule(type: "BARE_TEXT_IN_CONTAINER")]);
			Assert.Single(result.Emitted);
			Assert.Equal(0, result.RuleHits[0]);
		}

		// ── Value substring matching ─────────────────────────────────────

		[Fact]
		public void Apply_ValueSubstring_MatchesAgainstContext()
		{
			var input = new[]
			{
				Issue(context: "[<div class=\"h2\">] heading text"),
				Issue(context: "[<div class=\"sectionTitle\">] heading text"),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(value: "class=\"h2\"")]);
			Assert.Single(result.Emitted);
			Assert.Contains("sectionTitle", result.Emitted[0].Context);
		}

		[Fact]
		public void Apply_ValueSubstring_MatchesAgainstDetail()
		{
			var input = new[]
			{
				Issue(issueType: "UNWANTED_PATTERN",
					detail: "Security: CMS-Parameter-Leak — patterns: %(, )%",
					context: "...some context..."),
				Issue(issueType: "UNWANTED_PATTERN",
					detail: "Marketing: Lorem-Ipsum-Placeholder",
					context: "...some context..."),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(type: "UNWANTED_PATTERN", value: "CMS-Parameter-Leak")]);
			Assert.Single(result.Emitted);
			Assert.Contains("Lorem-Ipsum", result.Emitted[0].Detail);
		}

		[Fact]
		public void Apply_ValueIsCaseSensitive()
		{
			var input = new[]
			{
				Issue(context: "[<div class=\"H2\">] uppercase H2"),
				Issue(context: "[<div class=\"h2\">] lowercase h2"),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(value: "class=\"h2\"")]);
			Assert.Single(result.Emitted);
			Assert.Contains("H2", result.Emitted[0].Context);
		}

		[Fact]
		public void Apply_EmptyValue_MatchesAnyValueWithinType()
		{
			var input = new[]
			{
				Issue(context: "anything"),
				Issue(context: "something different"),
			};
			var result = IssueSuppressions.Apply(input, [Rule(value: "")]);
			Assert.Empty(result.Emitted);
			Assert.Equal(2, result.RuleHits[0]);
		}

		// ── Pages glob matching ──────────────────────────────────────────

		[Fact]
		public void Apply_PagesGlob_ScopesSuppression()
		{
			var input = new[]
			{
				Issue(filename: "abc123demo-page.html"),
				Issue(filename: "abc123real-page.html"),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(pages: ["*demo*"])]);
			Assert.Single(result.Emitted);
			Assert.Contains("real-page", result.Emitted[0].Filename);
		}

		[Fact]
		public void Apply_PagesGlob_StarMatchesAnyChars()
		{
			var input = new[]
			{
				Issue(filename: "prefix-foo-suffix.html"),
				Issue(filename: "prefix-foo.html"),
				Issue(filename: "prefix-bar-suffix.html"),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(pages: ["*foo*"])]);
			Assert.Single(result.Emitted);
			Assert.Contains("bar", result.Emitted[0].Filename);
		}

		[Fact]
		public void Apply_PagesGlob_AnchoredOnBothEnds()
		{
			// Glob "foo.html" without surrounding * matches only exact filename.
			var input = new[]
			{
				Issue(filename: "foo.html"),
				Issue(filename: "prefix-foo.html"),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(pages: ["foo.html"])]);
			Assert.Single(result.Emitted);
			Assert.Equal("prefix-foo.html", result.Emitted[0].Filename);
		}

		[Fact]
		public void Apply_PagesList_AnyEntryMatching_Suppresses()
		{
			var input = new[]
			{
				Issue(filename: "abc-alpha.html"),
				Issue(filename: "abc-beta.html"),
				Issue(filename: "abc-gamma.html"),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(pages: ["*alpha*", "*beta*"])]);
			Assert.Single(result.Emitted);
			Assert.Contains("gamma", result.Emitted[0].Filename);
		}

		[Fact]
		public void Apply_EmptyPages_IsGlobal()
		{
			var input = new[]
			{
				Issue(filename: "any-page.html"),
				Issue(filename: "other-page.html"),
			};
			var result = IssueSuppressions.Apply(input, [Rule(pages: [])]);
			Assert.Empty(result.Emitted);
		}

		// ── Enabled toggle ───────────────────────────────────────────────

		[Fact]
		public void Apply_DisabledRule_IsIgnored()
		{
			var input = new[] { Issue(), Issue() };
			var result = IssueSuppressions.Apply(input, [Rule(enabled: false)]);
			Assert.Equal(2, result.Emitted.Count);
			Assert.Equal(0, result.RuleHits[0]);
		}

		// ── Validation / robustness ──────────────────────────────────────

		[Fact]
		public void Apply_RuleWithEmptyType_IsSkipped()
		{
			var input = new[] { Issue() };
			var result = IssueSuppressions.Apply(input, [Rule(type: "")]);
			Assert.Single(result.Emitted);
			Assert.Equal(0, result.RuleHits[0]);
		}

		[Fact]
		public void Apply_EmittedOrder_PreservesInputOrder()
		{
			var input = new[]
			{
				Issue(filename: "a.html"),
				Issue(filename: "b.html"),
				Issue(filename: "c.html"),
				Issue(filename: "d.html"),
			};
			var result = IssueSuppressions.Apply(input,
				[Rule(pages: ["b.html", "c.html"])]);
			Assert.Equal(2, result.Emitted.Count);
			Assert.Equal("a.html", result.Emitted[0].Filename);
			Assert.Equal("d.html", result.Emitted[1].Filename);
		}

		// ── First-match attribution ──────────────────────────────────────

		[Fact]
		public void Apply_MultipleMatchingRules_FirstMatchCredited()
		{
			// Two rules both match the same finding. Hit attribution goes
			// to rule index 0 (earliest in the rules list), per the
			// documented semantics.
			var input = new[] { Issue() };
			var rules = new[]
			{
				Rule(value: "class=\"h2\""),    // matches Context
				Rule(value: "block wrapper"),   // also matches Detail
			};
			var result = IssueSuppressions.Apply(input, rules);
			Assert.Empty(result.Emitted);
			Assert.Equal(1, result.RuleHits[0]);
			Assert.Equal(0, result.RuleHits[1]);
		}

		[Fact]
		public void Apply_NonOverlappingRules_HitsAttributedCorrectly()
		{
			var input = new[]
			{
				Issue(issueType: "BARE_TEXT_IN_CONTAINER"),
				Issue(issueType: "UNWANTED_PATTERN"),
				Issue(issueType: "BARE_TEXT_IN_CONTAINER"),
			};
			var rules = new[]
			{
				Rule(type: "BARE_TEXT_IN_CONTAINER"),
				Rule(type: "UNWANTED_PATTERN"),
			};
			var result = IssueSuppressions.Apply(input, rules);
			Assert.Empty(result.Emitted);
			Assert.Equal(2, result.RuleHits[0]);
			Assert.Equal(1, result.RuleHits[1]);
		}

		// ── GlobToRegex internals ───────────────────────────────────────

		[Fact]
		public void GlobToRegex_DotsEscaped()
		{
			// Without escaping, "foo.html" matches "fooXhtml" too. Confirm escape.
			var rx = IssueSuppressions.GlobToRegex("foo.html");
			Assert.Matches(rx, "foo.html");
			Assert.DoesNotMatch(rx, "fooXhtml");
		}

		[Fact]
		public void GlobToRegex_StarMatchesEmpty()
		{
			var rx = IssueSuppressions.GlobToRegex("*foo*");
			Assert.Matches(rx, "foo");
			Assert.Matches(rx, "xfoo");
			Assert.Matches(rx, "fooy");
			Assert.Matches(rx, "xfooy");
			Assert.DoesNotMatch(rx, "bar");
		}

		[Fact]
		public void GlobToRegex_BracketsEscaped()
		{
			// Brackets are regex metacharacters but glob literals.
			var rx = IssueSuppressions.GlobToRegex("[a]b");
			Assert.Matches(rx, "[a]b");
			Assert.DoesNotMatch(rx, "ab");
		}
	}
}
