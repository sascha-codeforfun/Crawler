using Xunit;
using Outcome = Crawler.ProxyCredentialResolution.Outcome;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ProxyCredentialResolution.Decide — the pure decision for how a
	/// run obtains its proxy credentials, given run mode and config alone (before
	/// any operator interaction).
	///
	/// These pin the resolution model agreed in #410:
	///   * Silent / no-proxy / proxy-off → UseAsConfigured, no prompt (silent must
	///     never block on input; no-proxy has nothing to resolve).
	///   * Interactive + proxy, config blank → PromptFresh.
	///   * Interactive + proxy, config populated → OfferUseOrOverride (config is a
	///     default the operator can accept or override — never a lock, because
	///     config-held credentials are bridging tech and manual input must stay
	///     reachable).
	///
	/// The console I/O for the interactive cases lives in Program
	/// (ResolveProxyCredentials / PromptForProxyCredentials) and is
	/// operator-eyeball-verified, not unit-tested — only the decision is pinned here.
	/// </summary>
	public class ProxyCredentialResolutionTests
	{
		// ── Silent mode: config wins, never prompts ───────────────────────────

		[Theory]
		[InlineData("user", "pass")]
		[InlineData("", "")]
		[InlineData("user", "")]
		public void Silent_AlwaysUsesConfigured_NeverPrompts(string user, string pass)
		{
			var d = ProxyCredentialResolution.Decide(
				silent: true, useProxy: true, proxyUrl: "http://proxy:8080",
				configUser: user, configPassword: pass);

			Assert.Equal(Outcome.UseAsConfigured, d.Outcome);
			Assert.Equal(user, d.User);
			Assert.Equal(pass, d.Password);
		}

		// ── No proxy / proxy off: nothing to resolve ──────────────────────────

		[Fact]
		public void ProxyDisabled_UsesConfigured_EvenInteractive()
		{
			var d = ProxyCredentialResolution.Decide(
				silent: false, useProxy: false, proxyUrl: "http://proxy:8080",
				configUser: "user", configPassword: "pass");

			Assert.Equal(Outcome.UseAsConfigured, d.Outcome);
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void BlankProxyUrl_UsesConfigured_EvenInteractive(string? url)
		{
			var d = ProxyCredentialResolution.Decide(
				silent: false, useProxy: true, proxyUrl: url,
				configUser: "user", configPassword: "pass");

			Assert.Equal(Outcome.UseAsConfigured, d.Outcome);
		}

		// ── Interactive + proxy, config blank: prompt fresh ───────────────────

		[Theory]
		[InlineData(null, null)]
		[InlineData("", "")]
		[InlineData("   ", "   ")]
		public void Interactive_BlankConfig_PromptsFresh(string? user, string? pass)
		{
			var d = ProxyCredentialResolution.Decide(
				silent: false, useProxy: true, proxyUrl: "http://proxy:8080",
				configUser: user, configPassword: pass);

			Assert.Equal(Outcome.PromptFresh, d.Outcome);
		}

		// ── Interactive + proxy, config populated: offer use-or-override ──────

		[Theory]
		[InlineData("user", "pass")]  // both set
		[InlineData("user", "")]      // user only
		[InlineData("", "pass")]      // password only (still "configured")
		public void Interactive_PopulatedConfig_OffersUseOrOverride(string user, string pass)
		{
			var d = ProxyCredentialResolution.Decide(
				silent: false, useProxy: true, proxyUrl: "http://proxy:8080",
				configUser: user, configPassword: pass);

			Assert.Equal(Outcome.OfferUseOrOverride, d.Outcome);
			Assert.Equal(user, d.User);
			Assert.Equal(pass, d.Password);
		}

		// ── Null config inputs normalise to empty strings ─────────────────────

		[Fact]
		public void NullConfigInputs_NormaliseToEmpty_OnUseAsConfigured()
		{
			var d = ProxyCredentialResolution.Decide(
				silent: true, useProxy: true, proxyUrl: "http://proxy:8080",
				configUser: null, configPassword: null);

			Assert.Equal(Outcome.UseAsConfigured, d.Outcome);
			Assert.Equal(string.Empty, d.User);
			Assert.Equal(string.Empty, d.Password);
		}
	}
}
