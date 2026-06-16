using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for genuinely-untested logic identified in the coverage sweep:
	///   • Config.LoadFromJson — a clean multi-site config loads without halting.
	///   • CrawlIndex.LookUpSourceForFile — the source-column lookup facade
	///     (sibling of the already-tested LookUpUrlForFile).
	///
	/// SYNTHETIC fixtures. In the Logger collection: LoadFromJson / CrawlIndex log
	/// via the static Logger; UrlCache is process-wide so lookup fixtures use
	/// distinct filenames.
	/// </summary>
	[Collection("Logger")]
	public class ConfigMigrationGuardTests : IDisposable
	{
		private readonly string _dir;

		public ConfigMigrationGuardTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"cfgmig-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		private string WriteConfig(string json)
		{
			var path = Path.Combine(_dir, $"config-{Guid.NewGuid():N}.json");
			File.WriteAllText(path, json, Encoding.UTF8);
			return path;
		}

		// A minimal config that satisfies every ValidateConfig rule (mirrors the
		// known-good shape in ConfigValidationTests).
		private static string ValidBase()
		{
			return "{\n" +
				"  \"Sites\": [ {\n" +
				"    \"Name\": \"Site1\",\n" +
				"    \"Tenant\": \"tenant001\",\n" +
				"    \"IsPrimary\": true,\n" +
				"    \"Url\": \"https://example.com\",\n" +
				"    \"UrlSubdomainsAllowed\": [],\n" +
				"    \"PostCrawlPass\": false\n" +
				"  } ],\n" +
				"  \"BaseDirectory\": \"C:/temp\",\n" +
				"  \"FilePattern\": \"*.html\",\n" +
				"  \"CustomDictionaryFile\": \"custom.dic\",\n" +
				"  \"MaxConcurrentAssetDownloads\": 1\n" +
				"}";
		}

		[Fact]
		public void LoadFromJson_CleanMultiSiteConfig_LoadsWithoutGuardHalt()
		{
			// None of the legacy keys present → the config loads cleanly.
			var path = WriteConfig(ValidBase());

			var config = Config.LoadFromJson(path);
			Assert.NotNull(config);
		}

		// ── CrawlIndex.LookUpSourceForFile ──────────────────────────────────

		[Fact]
		public void LookUpSourceForFile_RegisteredFile_ReturnsSource()
		{
			var filename = $"page-{Guid.NewGuid():N}.html";
			var lookup = Path.Combine(_dir, $"lookup-{Guid.NewGuid():N}.txt");
			File.WriteAllLines(lookup,
				new[] { $"{filename}|https://example.com/page|sitemap" }, Encoding.UTF8);
			UrlCache.LoadCache(lookup);

			Assert.Equal("sitemap", CrawlIndex.LookUpSourceForFile(filename));
		}

		[Fact]
		public void LookUpSourceForFile_UnregisteredFile_ReturnsEmpty()
		{
			Assert.Equal(string.Empty,
				CrawlIndex.LookUpSourceForFile($"never-registered-{Guid.NewGuid():N}.html"));
		}
	}
}
