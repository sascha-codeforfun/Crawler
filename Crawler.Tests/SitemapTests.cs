using System.Text;
using Xunit;
using Crawler.Urls;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Sitemap.GenerateSitemap — particularly the safeguard that
	/// prevents pages sourced from the CMS list pass from entering the sitemap.
	/// Uses real temp files and Cache.Load to exercise the full path.
	/// Placed in the Logger collection because Cache is static shared state.
	/// </summary>
	[Collection("Logger")]
	public class SitemapTests : IDisposable
	{
		private readonly string _tempDir;

		public SitemapTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"SitemapTests_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

		/// <summary>
		/// Writes a minimal HTML file and its index entry, then loads the cache.
		/// </summary>
		private string WritePageAndIndex(string filename, string url, string source,
			string robotsContent = "")
		{
			var robotsMeta = string.IsNullOrEmpty(robotsContent)
				? string.Empty
				: $"<meta name=\"robots\" content=\"{robotsContent}\">";

			var html = $"<html><head>{robotsMeta}<title>Test</title></head><body>Test</body></html>";
			var filePath = Path.Combine(_tempDir, filename);
			File.WriteAllText(filePath, html, Encoding.UTF8);
			return filePath;
		}

		private void LoadIndex(params (string filename, string url, string source)[] entries)
		{
			var indexPath = Path.Combine(_tempDir, "index.log");
			var lines = new System.Collections.Generic.List<string>();
			foreach (var (filename, url, src) in entries)
			{
				lines.Add($"{filename}|{url}|{src}");
			}

			File.WriteAllLines(indexPath, lines, Encoding.UTF8);
			Cache.Load(indexPath);
		}

		// ── Safeguard: list pages must never enter sitemap ────────────────────────

		[Fact]
		public void GenerateSitemap_ListPage_IsExcluded()
		{
			WritePageAndIndex("list-page.html", "https://example.com/list-page.html", "list");
			LoadIndex(("list-page.html", "https://example.com/list-page.html", "list"));

			var xml = Sitemap.GenerateSitemap(_tempDir, [], [], "*.html");

			Assert.DoesNotContain("list-page.html", xml);
			Assert.DoesNotContain("/list-page", xml);
		}

		[Fact]
		public void GenerateSitemap_DiscoveryPage_IsIncluded()
		{
			WritePageAndIndex("disc-page.html", "https://example.com/disc-page.html", "discovery");
			LoadIndex(("disc-page.html", "https://example.com/disc-page.html", "discovery"));

			var xml = Sitemap.GenerateSitemap(_tempDir, [], [], "*.html");

			Assert.Contains("disc-page.html", xml);
		}

		[Fact]
		public void GenerateSitemap_ListPageWithNoIndex_IsStillExcluded()
		{
			// noindex list pages are excluded by both the noindex check AND the
			// source safeguard — safeguard fires first.
			WritePageAndIndex("list-noindex.html", "https://example.com/list-noindex.html",
				"list", robotsContent: "noindex,nofollow");
			LoadIndex(("list-noindex.html", "https://example.com/list-noindex.html", "list"));

			var xml = Sitemap.GenerateSitemap(_tempDir, [], [], "*.html");

			Assert.DoesNotContain("list-noindex", xml);
		}

		[Fact]
		public void GenerateSitemap_ListPageWithIndex_IsExcluded()
		{
			// Even a page explicitly allowing indexing must not enter the sitemap
			// when its source is "list" — the safeguard is unconditional.
			WritePageAndIndex("list-index.html", "https://example.com/list-index.html",
				"list", robotsContent: "index,follow");
			LoadIndex(("list-index.html", "https://example.com/list-index.html", "list"));

			var xml = Sitemap.GenerateSitemap(_tempDir, [], [], "*.html");

			Assert.DoesNotContain("list-index", xml);
		}

		[Fact]
		public void GenerateSitemap_MixedSources_OnlyDiscoveryIncluded()
		{
			WritePageAndIndex("disc.html", "https://example.com/disc.html", "discovery");
			WritePageAndIndex("list.html", "https://example.com/list.html", "list");
			LoadIndex(
				("disc.html", "https://example.com/disc.html", "discovery"),
				("list.html", "https://example.com/list.html", "list"));

			var xml = Sitemap.GenerateSitemap(_tempDir, [], [], "*.html");

			Assert.Contains("disc.html", xml);
			Assert.DoesNotContain("list.html", xml);
		}

		// ── Existing noindex behaviour unaffected ─────────────────────────────────

		[Fact]
		public void GenerateSitemap_DiscoveryPageWithNoIndex_IsExcluded()
		{
			WritePageAndIndex("disc-noindex.html", "https://example.com/disc-noindex.html",
				"discovery", robotsContent: "noindex");
			LoadIndex(("disc-noindex.html", "https://example.com/disc-noindex.html", "discovery"));

			var xml = Sitemap.GenerateSitemap(_tempDir, [], [], "*.html");

			Assert.DoesNotContain("disc-noindex", xml);
		}
	}
}
