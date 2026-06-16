namespace Crawler.Tests.SpellCheck
{
	using System.Collections.Generic;
	using System.Linq;
	using Crawler;               // QualityIssue
	using Crawler.SpellCheck;
	using Xunit;

	/// <summary>
	/// The cross-pass dedup matcher. Every fixture is SYNTHETIC and neutral, invented to exercise
	/// the seam-merge shape — never lifted from a crawled site. The core is SeamTokens: the merged
	/// word a collision excerpt yields once its inline tag is removed, isolated as the token present
	/// de-tagged but not tagged.
	/// </summary>
	public class WordCollisionMatcherTests
	{
		// trailing seam: <inline>…left</inline>Right…  → "leftRight"
		private const string TrailingSeam =
			"<span class=\"lead\">Alpha plus beta</span>Gamma continues here";

		// leading seam: …left<inline>Right…</inline>  → "leftRight"
		private const string LeadingSeam =
			"Alpha beta<span class=\"x\">Gamma delta</span>";

		[Fact]
		public void SeamTokens_TrailingSeam_YieldsMergedToken()
		{
			var tokens = WordCollisionMatcher.SeamTokens(TrailingSeam);
			Assert.Contains("betaGamma", tokens);
		}

		[Fact]
		public void SeamTokens_LeadingSeam_YieldsMergedToken()
		{
			var tokens = WordCollisionMatcher.SeamTokens(LeadingSeam);
			Assert.Contains("betaGamma", tokens);
		}

		[Fact]
		public void SeamTokens_DoesNotYieldSharedWords()
		{
			// Words that exist on their own in the excerpt appear in BOTH tagged and de-tagged forms,
			// so they are never seam tokens — a genuine typo sharing the sentence stays checkable.
			var tokens = WordCollisionMatcher.SeamTokens(TrailingSeam);
			Assert.DoesNotContain("Alpha", tokens);
			Assert.DoesNotContain("continues", tokens);
			Assert.DoesNotContain("here", tokens);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		public void SeamTokens_NullOrEmpty_IsEmpty(string? excerpt)
		{
			Assert.Empty(WordCollisionMatcher.SeamTokens(excerpt));
		}

		private static ContentQuality.QualityIssue Collision(string filename, string excerpt)
			=> new(filename, WordCollisionMatcher.WordCollisionType, "merge", excerpt);

		[Fact]
		public void WordsForFile_ContainsSeamToken_KeyedByFilename()
		{
			var m = new WordCollisionMatcher(new[] { Collision("page-1.html", TrailingSeam) });
			Assert.False(m.IsEmpty);
			Assert.Contains("betaGamma", m.WordsForFile("page-1.html"));
			Assert.Empty(m.WordsForFile("other.html"));   // unknown file → empty set
		}

		[Fact]
		public void IgnoresNonCollisionIssues()
		{
			var m = new WordCollisionMatcher(new[]
			{
				new ContentQuality.QualityIssue("page-1.html", "BARE_TEXT", "x", TrailingSeam),
			});
			Assert.True(m.IsEmpty);   // not a WORD_COLLISION → no seam tokens harvested
		}

		[Fact]
		public void NullCollisions_IsEmpty_AndYieldsEmptySet()
		{
			var m = new WordCollisionMatcher(null);
			Assert.True(m.IsEmpty);
			Assert.Empty(m.WordsForFile("anything.html"));
		}
	}
}
