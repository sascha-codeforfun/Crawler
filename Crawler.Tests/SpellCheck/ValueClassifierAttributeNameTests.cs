using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the attribute-name skip in <see cref="ValueClassifier.ClassifyScriptLiteral"/>: a SCRIPT
	/// literal whose ENTIRE value is an HTML attribute NAME is dropped with
	/// <see cref="ValueVerdict.SkipAttributeName"/>.
	///
	/// Two deliberately NARROW members:
	///   • data-*  — the spec's author-defined custom-data namespace. A literal matching the strict
	///               lowercase-kebab shape ^data-[a-z0-9-]+$ is an attribute NAME (a developer-coined
	///               identifier), never prose. Generic across any site.
	///   • aria-label — by NAME only, via a curated set. NOT an "aria-*" prefix rule: several ARIA
	///               attributes carry human-readable prose values, so a prefix rule could suppress a
	///               real typo. Other aria-* names therefore stay checked here.
	///
	/// Narrowness guards (all must stay CHECKED): uppercase/Title data ("data-Foo"), a word that
	/// merely begins with "data" ("database"), a non-data/aria kebab ("some-kebab-token"), and aria-*
	/// names not on the by-name list ("aria-hidden", "aria-roledescription").
	///
	/// All fixtures are invented, neutral tokens or universal spec names — no real page content.
	/// </summary>
	public class ValueClassifierAttributeNameTests
	{
		private static ValueVerdict Script(string v) => ValueClassifier.ClassifyScriptLiteral(v).Verdict;
		private static bool ScriptChecks(string v) => ValueClassifier.ClassifyScriptLiteral(v).ShouldCheck;

		[Theory]
		[InlineData("data-toggle")]
		[InlineData("data-widget-id")]
		[InlineData("data-foo-bar-baz")]
		[InlineData("data-x")]
		[InlineData("data-12")]
		[InlineData("aria-label")]
		[InlineData("ARIA-LABEL")] // by-name match is case-insensitive
		public void AttributeName_IsSkipped(string literal)
		{
			Assert.Equal(ValueVerdict.SkipAttributeName, Script(literal));
			Assert.False(ScriptChecks(literal));
		}

		// NARROW boundary: things that look adjacent but must stay checked.
		[Theory]
		[InlineData("aria-hidden")]          // aria-* but not on the by-name list
		[InlineData("aria-roledescription")] // prose-valued aria; deliberately not skipped by name
		[InlineData("data-Foo")]             // uppercase — strict lowercase-kebab rule misses it
		[InlineData("database")]             // a word that merely begins with "data" (no hyphen)
		[InlineData("some-kebab-token")]     // a kebab token, but no data-/aria- prefix
		[InlineData("databar")]              // "data" with no hyphen — not the data-* shape
		public void AdjacentButNotAnAttributeName_IsStillChecked(string literal)
		{
			Assert.NotEqual(ValueVerdict.SkipAttributeName, Script(literal));
		}

		// A data: URI is a URL and is caught by the universal gate BEFORE this rule — it is still
		// skipped, just not as an attribute name. Pinned so the interaction is explicit.
		[Fact]
		public void DataUri_IsSkippedAsUrl_NotAttributeName()
		{
			Assert.Equal(ValueVerdict.SkipUrl, Script("data:image/png;base64,AAAA"));
		}
	}
}
