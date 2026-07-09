using System.Collections.Generic;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// 654 — pins the config-summary dictionary labels: "DisplayName (code)", sorted by code, with a
	/// bare-code fallback when a name is somehow absent. Fixtures are generic.
	/// </summary>
	public class ConfigSummaryDictionaryLabelsTests
	{
		private static DictionaryBundleConfig B(string lang, string display)
			=> new() { LanguageCode = lang, DisplayName = display };

		[Fact]
		public void NamedBundles_RenderAsDisplayNameWithCode()
		{
			var labels = ConfigSummary.FormatDictionaryLabels(new List<DictionaryBundleConfig>
			{
				B("de", "German"),
				B("en", "English"),
			});

			Assert.Equal(new[] { "German (de)", "English (en)" }, labels);
		}

		[Fact]
		public void SortIsByCode_NotByDisplayName()
		{
			// Input out of order; expect code-order de < en < pt, regardless of the names.
			var labels = ConfigSummary.FormatDictionaryLabels(new List<DictionaryBundleConfig>
			{
				B("pt", "Portuguese"),
				B("de", "German"),
				B("en", "English"),
			});

			Assert.Equal(new[] { "German (de)", "English (en)", "Portuguese (pt)" }, labels);
		}

		[Fact]
		public void MissingDisplayName_FallsBackToBareCode()
		{
			var labels = ConfigSummary.FormatDictionaryLabels(new List<DictionaryBundleConfig>
			{
				B("sk", ""),          // no name yet
				B("de", "German"),
			});

			Assert.Equal(new[] { "German (de)", "sk" }, labels);
		}

		[Fact]
		public void EmptyLanguageCode_IsDropped()
		{
			var labels = ConfigSummary.FormatDictionaryLabels(new List<DictionaryBundleConfig>
			{
				B("", "Orphan"),
				B("de", "German"),
			});

			Assert.Equal(new[] { "German (de)" }, labels);
		}
	}
}
