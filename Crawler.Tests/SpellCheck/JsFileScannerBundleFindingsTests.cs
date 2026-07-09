using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// 659 — pins <see cref="JsFileScanner.BuildBundleFindings"/>, the pure assembly behind the emitted
	/// file-scan findings: distinct words (first excerpt kept, order preserved) and the reach-based routing
	/// decision (CLEAR when reach ≤ threshold, BULK when above). This is the contract the eventual
	/// SCRIPT_SPELLING tickets depend on; it is unit-testable without a spell checker or a crawl directory.
	/// </summary>
	public class JsFileScannerBundleFindingsTests
	{
		private static readonly IReadOnlyList<string> NoPages = new List<string>();

		private static ScriptBundleFindings Build(int reach, int threshold, params ScriptWordHit[] hits) =>
			JsFileScanner.BuildBundleFindings("app/main.js", "main-js", "https://site/app/main.js",
				reach, threshold, NoPages, hits);

		[Fact]
		public void DedupesWords_KeepingFirstExcerptAndOrder()
		{
			var r = Build(1, 5,
				new ScriptWordHit("foo", "first foo context"),
				new ScriptWordHit("bar", "bar context"),
				new ScriptWordHit("foo", "second foo context"));

			Assert.Equal(2, r.Words.Count);
			Assert.Equal("foo", r.Words[0].Word);
			Assert.Equal("first foo context", r.Words[0].Excerpt); // first excerpt wins
			Assert.Equal("bar", r.Words[1].Word);                  // order preserved
		}

		[Fact]
		public void ReachAboveThreshold_IsBulk()
		{
			Assert.True(Build(10, 5, new ScriptWordHit("x", "c")).IsBulk);
		}

		[Fact]
		public void ReachAtThreshold_IsClear()
		{
			Assert.False(Build(5, 5, new ScriptWordHit("x", "c")).IsBulk);
		}

		[Fact]
		public void ReachBelowThreshold_IsClear()
		{
			Assert.False(Build(2, 5, new ScriptWordHit("x", "c")).IsBulk);
		}

		[Fact]
		public void NoHits_ProducesNoWords()
		{
			Assert.Empty(Build(3, 5).Words);
		}

		[Fact]
		public void CarriesBundleIdentityAndPages()
		{
			var pages = new List<string> { "https://site/a", "https://site/b" };
			var r = JsFileScanner.BuildBundleFindings("v/lib.js", "lib-js", "https://site/v/lib.js",
				2, 5, pages, new[] { new ScriptWordHit("typo", "a typo here") });

			Assert.Equal("v/lib.js", r.BundlePath);
			Assert.Equal("lib-js", r.StableKey);
			Assert.Equal("https://site/v/lib.js", r.BundleUrl);
			Assert.Equal(2, r.Reach);
			Assert.Equal(pages, r.Pages);
		}
	}
}
