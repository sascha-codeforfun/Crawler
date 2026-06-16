using System;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// 650 — pins the operator-facing SETUP halt behaviour. The empty-dictionary file-scan halt must
	/// surface as a <see cref="ConfigHaltException"/> carrying structured, teaching content (so the
	/// entry point renders a calm action screen, not a raw dump), and the exception must stay a usable
	/// plain-message exception for logs/silent runs. Fixtures are generic — no real site content.
	/// </summary>
	public class ConfigHaltScreenTests
	{
		private static Config WithJsScan(bool on, params string[] dictionaries)
		{
			var config = new Config { Url = "https://example.test" };
			var js = config.SpellCheckEngine.SpellCheckJavaScript;
			js.ScanScriptFilesInDownload = on;
			js.ScriptFileScanDictionaries.Clear();
			js.ScriptFileScanDictionaries.AddRange(dictionaries);
			return config;
		}

		[Fact]
		public void JsFileScanEnabled_NoDictionaries_HaltsAsConfigHaltException_WithTeachingContent()
		{
			var config = WithJsScan(on: true); // dictionaries empty

			var ex = Assert.Throws<ConfigHaltException>(() => Config.ValidateResolvedSite(config));

			// A setup-shaped heading (action, not failure) and the actionable specifics.
			Assert.Contains("SETUP NEEDED", ex.Heading);
			Assert.Contains("JS-file scan", ex.Heading);
			Assert.Contains("ScriptFileScanDictionaries", ex.Message);
			Assert.Contains("ScanScriptFilesInDownload", ex.Message);
			// The teaching point — opinion-free by design — must be present.
			Assert.Contains("opinion-free", ex.Message);
			Assert.NotEmpty(ex.Lines);
		}

		[Fact]
		public void JsFileScanEnabled_WithDictionary_DoesNotRaiseSetupHalt()
		{
			var config = WithJsScan(on: true, "de");

			// May still throw a later, unrelated validation error for this minimal config, but it must
			// NOT be the JS-file-scan setup halt — naming a dictionary satisfies that check.
			var ex = Record.Exception(() => Config.ValidateResolvedSite(config));
			Assert.True(ex is null or not ConfigHaltException);
		}

		[Fact]
		public void ConfigHaltException_CarriesHeadingAndLines_AndFlattensIntoMessage()
		{
			var halt = new ConfigHaltException("HEAD", new[] { "line one", "line two" });

			Assert.Equal("HEAD", halt.Heading);
			Assert.Equal(2, halt.Lines.Count);
			// Flattened message keeps it usable as a plain exception (log/silent run/test).
			Assert.Contains("HEAD", halt.Message);
			Assert.Contains("line one", halt.Message);
			Assert.Contains("line two", halt.Message);
			// It is an InvalidOperationException, so existing config-halt handling treats it uniformly.
			Assert.IsAssignableFrom<InvalidOperationException>(halt);
		}
	}
}
