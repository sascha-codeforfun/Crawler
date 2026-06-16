using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Round-trips the *shipped* config.json through Config.LoadFromJson. This is
	/// a guard against drift: the committed default config carries 500+ comment
	/// lines and exercises FilterComments, the legacy-field migration guards, and
	/// every required-field rule in ValidateConfig. If a future edit breaks the
	/// JSON, renames a field, or violates a validation rule, this fails loudly
	/// rather than only surfacing at first run.
	///
	/// The file is located by walking up from the test assembly to the source
	/// tree (…/Crawler/config.json). If it cannot be found — e.g. an unusual
	/// build layout where the source isn't alongside the bin output — the test
	/// early-returns (a no-op pass) rather than failing, so it never produces a
	/// false negative in CI-only checkouts. (xUnit 2.9.0 has no Assert.Skip, so
	/// an early return is used in place of a runtime skip.) In the Logger
	/// collection because LoadFromJson routes validation failures through the
	/// static Logger.
	/// </summary>
	[Collection("Logger")]
	public class ConfigJsonRoundTripTests
	{
		/// <summary>
		/// Walks up from the test assembly's directory looking for
		/// Crawler/config.json. Returns null if not found within a sane depth.
		/// </summary>
		private static string? LocateShippedConfig()
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);

			for (int i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
			{
				// Common layouts: solution-root/Crawler/config.json, or the file
				// copied next to the test bin output.
				var candidates = new[]
				{
					Path.Combine(dir.FullName, "Crawler", "config.json"),
					Path.Combine(dir.FullName, "config.json"),
				};

				foreach (var c in candidates)
				{
					if (File.Exists(c))
					{
						return c;
					}
				}
			}

			return null;
		}

		[Fact]
		public void ShippedConfigJson_LoadsAndValidates()
		{
			var path = LocateShippedConfig();
			if (path is null)
			{
				// Shipped config.json not found relative to the test assembly
				// (unusual build layout). Treat as a no-op rather than failing —
				// xUnit 2.9.0 has no Assert.Skip, so we early-return instead.
				return;
			}

			// Should not throw: parses, passes the legacy-field guards, and
			// satisfies every ValidateConfig rule.
			var config = Config.LoadFromJson(path);

			Assert.NotNull(config);

			// Spot-check the always-required fields so a silently-emptied file
			// (e.g. all fields dropped by a bad merge) is caught even if
			// ValidateConfig's rules are later loosened. Url is now PER-SITE
			// (config.Url is empty until ResolveForSite projects a selected site),
			// so check the Sites collection rather than the top-level Url.
			Assert.NotEmpty(config.Sites);
			Assert.Single(config.Sites, s => s.IsPrimary);
			Assert.All(config.Sites, s => Assert.False(string.IsNullOrWhiteSpace(s.Url)));
			Assert.False(string.IsNullOrWhiteSpace(config.BaseDirectory));
			Assert.False(string.IsNullOrWhiteSpace(config.FilePattern));
			Assert.False(string.IsNullOrWhiteSpace(config.CustomDictionaryFile));
		}

		[Fact]
		public void ShippedConfigJson_ParallelismDefaultsAreInValidRange()
		{
			var path = LocateShippedConfig();
			if (path is null)
			{
				return;
			}

			var config = Config.LoadFromJson(path);

			// Mirrors the numeric ValidateConfig rules — kept here so a change to
			// the shipped defaults that slips past validation (e.g. validation
			// relaxed) still gets a sanity bound.
			Assert.True(config.MaxDegreeOfParallelism >= 0);
			Assert.True(config.MaxConcurrentPageDownloads >= 1);
			Assert.True(config.MaxConcurrentAssetDownloads >= 1);
		}
	}
}
