using Xunit;
using Crawler.Security;

namespace Crawler.Tests.Security
{
	/// <summary>
	/// Tests for CsvInjectionGuard — the pure CSV formula-injection guard applied
	/// at the export compose chokepoint. No Logger and no shared state: the guard
	/// is a deterministic function pair, so these run fully parallel and assert the
	/// returned string directly. Inputs are treated as attacker-influenced; the
	/// assertions are our invariants (a leading formula trigger is escaped; every
	/// other value is preserved; Denormalize inverts what Neutralize added).
	/// </summary>
	public class CsvInjectionGuardTests
	{
		[Theory]
		[InlineData("=cmd()")]
		[InlineData("+1+1")]
		[InlineData("-2+3")]
		[InlineData("@SUM(A1:A9)")]
		[InlineData("\t=danger")]
		[InlineData("\r=danger")]
		public void Neutralize_LeadingTrigger_PrependsApostrophe(string input)
		{
			var result = CsvInjectionGuard.Neutralize(input);
			Assert.Equal("'" + input, result);
		}

		[Theory]
		[InlineData("https://example.test/path")]
		[InlineData("ab12cd34.unverified")]
		[InlineData("BARE_TEXT_IN_CONTAINER")]
		[InlineData("einfachen")]
		[InlineData("plain text excerpt")]
		[InlineData("3 widgets")]
		public void Neutralize_SafeValue_Unchanged(string input)
		{
			Assert.Equal(input, CsvInjectionGuard.Neutralize(input));
		}

		[Fact]
		public void Neutralize_AlreadyLeadingApostrophe_Unchanged()
		{
			// First char is an apostrophe, not a formula trigger, so nothing is added.
			Assert.Equal("'tis", CsvInjectionGuard.Neutralize("'tis"));
		}

		[Theory]
		[InlineData("")]
		[InlineData(null)]
		public void Neutralize_NullOrEmpty_ReturnsEmpty(string? input)
		{
			Assert.Equal(string.Empty, CsvInjectionGuard.Neutralize(input));
		}

		[Theory]
		[InlineData("=cmd()")]
		[InlineData("+1+1")]
		[InlineData("-2+3")]
		[InlineData("@SUM(A1:A9)")]
		[InlineData("\t=danger")]
		[InlineData("\r=danger")]
		public void RoundTrip_TriggerLeading_IsIdentity(string input)
		{
			var restored = CsvInjectionGuard.Denormalize(CsvInjectionGuard.Neutralize(input));
			Assert.Equal(input, restored);
		}

		[Theory]
		[InlineData("https://example.test/path")]
		[InlineData("ab12cd34.unverified")]
		[InlineData("plain text excerpt")]
		[InlineData("'tis a quote")]
		public void RoundTrip_SafeValue_IsIdentity(string input)
		{
			var restored = CsvInjectionGuard.Denormalize(CsvInjectionGuard.Neutralize(input));
			Assert.Equal(input, restored);
		}

		[Fact]
		public void Denormalize_GenuineApostropheNotTrigger_Preserved()
		{
			// Apostrophe followed by a non-trigger char is real data, left intact.
			Assert.Equal("'tis", CsvInjectionGuard.Denormalize("'tis"));
			Assert.Equal("'", CsvInjectionGuard.Denormalize("'"));
		}

		[Fact]
		public void Denormalize_GenuineApostropheThenTrigger_IsStripped_KnownEdge()
		{
			// Documented, accepted limitation: a source value that genuinely begins
			// with an apostrophe immediately followed by a trigger (e.g. "'=x") is
			// indistinguishable from a neutralized "=x", so Denormalize strips it.
			// This runs on exactly one read-back field (self-link ContextSnippet),
			// where such a value is vanishingly rare and the only effect is one lost
			// leading apostrophe in a display excerpt — never an identity/key field.
			Assert.Equal("=x", CsvInjectionGuard.Denormalize("'=x"));
		}

		[Theory]
		[InlineData("")]
		[InlineData(null)]
		public void Denormalize_NullOrEmpty_ReturnsEmpty(string? input)
		{
			Assert.Equal(string.Empty, CsvInjectionGuard.Denormalize(input));
		}

		[Fact]
		public void Neutralize_FormulaCharNotLeading_Unchanged()
		{
			// Triggers only matter as the first character; mid-string they are data.
			Assert.Equal("a=b+c", CsvInjectionGuard.Neutralize("a=b+c"));
			Assert.Equal("x-y", CsvInjectionGuard.Neutralize("x-y"));
		}
	}
}
