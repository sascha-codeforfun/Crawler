namespace Crawler.Tests.SpellCheck
{
	using System.Collections.Generic;
	using Crawler.SpellCheck;
	using Xunit;

	public class KnownDefectMatcherTests
	{
		private static KnownDefectMatcher Matcher() => new(new Dictionary<string, List<string>>
		{
			["div[@data-pagenav-global-label]"] = new() { "Seiteninhalt" },        // exact
			["div[@data-pagenav-title]"] = new() { "Springe zu*" },                // prefix
		});

		[Fact]
		public void Empty_WhenNoConfig()
		{
			Assert.True(new KnownDefectMatcher(null).IsEmpty);
			Assert.True(new KnownDefectMatcher(new Dictionary<string, List<string>>()).IsEmpty);
		}

		[Fact]
		public void Exact_MutesWholeValueWords()
		{
			var m = Matcher();
			Assert.True(m.IsKnownDefect("div[@data-pagenav-global-label]", "Seiteninhalt", "Seiteninhalt"));
		}

		[Fact]
		public void Exact_DoesNotMatch_WhenValueDiffers()
		{
			var m = Matcher();
			// value is not exactly "Seiteninhalt" → no mute
			Assert.False(m.IsKnownDefect("div[@data-pagenav-global-label]", "Seiteninhalt extra", "Seiteninhalt"));
		}

		[Fact]
		public void Prefix_MutesLiteralWords_ButKeepsVaryingTail()
		{
			var m = Matcher();
			const string value = "Springe zu Modernising";

			Assert.True(m.IsKnownDefect("div[@data-pagenav-title]", value, "Springe")); // literal → mute
			Assert.True(m.IsKnownDefect("div[@data-pagenav-title]", value, "zu"));      // literal → mute
			Assert.False(m.IsKnownDefect("div[@data-pagenav-title]", value, "Modernising")); // tail → still checked
		}

		[Fact]
		public void Prefix_DoesNotMatch_WhenValueDoesNotStartWithLiteral()
		{
			var m = Matcher();
			Assert.False(m.IsKnownDefect("div[@data-pagenav-title]", "Etwas anderes", "Springe"));
		}

		[Fact]
		public void WrongPath_NotMuted()
		{
			var m = Matcher();
			// "Seiteninhalt" elsewhere (e.g. real body text) is NOT muted — only from the declared path.
			Assert.False(m.IsKnownDefect("p[#text]", "Seiteninhalt", "Seiteninhalt"));
		}

		// ---- 608-pre: pin the construction guard branches (the class 608 mirrors). Test-only.

		[Fact]
		public void EmptyConfig_IsEmpty_AndMutesNothing()
		{
			var m = new KnownDefectMatcher(new Dictionary<string, List<string>>());
			Assert.True(m.IsEmpty);
			Assert.False(m.IsKnownDefect("div[@x]", "anything", "anything"));
		}

		[Fact]
		public void NullPatternList_EntrySkipped_NoThrow()
		{
			// A config entry whose pattern list is null is skipped, not dereferenced.
			var m = new KnownDefectMatcher(new Dictionary<string, List<string>> { ["div[@x]"] = null! });
			Assert.True(m.IsEmpty);
			Assert.False(m.IsKnownDefect("div[@x]", "anything", "anything"));
		}

		[Fact]
		public void EmptyPatternString_Skipped_RealPatternStillWorks()
		{
			// An empty pattern string is skipped; a real sibling pattern still compiles and matches.
			var m = new KnownDefectMatcher(new Dictionary<string, List<string>>
			{
				["div[@x]"] = new() { "", "Mustertext" },
			});
			Assert.True(m.IsKnownDefect("div[@x]", "Mustertext", "Mustertext"));
		}
	}
}
