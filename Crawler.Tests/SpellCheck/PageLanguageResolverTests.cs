namespace Crawler.Tests.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using Crawler.SpellCheck;
	using Xunit;

	public class PageLanguageResolverTests
	{
		private static Dictionary<string, List<string>> Overrides() => new()
		{
			["/de/home/international/"] = new() { "de", "en" },
			["/de/home/service/anleitungen-und-hilfe-en"] = new() { "de", "en" },
			["/de/home/welcome"] = new() { "de", "en", "fr" },
		};

		[Fact]
		public void NoOverrides_ReturnsBranchLanguageOnly()
		{
			var langs = PageLanguageResolver.Resolve("https://x.de/de/home/foo.html", "de", null);
			Assert.Equal(new[] { "de" }, langs);
		}

		[Fact]
		public void NoMatch_ReturnsBranchLanguageOnly()
		{
			var langs = PageLanguageResolver.Resolve("https://x.de/de/home/other.html", "de", Overrides());
			Assert.Equal(new[] { "de" }, langs);
		}

		[Fact]
		public void PrefixMatch_StartsWith_CatchesPageAndSubpages()
		{
			// The example: one prefix catches both the page and its lightbox subpages.
			var a = PageLanguageResolver.Resolve(
				"https://x.de/de/home/service/anleitungen-und-hilfe-en.html", "de", Overrides());
			var b = PageLanguageResolver.Resolve(
				"https://x.de/de/home/service/anleitungen-und-hilfe-en/lightboxes/passwort.html", "de", Overrides());

			Assert.Equal(new[] { "de", "en" }, a);
			Assert.Equal(new[] { "de", "en" }, b);
		}

		[Fact]
		public void DomainStripped_MatchesOnPathOnly()
		{
			var langs = PageLanguageResolver.Resolve(
				"https://www.any-host.example/de/home/welcome/start.html", "de", Overrides());
			Assert.Equal(new[] { "de", "en", "fr" }, langs);
		}

		[Fact]
		public void LongestPrefixWins_LexSpecialis()
		{
			var ov = new Dictionary<string, List<string>>
			{
				["/de/home/"] = new() { "de" },
				["/de/home/international/"] = new() { "de", "en" },
			};
			var langs = PageLanguageResolver.Resolve(
				"https://x.de/de/home/international/page.html", "de", ov);
			Assert.Equal(new[] { "de", "en" }, langs); // specific beats general regardless of order
		}

		[Fact]
		public void Match_IsCaseInsensitive_BothSides()
		{
			var langs = PageLanguageResolver.Resolve(
				"https://x.de/DE/Home/International/x.html", "de", Overrides());
			Assert.Equal(new[] { "de", "en" }, langs);
		}

		[Fact]
		public void QueryAndFragment_DoNotBreakMatch()
		{
			var langs = PageLanguageResolver.Resolve(
				"https://x.de/de/home/welcome?stref=languageswitch#top", "de", Overrides());
			Assert.Equal(new[] { "de", "en", "fr" }, langs);
		}

		[Fact]
		public void ValidateBundles_AllPresent_DoesNotThrow()
		{
			var bundles = new Dictionary<string, DictionaryBundle>
			{
				["de"] = new DictionaryBundle(),
				["en"] = new DictionaryBundle(),
				["fr"] = new DictionaryBundle(),
			};
			PageLanguageResolver.ValidateBundles(Overrides(), bundles); // no throw
		}

		[Fact]
		public void ValidateBundles_MissingBundle_Throws_NamingLanguageAndKey()
		{
			var bundles = new Dictionary<string, DictionaryBundle>
			{
				["de"] = new DictionaryBundle(),
				["en"] = new DictionaryBundle(),
				// fr deliberately absent
			};
			var ex = Assert.Throws<InvalidOperationException>(
				() => PageLanguageResolver.ValidateBundles(Overrides(), bundles));

			Assert.Contains("fr", ex.Message);
			Assert.Contains("/de/home/welcome", ex.Message);
		}

		[Fact]
		public void ValidateBundles_NoOverrides_DoesNotThrow()
		{
			var bundles = new Dictionary<string, DictionaryBundle>();
			PageLanguageResolver.ValidateBundles(null, bundles);
			PageLanguageResolver.ValidateBundles(new Dictionary<string, List<string>>(), bundles);
		}
	}
}
