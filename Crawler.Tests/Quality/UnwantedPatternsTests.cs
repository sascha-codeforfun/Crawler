using Crawler.Quality;
using Xunit;

namespace Crawler.Tests.Quality
{
	public class UnwantedPatternsTests
	{
		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius    = 120,
			ContentQualityQuoteFullSentence = false,  // keep tests deterministic
			ContentQualityQuoteMaxExcerpt  = 400,
		};

		// ── Check ─────────────────────────────────────────────

		[Fact]
		public void Check_NoMatch_ReturnsEmpty()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>Clean content</p>", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_Match_ReturnsIssue()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>%(unfilled_var)</p>", patterns, DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issues[0].IssueType);
		}

		[Fact]
		public void Check_CaseInsensitive_MatchesRegardlessOfCase()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Keyword", Patterns = ["todo"], CaseSensitive = false }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>TODO: fix this</p>", patterns, DefaultConfig()).ToList();
			Assert.Single(issues);
		}

		[Fact]
		public void Check_CaseSensitive_NoMatchOnWrongCase()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Keyword", Patterns = ["todo"], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "<p>TODO: fix this</p>", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_MultipleOccurrences_ReportsAll()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Template", Name = "Unfilled", Patterns = ["%("], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "%(var1) and %(var2)", patterns, DefaultConfig()).ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void Check_UnconfiguredGroup_Skipped()
		{
			// IsConfigured returns false when Patterns is empty
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Test", Name = "Empty", Patterns = [], CaseSensitive = true }
			};
			var issues = UnwantedPatterns.Check("f.html", "anything", patterns, DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_OpenEnvelopeWithReferenceHits_CoalescesToOneIssue()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// Opener %( present, closer )% absent (the broken case); produkt. and p_name sit
			// in the unbroken run after the opener → all three collapse into one finding.
			var issues = UnwantedPatterns.Check(
				"f.html", "<x>%(produkt.278.p_name)</x>", patterns, DefaultConfig()).ToList();

			var issue = Assert.Single(issues);
			Assert.Equal("UNWANTED_PATTERN", issue.IssueType);
			Assert.Contains("CMS-Parameter-Leak", issue.Detail);
			Assert.Contains("missing closing ')%'", issue.Detail);
			Assert.Contains("— patterns: %(, produkt., p_name", issue.Detail);
		}

		[Fact]
		public void Check_OpenEnvelopeNoReferenceHitsInRange_EachFiresAlone()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// produkt. is past the whitespace bounding the open envelope's region → not folded.
			var issues = UnwantedPatterns.Check(
				"f.html", "%(foo.bar) produkt.", patterns, DefaultConfig()).ToList();

			Assert.Equal(2, issues.Count);
			Assert.Contains(issues, x => x.Detail.Contains("— pattern: %("));
			Assert.Contains(issues, x => x.Detail.Contains("— pattern: produkt."));
			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
		}

		[Fact]
		public void Check_EnvelopeWithoutReference_DoesNotCoalesce()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"] },   // no Reference → no coalescing
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			var issues = UnwantedPatterns.Check(
				"f.html", "<x>%(produkt.278.p_name)</x>", patterns, DefaultConfig()).ToList();

			// Envelope fires (pattern: %() and both editor hits fire separately — three cards.
			Assert.Equal(3, issues.Count);
			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
		}

		[Fact]
		public void Check_BalancedEnvelope_NotCoalesced()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "CMS-Editor-Error" },
				new() { Category = "Security", Name = "CMS-Editor-Error", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["produkt.", "p_name"] }
			};
			// Both delimiters present → balanced, not the broken case → no coalescing.
			var issues = UnwantedPatterns.Check(
				"f.html", "<x>%(produkt.278.p_name)%</x>", patterns, DefaultConfig()).ToList();

			Assert.DoesNotContain(issues, x => x.Detail.Contains("open placeholder"));
			Assert.Contains(issues, x => x.Detail.Contains("— patterns: %(, )%"));
			Assert.Equal(3, issues.Count);
		}

		[Fact]
		public void Check_RegionStopsAtMarkupBoundary()
		{
			var patterns = new List<ContentUnwantedPattern>
			{
				new() { Category = "Security", Name = "CMS-Parameter-Leak", GroupPatterns = true,
					CaseSensitive = true, Patterns = ["%(", ")%"], Reference = "Inner" },
				new() { Category = "Security", Name = "Inner", GroupPatterns = false,
					CaseSensitive = true, Patterns = ["institut.", "name"] }
			};
			// The 'name' inside the placeholder folds; the 'name' after </p> is past the '<'
			// boundary and must NOT be folded — it surfaces on its own.
			var issues = UnwantedPatterns.Check(
				"f.html", "%(institut.name)</p>name", patterns, DefaultConfig()).ToList();

			Assert.Contains(issues, x => x.Detail.Contains("open placeholder")
				&& x.Detail.Contains("institut."));
			Assert.Contains(issues, x => x.Detail == "Security: Inner — pattern: name");
		}

		// ── ExtractHighlightPatterns: UNWANTED_PATTERN merged-Detail round-trip ──

		[Fact]
		public void ExtractHighlightPatterns_MergedEnvelopeDetail_ReturnsAllPatterns()
		{
			// The merged Detail must round-trip through the highlighter so every folded
			// pattern is marked on the card and in the ticket.
			var word = "UNWANTED_PATTERN:Security: CMS-Parameter-Leak — open placeholder, " +
				"missing closing ')%' — patterns: %(, produkt., p_name";
			var result = ContentQualityTriage.ExtractHighlightPatterns(word);
			Assert.Equal(new[] { "%(", "produkt.", "p_name" }, result);
		}
	}
}
