using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ConnectivityCheck.RunAsync's silent-mode failure paths, which are
	/// reachable without external network or console input:
	///
	///   • An invalid target URL fails at Uri parsing, before any DNS/HTTP, so the
	///     check returns false and the failure is appended to the log. This drives
	///     CheckAsync's identity gathering (GetUsername / GetMachineIPs), the
	///     invalid-URL Fail, AppendToLog, and RunAsync's silent-fail branch.
	///   • A localhost target with an invalid proxy URL resolves the target locally
	///     then fails parsing the proxy, exercising the proxy-failure Fail and
	///     AppendToLog's proxy lines.
	///
	/// The HTTP HEAD request, the success path, and the interactive [C]ontinue /
	/// [A]bort prompt all require real network or console input and are left
	/// uncovered. silent:true avoids the ReadKey branch. SYNTHETIC inputs.
	/// </summary>
	[Collection("Logger")]
	public class ConnectivityCheckRunTests : IDisposable
	{
		private readonly string _dir;

		public ConnectivityCheckRunTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"connchk-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		[Fact]
		public async Task RunAsync_InvalidTargetUrl_Silent_ReturnsFalseAndLogsFailure()
		{
			var log = Path.Combine(_dir, "connectivity.log");

			var ok = await ConnectivityCheck.RunAsync(
				"not a valid url", proxyUrl: null, log, silent: true);

			Assert.False(ok);
			Assert.True(File.Exists(log));
			var text = File.ReadAllText(log);
			Assert.Contains("FAILED", text);
			Assert.Contains("Invalid target URL", text);
		}

		[Fact]
		public async Task RunAsync_InvalidProxyUrl_Silent_ReturnsFalseAndLogsProxy()
		{
			var log = Path.Combine(_dir, "connectivity-proxy.log");

			// Target resolves locally (no external network); the invalid proxy URL
			// fails parsing, so the result carries the proxy and the log records it.
			var ok = await ConnectivityCheck.RunAsync(
				"http://localhost/", proxyUrl: "not a valid proxy url", log, silent: true);

			Assert.False(ok);
			var text = File.ReadAllText(log);
			Assert.Contains("FAILED", text);
			Assert.Contains("Proxy URL", text);
		}
	}
}
