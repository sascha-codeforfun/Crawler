using System.Collections.Generic;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the 647 reverse index (bundle → pages): the inversion, reach counting, and — critically —
	/// that a bundle re-deployed under a different fingerprint across pages still unifies to one key
	/// (so reach is real, not split by cache-busters). Synthetic pages; no disk, no Cache.
	/// </summary>
	public class ScriptPageIndexTests
	{
		private static string Page(params string?[] srcs)
		{
			var sb = new System.Text.StringBuilder("<html><body>");
			foreach (var s in srcs)
			{
				sb.Append(s == null ? "<script>var inline=1;</script>" : $"<script src=\"{s}\"></script>");
			}
			sb.Append("</body></html>");
			return sb.ToString();
		}

		[Fact]
		public void SameBundleDifferentHashAcrossPages_UnifiesToReachTwo()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/a.html", Page("/x/business_check.min.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.js")),
				("https://s/b.html", Page("/x/business_check.min.BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB.js")),
			});

			Assert.Equal(1, idx.KeyCount);
			Assert.Equal(2, idx.Reach("/x/business_check.min.js"));
			Assert.Equal(new[] { "https://s/a.html", "https://s/b.html" }, idx.Pages("/x/business_check.min.js"));
		}

		[Fact]
		public void PageWithTwoBundles_BothReachOne()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/p.html", Page("/x/foo.min.js", "/x/bar.min.js")),
			});

			Assert.Equal(1, idx.Reach("/x/foo.min.js"));
			Assert.Equal(1, idx.Reach("/x/bar.min.js"));
		}

		[Fact]
		public void InlineScriptIgnored()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/p.html", Page(new string?[] { null })),
			});

			Assert.Equal(0, idx.KeyCount);
		}

		[Fact]
		public void UnknownKey_ReachZero_NoPages()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>());
			Assert.Equal(0, idx.Reach("/x/nope.min.js"));
			Assert.Empty(idx.Pages("/x/nope.min.js"));
		}

		[Fact]
		public void RelativeSrcResolvedAgainstPageUrl()
		{
			var idx = ScriptPageIndex.Build(new List<(string, string)>
			{
				("https://s/sub/page.html", Page("../x/rel.min.6c0bbe7c96d74f6d31c63e6df2f1b9d4.js")),
			});

			Assert.Equal(1, idx.Reach("/x/rel.min.js"));
		}
	}
}
