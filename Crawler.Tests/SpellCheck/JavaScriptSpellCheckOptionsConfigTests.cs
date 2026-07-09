using System.Text.Json;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the SpellCheckJavaScript config shape. As of the breaking change, the key is an OBJECT
	/// (<c>{ "Enabled": …, "TokensToFilter": [ … ] }</c>), not the former plain bool. Deserialization
	/// mirrors the engine's loader (plain System.Text.Json, default options, PascalCase names).
	/// </summary>
	public class JavaScriptSpellCheckOptionsConfigTests
	{
		private static SpellCheckEngineConfig Bind(string json) =>
			JsonSerializer.Deserialize<SpellCheckEngineConfig>(json)!;

		[Fact]
		public void ObjectForm_BindsEnabledAndTokens()
		{
			var cfg = Bind("{ \"SpellCheckJavaScript\": { \"Enabled\": true, \"TokensToFilter\": [ \"alpha\", \"beta\" ] } }");
			Assert.True(cfg.SpellCheckJavaScript.Enabled);
			Assert.Equal(new[] { "alpha", "beta" }, cfg.SpellCheckJavaScript.TokensToFilter);
		}

		[Fact]
		public void AbsentKey_DefaultsToDisabledWithEmptyTokens()
		{
			var cfg = Bind("{ }");
			Assert.NotNull(cfg.SpellCheckJavaScript);
			Assert.False(cfg.SpellCheckJavaScript.Enabled);
			Assert.Empty(cfg.SpellCheckJavaScript.TokensToFilter);
		}

		[Fact]
		public void EnabledOnly_LeavesTokensEmpty()
		{
			var cfg = Bind("{ \"SpellCheckJavaScript\": { \"Enabled\": true } }");
			Assert.True(cfg.SpellCheckJavaScript.Enabled);
			Assert.Empty(cfg.SpellCheckJavaScript.TokensToFilter);
		}

		[Fact]
		public void LegacyBoolForm_NoLongerBinds()
		{
			// BREAKING (documented): the old "SpellCheckJavaScript": true no longer deserializes —
			// a bool where an object is expected throws. Pinned so the break is intentional, not silent.
			Assert.Throws<JsonException>(() =>
				JsonSerializer.Deserialize<SpellCheckEngineConfig>("{ \"SpellCheckJavaScript\": true }"));
		}
	}
}
