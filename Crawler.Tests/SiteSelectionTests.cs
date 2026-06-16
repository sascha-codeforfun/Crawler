using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for SiteSelection — the pure decision for which configured site a run
	/// processes, given mode + an optional already-parsed operator choice. No console
	/// I/O is exercised here (the numbered-menu rendering and the re-prompt loop live
	/// in Program.PromptForSite and are operator-eyeball-verified); only the mapping
	/// is pinned.
	///
	/// Model:
	///   * Silent → the single IsPrimary site.
	///   * Interactive, Enter (null choice) → the primary (default matches silent).
	///   * Interactive, in-range number → that site (1-based).
	///   * Interactive, out-of-range → null (caller re-prompts; never coerced).
	/// The exactly-one-primary invariant is enforced by config validation; Primary
	/// throws if it is violated (defensive — reaching it with 0/2 primaries is a bug).
	/// </summary>
	public class SiteSelectionTests
	{
		private static SiteConfig Site(string name, bool primary) =>
			new() { Name = name, Url = $"https://{name}.example.com", IsPrimary = primary };

		private static List<SiteConfig> ThreeSites() =>
		[
			Site("alpha", primary: false),
			Site("bravo", primary: true),
			Site("charlie", primary: false),
		];

		// ── Primary ───────────────────────────────────────────────────────────

		[Fact]
		public void Primary_ReturnsTheSinglePrimary()
		{
			var primary = SiteSelection.Primary(ThreeSites());
			Assert.Equal("bravo", primary.Name);
		}

		[Fact]
		public void Primary_ThrowsWhenNoPrimary()
		{
			var sites = new List<SiteConfig> { Site("a", false), Site("b", false) };
			var ex = Assert.Throws<InvalidOperationException>(() => SiteSelection.Primary(sites));
			Assert.Contains("exactly one IsPrimary", ex.Message);
		}

		[Fact]
		public void Primary_ThrowsWhenMultiplePrimary()
		{
			var sites = new List<SiteConfig> { Site("a", true), Site("b", true) };
			Assert.Throws<InvalidOperationException>(() => SiteSelection.Primary(sites));
		}

		// ── Silent ──────────────────────────────────────────────────────────────

		[Fact]
		public void SelectSilent_ReturnsPrimary()
		{
			var chosen = SiteSelection.SelectSilent(ThreeSites());
			Assert.Equal("bravo", chosen.Name);
		}

		// ── Interactive ───────────────────────────────────────────────────────

		[Fact]
		public void SelectInteractive_EnterSelectsPrimary()
		{
			// null = operator pressed Enter for the default.
			var chosen = SiteSelection.SelectInteractive(ThreeSites(), null);
			Assert.NotNull(chosen);
			Assert.Equal("bravo", chosen!.Name);
		}

		[Theory]
		[InlineData(1, "alpha")]
		[InlineData(2, "bravo")]
		[InlineData(3, "charlie")]
		public void SelectInteractive_InRangeNumberSelectsThatSite(int oneBased, string expectedName)
		{
			var chosen = SiteSelection.SelectInteractive(ThreeSites(), oneBased);
			Assert.NotNull(chosen);
			Assert.Equal(expectedName, chosen!.Name);
		}

		[Theory]
		[InlineData(0)]    // below range (choices are 1-based)
		[InlineData(4)]    // above range (only 3 sites)
		[InlineData(99)]
		[InlineData(-1)]
		public void SelectInteractive_OutOfRangeReturnsNull(int oneBased)
		{
			// null signals the caller to re-prompt — must NOT be coerced to a default.
			var chosen = SiteSelection.SelectInteractive(ThreeSites(), oneBased);
			Assert.Null(chosen);
		}
	}
}
