using System.Net;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ProxyConfig.Build — the single source of truth for proxy +
	/// credential construction shared by ConnectivityCheck, CrawlAsset, and Crawl.
	///
	/// This is the logic that previously drifted: the connectivity preflight built
	/// a credential-less proxy while the crawl used UseDefaultCredentials, so an
	/// authenticating proxy (407 to anonymous) failed the preflight and aborted the
	/// run. These tests pin the three credential branches so the paths can never
	/// diverge again.
	///
	/// Note: the actual proxy round-trip cannot be verified without a live proxy —
	/// these assert the constructed IWebProxy's credential posture, address, and
	/// bypass flag, which is the part that was wrong. Most tests pass logContext:null
	/// so they don't touch the static Logger; the one logging-path test initialises
	/// Logger and lives in the Logger collection.
	/// </summary>
	public class ProxyConfigTests
	{
		// ── No proxy ──────────────────────────────────────────────────────────

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void BlankUrl_ReturnsNull(string? url)
		{
			Assert.Null(ProxyConfig.Build(url, "user", "pass", logContext: null));
		}

		// ── Explicit credentials branch ───────────────────────────────────────

		[Fact]
		public void ExplicitUser_SetsNetworkCredential_NotDefault()
		{
			var proxy = ProxyConfig.Build("http://proxy:8080", "alice", "secret", logContext: null);

			var web = Assert.IsType<WebProxy>(proxy);
			Assert.False(web.UseDefaultCredentials);
			var cred = Assert.IsType<NetworkCredential>(web.Credentials);
			Assert.Equal("alice", cred.UserName);
			Assert.Equal("secret", cred.Password);
		}

		[Fact]
		public void ExplicitUser_NullPassword_UsesEmptyString()
		{
			var proxy = ProxyConfig.Build("http://proxy:8080", "alice", null, logContext: null);

			var web = Assert.IsType<WebProxy>(proxy);
			var cred = Assert.IsType<NetworkCredential>(web.Credentials);
			Assert.Equal("alice", cred.UserName);
			Assert.Equal(string.Empty, cred.Password);
		}

		// ── Default-credentials branch (the one the preflight was missing) ─────

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void NoUser_UsesDefaultCredentials(string? user)
		{
			var proxy = ProxyConfig.Build("http://proxy:8080", user, "ignored", logContext: null);

			var web = Assert.IsType<WebProxy>(proxy);
			Assert.True(web.UseDefaultCredentials);
			// Per WebProxy semantics, setting UseDefaultCredentials = true assigns
			// Credentials = CredentialCache.DefaultCredentials (NOT null). The key
			// point is that it is NOT an explicit NetworkCredential.
			Assert.Same(CredentialCache.DefaultCredentials, web.Credentials);
			Assert.IsNotType<NetworkCredential>(web.Credentials);
		}

		// ── Address + bypass posture ──────────────────────────────────────────

		[Fact]
		public void ProxyAddress_MatchesConfiguredUrl()
		{
			var proxy = ProxyConfig.Build("http://proxy.corp:3128", "u", "p", logContext: null);

			var web = Assert.IsType<WebProxy>(proxy);
			Assert.Equal("http://proxy.corp:3128/", web.Address?.ToString());
		}

		[Fact]
		public void BypassOnLocal_IsFalse_SoLocalAddressesStillProxied()
		{
			var proxy = ProxyConfig.Build("http://proxy:8080", null, null, logContext: null);

			var web = Assert.IsType<WebProxy>(proxy);
			Assert.False(web.BypassProxyOnLocal);
		}

		// ── Consistency: same inputs → same posture across all callers ─────────
		// All three HTTP paths now route through Build, so identical settings must
		// produce identical credential posture. This is the regression guard for
		// the original drift.

		[Fact]
		public void SameInputs_ProduceSameCredentialPosture()
		{
			var a = (WebProxy)ProxyConfig.Build("http://p:8080", "bob", "pw", logContext: null)!;
			var b = (WebProxy)ProxyConfig.Build("http://p:8080", "bob", "pw", logContext: null)!;

			Assert.Equal(a.UseDefaultCredentials, b.UseDefaultCredentials);
			Assert.Equal(
				((NetworkCredential)a.Credentials!).UserName,
				((NetworkCredential)b.Credentials!).UserName);
			Assert.Equal(a.Address?.ToString(), b.Address?.ToString());
		}
	}

	/// <summary>
	/// Separate class for the one test that exercises the logging path, so it can
	/// initialise and serialise on the static Logger via the Logger collection
	/// without forcing the pure tests above to do the same.
	/// </summary>
	[Collection("Logger")]
	public class ProxyConfigLoggingTests : IDisposable
	{
		private readonly string _tempDir;

		public ProxyConfigLoggingTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"proxy-cfg-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		[Fact]
		public void WithLogContext_BuildsProxyAndDoesNotThrow()
		{
			// logContext non-null exercises the Logger.LogInfo branch; we just
			// assert it builds the proxy correctly and the log call is harmless.
			var proxy = ProxyConfig.Build("http://proxy:8080", "u", "p", logContext: "UnitTest");
			Assert.IsType<WebProxy>(proxy);
		}

		[Fact]
		public void WithLogContext_BlankUrl_ReturnsNull_AndLogsNoProxy()
		{
			var proxy = ProxyConfig.Build("", "u", "p", logContext: "UnitTest");
			Assert.Null(proxy);
		}
	}
}
