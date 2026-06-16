using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the URL-manipulation helpers in Tools: RemoveQueryStringElements,
	/// RemoveQueryString, ExtractModalUrl, GenerateFileName, GetHash.
	///
	/// These are pure functions; no temp dir or logger setup required.
	/// </summary>
	[Collection("Logger")]
	public class ToolsUrlTests : IDisposable
	{
		private readonly string _logFile;

		public ToolsUrlTests()
		{
			// Several helpers log on error paths — initialise the logger so the
			// "Logger not initialised" guard does not fire mid-test.
			_logFile = Path.GetTempFileName();
			Logger.Initialize(_logFile, silent: true);
		}

		public void Dispose()
		{
			try { File.Delete(_logFile); } catch { }
		}

		// ── RemoveQueryStringElements ───────────────────────────────────────

		[Fact]
		public void RemoveQueryStringElements_RemovesSingleKey()
		{
			var result = Tools.RemoveQueryStringElements(
				"https://example.com/page?a=1&b=2&c=3", "b");

			Assert.Contains("a=1", result);
			Assert.Contains("c=3", result);
			Assert.DoesNotContain("b=2", result);
		}

		[Fact]
		public void RemoveQueryStringElements_RemovesMultiplePipeSeparatedKeys()
		{
			var result = Tools.RemoveQueryStringElements(
				"https://example.com/page?a=1&b=2&c=3&d=4", "a|c");

			Assert.DoesNotContain("a=1", result);
			Assert.Contains("b=2", result);
			Assert.DoesNotContain("c=3", result);
			Assert.Contains("d=4", result);
		}

		[Fact]
		public void RemoveQueryStringElements_LeavesUrlUntouchedWhenKeyAbsent()
		{
			var result = Tools.RemoveQueryStringElements(
				"https://example.com/page?a=1", "missing");

			Assert.Contains("a=1", result);
		}

		[Fact]
		public void RemoveQueryStringElements_NoQueryString_ReturnsUrlUnchanged()
		{
			var result = Tools.RemoveQueryStringElements(
				"https://example.com/page", "anykey");

			// Note: UriBuilder.ToString() inserts the explicit default port
			// (e.g. ":443" for https) — host+path are preserved, no query added.
			// This differs from RemoveQueryString which strips the default port.
			// Quirk documented in notes-for-later.
			Assert.Contains("example.com", result);
			Assert.Contains("/page", result);
			Assert.DoesNotContain("?", result);
		}

		[Fact]
		public void RemoveQueryStringElements_RelativeUrl_ReturnsAsIs()
		{
			// Relative URLs don't reach the UriBuilder path — function returns
			// the input unchanged and logs an error.
			const string relative = "/page?a=1&b=2";
			var result = Tools.RemoveQueryStringElements(relative, "a");

			Assert.Equal(relative, result);
		}

		[Fact]
		public void RemoveQueryStringElements_ThrowsOnEmptyUrl()
		{
			Assert.Throws<ArgumentException>(() =>
				Tools.RemoveQueryStringElements("", "key"));
		}

		[Fact]
		public void RemoveQueryStringElements_ThrowsOnNullUrl()
		{
			Assert.Throws<ArgumentException>(() =>
				Tools.RemoveQueryStringElements(null!, "key"));
		}

		// ── RemoveQueryString ───────────────────────────────────────────────

		[Fact]
		public void RemoveQueryString_StripsQueryParameters()
		{
			var result = Tools.RemoveQueryString("https://example.com/page?a=1&b=2");

			Assert.Equal("https://example.com/page", result);
		}

		[Fact]
		public void RemoveQueryString_LeavesUrlWithoutQueryUnchanged()
		{
			var result = Tools.RemoveQueryString("https://example.com/page");

			Assert.Equal("https://example.com/page", result);
		}

		[Fact]
		public void RemoveQueryString_OmitsDefaultPort()
		{
			// Port 443 on https is the default — Uri.IsDefaultPort returns true and
			// the rebuilt URL has no explicit port. This was the original intent of
			// the manual scheme://host{:port?}{path} reconstruction.
			var result = Tools.RemoveQueryString("https://example.com:443/page?x=1");

			Assert.Equal("https://example.com/page", result);
		}

		[Fact]
		public void RemoveQueryString_PreservesNonDefaultPort()
		{
			var result = Tools.RemoveQueryString("https://example.com:8443/page?x=1");

			Assert.Equal("https://example.com:8443/page", result);
		}

		// ── ExtractModalUrl ─────────────────────────────────────────────────

		[Fact]
		public void ExtractModalUrl_DecodesEncodedAbsoluteUrl()
		{
			// Modal URLs carry the target URL URL-encoded inside a parameter.
			var carrier = "https://www.example.com/page?modal=https%3A%2F%2Fwww.example.com%2Ftarget";

			var result = Tools.ExtractModalUrl(carrier, "https://www.example.com", "modal");

			Assert.Equal("https://www.example.com/target", result);
		}

		[Fact]
		public void ExtractModalUrl_ResolvesRelativeTargetWithBaseUrl()
		{
			// When the encoded modal value is a relative path, the function prefixes
			// it with baseUrl (with appropriate slash handling).
			var carrier = "https://www.example.com/page?modal=%2Ftarget-relative";

			var result = Tools.ExtractModalUrl(carrier, "https://www.example.com", "modal");

			Assert.Equal("https://www.example.com/target-relative", result);
		}

		[Fact]
		public void ExtractModalUrl_StripsQueryFromDecodedTarget()
		{
			// The decoded target may have its own query string — ExtractModalUrl
			// calls RemoveQueryString on the result.
			var carrier = "https://www.example.com/page?modal=https%3A%2F%2Fwww.example.com%2Ftarget%3Fx%3D1";

			var result = Tools.ExtractModalUrl(carrier, "https://www.example.com", "modal");

			Assert.Equal("https://www.example.com/target", result);
		}

		[Fact]
		public void ExtractModalUrl_ParameterMissing_ReturnsCarrierWithoutQuery()
		{
			var carrier = "https://www.example.com/page?other=value";

			var result = Tools.ExtractModalUrl(carrier, "https://www.example.com", "modal");

			Assert.Equal("https://www.example.com/page", result);
		}

		// ── GenerateFileName ────────────────────────────────────────────────
		//
		// GenerateFileName routes through two different code paths depending on
		// whether the URL has a query string. The tests document the observed
		// behaviour rather than asserting a clean ideal — see notes-for-later
		// regarding the no-query path's extension-detection quirk.

		[Fact]
		public void GenerateFileName_NoExtension_GetsHtmlxAppendedThenHashed()
		{
			// AbsolutePath "/page" has no extension → fileName += ".htmlx".
			// Then the no-query branch overwrites with hash + extension from
			// segments — but no segment contains a dot, so extension stays ".htm".
			var fn = Tools.GenerateFileName("https://example.com/page");

			Assert.EndsWith(".htm", fn);
			Assert.NotEqual(".htm", fn);
		}

		[Fact]
		public void GenerateFileName_PathWithHtmlExtension_NoQuery_UsesSegmentAsExtension()
		{
			// Documented quirk: the no-query branch sets extension = "<segment-with-dot>"
			// for the last segment containing a dot. The result is therefore
			// "<hash>page.html" — extension is the whole final segment.
			var fn = Tools.GenerateFileName("https://example.com/folder/page.html");

			Assert.EndsWith("page.html", fn);
		}

		[Fact]
		public void GenerateFileName_WithQuery_ReturnsAbsolutePathPlusHtmlx()
		{
			// When uri.Query.Length != 0, the function returns the (possibly
			// .htmlx-suffixed) AbsolutePath without applying the hash branch.
			var fn = Tools.GenerateFileName("https://example.com/page?a=1");

			Assert.Equal("/page.htmlx", fn);
		}

		[Fact]
		public void GenerateFileName_WithQueryAndExistingExtension_ReturnsAbsolutePathUnchanged()
		{
			var fn = Tools.GenerateFileName("https://example.com/page.html?a=1");

			Assert.Equal("/page.html", fn);
		}

		// ── GetHash ─────────────────────────────────────────────────────────

		[Fact]
		public void GetHash_IsStableForSameInput()
		{
			var h1 = Tools.GetHash("https://example.com/page");
			var h2 = Tools.GetHash("https://example.com/page");

			Assert.Equal(h1, h2);
		}

		[Fact]
		public void GetHash_DiffersForDifferentInputs()
		{
			var h1 = Tools.GetHash("https://example.com/page-a");
			var h2 = Tools.GetHash("https://example.com/page-b");

			Assert.NotEqual(h1, h2);
		}

		[Fact]
		public void GetHash_ReturnsSha256HexString_64Chars()
		{
			var h = Tools.GetHash("anything");

			Assert.Equal(64, h.Length);
			Assert.Matches("^[0-9a-f]+$", h);
		}

		[Fact]
		public void GetHash_ThrowsOnEmptyInput()
		{
			Assert.Throws<ArgumentException>(() => Tools.GetHash(""));
		}

		[Fact]
		public void GetHash_ThrowsOnNullInput()
		{
			Assert.Throws<ArgumentException>(() => Tools.GetHash(null!));
		}
	}
}
