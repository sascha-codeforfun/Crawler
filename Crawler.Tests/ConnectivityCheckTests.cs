using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ConnectivityCheck.BuildFailureLines — the pure helper that
	/// formats a ConnectivityResult into the operator-facing failure block.
	///
	/// The rest of ConnectivityCheck is DNS / HTTP I/O and is intentionally not
	/// unit-tested; this helper is the one piece with real branching:
	///   * proxy lines appear only when ProxyUrl is set,
	///   * resolved-IP lines show "— DNS failed" when the IP array is empty,
	///   * the HEAD status line appears only when HeadStatusCode has a value.
	/// No fixture needed — the helper has no I/O.
	/// </summary>
	public class ConnectivityCheckTests
	{
		private static ConnectivityCheck.ConnectivityResult Result(
			string[]? machineIPs = null,
			string[]? resolvedTargetIPs = null,
			string? proxyUrl = null,
			string[]? resolvedProxyIPs = null,
			int? headStatusCode = null,
			long headResponseMs = 0,
			string? failureReason = "boom")
			=> new(
				IsOk: false,
				MachineName: "BUILD-PC",
				MachineIPs: machineIPs ?? new[] { "10.0.0.5" },
				Username: "svc-crawler",
				TargetUrl: "https://example.com",
				TargetHostname: "example.com",
				ResolvedTargetIPs: resolvedTargetIPs ?? new[] { "93.184.216.34" },
				ProxyUrl: proxyUrl,
				ResolvedProxyIPs: resolvedProxyIPs ?? Array.Empty<string>(),
				HeadStatusCode: headStatusCode,
				HeadResponseMs: headResponseMs,
				FailureReason: failureReason);

		private static string Line(IEnumerable<string> lines, string startsWith)
			=> lines.Single(l => l.StartsWith(startsWith));

		// ── Always-present lines ──────────────────────────────────────────────

		[Fact]
		public void AlwaysIncludes_MachineUsernameTargetAndReason()
		{
			var lines = ConnectivityCheck.BuildFailureLines(Result());

			Assert.Contains(lines, l => l.StartsWith("Machine     :"));
			Assert.Contains(lines, l => l.StartsWith("Machine IPs :"));
			Assert.Contains(lines, l => l.StartsWith("Username    :"));
			Assert.Contains(lines, l => l.StartsWith("Target URL  :"));
			Assert.Contains(lines, l => l.StartsWith("Resolved IPs:"));
			Assert.Contains(lines, l => l.StartsWith("Reason      :"));
		}

		[Fact]
		public void ReasonLine_CarriesFailureReason()
		{
			var lines = ConnectivityCheck.BuildFailureLines(Result(failureReason: "DNS resolution failed"));
			Assert.Contains("DNS resolution failed", Line(lines, "Reason      :"));
		}

		// ── Resolved-IP "DNS failed" placeholder ──────────────────────────────

		[Fact]
		public void ResolvedTargetIPs_Empty_ShowsDnsFailedPlaceholder()
		{
			var lines = ConnectivityCheck.BuildFailureLines(
				Result(resolvedTargetIPs: Array.Empty<string>()));
			Assert.Contains("— DNS failed", Line(lines, "Resolved IPs:"));
		}

		[Fact]
		public void ResolvedTargetIPs_Present_AreJoinedWithCommas()
		{
			var lines = ConnectivityCheck.BuildFailureLines(
				Result(resolvedTargetIPs: new[] { "1.1.1.1", "2.2.2.2" }));
			var line = Line(lines, "Resolved IPs:");
			Assert.Contains("1.1.1.1, 2.2.2.2", line);
			Assert.DoesNotContain("DNS failed", line);
		}

		// ── Proxy branch ──────────────────────────────────────────────────────

		[Fact]
		public void NoProxy_OmitsProxyLines()
		{
			var lines = ConnectivityCheck.BuildFailureLines(Result(proxyUrl: null));
			Assert.DoesNotContain(lines, l => l.StartsWith("Proxy URL   :"));
			Assert.DoesNotContain(lines, l => l.StartsWith("Proxy IPs   :"));
		}

		[Fact]
		public void WithProxy_IncludesProxyLines()
		{
			var lines = ConnectivityCheck.BuildFailureLines(
				Result(proxyUrl: "http://proxy.corp:8080",
					   resolvedProxyIPs: new[] { "10.1.2.3" }));

			Assert.Contains("http://proxy.corp:8080", Line(lines, "Proxy URL   :"));
			Assert.Contains("10.1.2.3", Line(lines, "Proxy IPs   :"));
		}

		[Fact]
		public void WithProxy_ButProxyDnsFailed_ShowsPlaceholder()
		{
			var lines = ConnectivityCheck.BuildFailureLines(
				Result(proxyUrl: "http://proxy.corp:8080",
					   resolvedProxyIPs: Array.Empty<string>()));
			Assert.Contains("— DNS failed", Line(lines, "Proxy IPs   :"));
		}

		// ── HEAD status branch ────────────────────────────────────────────────

		[Fact]
		public void NoHeadStatus_OmitsHeadLine()
		{
			var lines = ConnectivityCheck.BuildFailureLines(Result(headStatusCode: null));
			Assert.DoesNotContain(lines, l => l.StartsWith("HEAD status :"));
		}

		[Fact]
		public void WithHeadStatus_IncludesStatusAndTiming()
		{
			var lines = ConnectivityCheck.BuildFailureLines(
				Result(headStatusCode: 503, headResponseMs: 1234));
			var line = Line(lines, "HEAD status :");
			Assert.Contains("503", line);
			Assert.Contains("1234 ms", line);
		}

		// ── Combined: full failure with everything present ────────────────────

		[Fact]
		public void FullFailure_ProducesAllSixSectionsPlusProxyAndHead()
		{
			var lines = ConnectivityCheck.BuildFailureLines(
				Result(proxyUrl: "http://proxy:8080",
					   resolvedProxyIPs: new[] { "10.1.2.3" },
					   headStatusCode: 502,
					   headResponseMs: 99));

			// 5 base + Reason + Proxy URL + Proxy IPs + HEAD = 9 lines.
			Assert.Equal(9, lines.Count);
		}
	}
}
