using Xunit;
using System.Text.Json;
using Crawler.Urls;

namespace Crawler.Tests.Urls
{
	/// <summary>
	/// Tests for the URL query-string helpers in <see cref="Query"/>:
	/// RemoveQueryStringElements, RemoveQueryString, ExtractModalUrl — plus the
	/// <see cref="ModalQueryParam"/> record/converter and its suffix behavior.
	///
	/// RemoveQueryStringElements logs on its relative-URL error path, so the
	/// logger is initialised for the class — the "Logger not initialised" guard
	/// must not fire mid-test.
	/// </summary>
	[Collection("Logger")]
	public class QueryTests : IDisposable
	{
		private readonly string _logFile;

		public QueryTests()
		{
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
			var result = Query.RemoveQueryStringElements(
				"https://example.com/page?a=1&b=2&c=3", "b");

			Assert.Contains("a=1", result);
			Assert.Contains("c=3", result);
			Assert.DoesNotContain("b=2", result);
		}

		[Fact]
		public void RemoveQueryStringElements_RemovesMultiplePipeSeparatedKeys()
		{
			var result = Query.RemoveQueryStringElements(
				"https://example.com/page?a=1&b=2&c=3&d=4", "a|c");

			Assert.DoesNotContain("a=1", result);
			Assert.Contains("b=2", result);
			Assert.DoesNotContain("c=3", result);
			Assert.Contains("d=4", result);
		}

		[Fact]
		public void RemoveQueryStringElements_LeavesUrlUntouchedWhenKeyAbsent()
		{
			var result = Query.RemoveQueryStringElements(
				"https://example.com/page?a=1", "missing");

			Assert.Contains("a=1", result);
		}

		[Fact]
		public void RemoveQueryStringElements_NoQueryString_ReturnsUrlUnchanged()
		{
			var result = Query.RemoveQueryStringElements(
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
			var result = Query.RemoveQueryStringElements(relative, "a");

			Assert.Equal(relative, result);
		}

		[Fact]
		public void RemoveQueryStringElements_ThrowsOnEmptyUrl()
		{
			Assert.Throws<ArgumentException>(() =>
				Query.RemoveQueryStringElements("", "key"));
		}

		[Fact]
		public void RemoveQueryStringElements_ThrowsOnNullUrl()
		{
			Assert.Throws<ArgumentException>(() =>
				Query.RemoveQueryStringElements(null!, "key"));
		}

		// ── RemoveQueryString ───────────────────────────────────────────────

		[Fact]
		public void RemoveQueryString_StripsQueryParameters()
		{
			var result = Query.RemoveQueryString("https://example.com/page?a=1&b=2");

			Assert.Equal("https://example.com/page", result);
		}

		[Fact]
		public void RemoveQueryString_LeavesUrlWithoutQueryUnchanged()
		{
			var result = Query.RemoveQueryString("https://example.com/page");

			Assert.Equal("https://example.com/page", result);
		}

		[Fact]
		public void RemoveQueryString_OmitsDefaultPort()
		{
			// Port 443 on https is the default — Uri.IsDefaultPort returns true and
			// the rebuilt URL has no explicit port. This was the original intent of
			// the manual scheme://host{:port?}{path} reconstruction.
			var result = Query.RemoveQueryString("https://example.com:443/page?x=1");

			Assert.Equal("https://example.com/page", result);
		}

		[Fact]
		public void RemoveQueryString_PreservesNonDefaultPort()
		{
			var result = Query.RemoveQueryString("https://example.com:8443/page?x=1");

			Assert.Equal("https://example.com:8443/page", result);
		}

		// ── ExtractModalUrl (legacy behavior — no suffix) ───────────────────
		//
		// A bare ModalQueryParam("modal") has AppendSuffix == "" and MUST reproduce
		// the pre-suffix output exactly. These four are the golden non-breaking
		// tests: they assert the same results the string-parameter overload did.

		[Fact]
		public void ExtractModalUrl_DecodesEncodedAbsoluteUrl()
		{
			// Modal URLs carry the target URL URL-encoded inside a parameter.
			var carrier = "https://www.example.com/page?modal=https%3A%2F%2Fwww.example.com%2Ftarget";

			var result = Query.ExtractModalUrl(carrier, "https://www.example.com", new ModalQueryParam("modal"));

			Assert.Equal("https://www.example.com/target", result);
		}

		[Fact]
		public void ExtractModalUrl_ResolvesRelativeTargetWithBaseUrl()
		{
			// When the encoded modal value is a relative path, the function prefixes
			// it with baseUrl (with appropriate slash handling).
			var carrier = "https://www.example.com/page?modal=%2Ftarget-relative";

			var result = Query.ExtractModalUrl(carrier, "https://www.example.com", new ModalQueryParam("modal"));

			Assert.Equal("https://www.example.com/target-relative", result);
		}

		[Fact]
		public void ExtractModalUrl_StripsQueryFromDecodedTarget()
		{
			// The decoded target may have its own query string — ExtractModalUrl
			// calls RemoveQueryString on the result.
			var carrier = "https://www.example.com/page?modal=https%3A%2F%2Fwww.example.com%2Ftarget%3Fx%3D1";

			var result = Query.ExtractModalUrl(carrier, "https://www.example.com", new ModalQueryParam("modal"));

			Assert.Equal("https://www.example.com/target", result);
		}

		[Fact]
		public void ExtractModalUrl_ParameterMissing_ReturnsCarrierWithoutQuery()
		{
			var carrier = "https://www.example.com/page?other=value";

			var result = Query.ExtractModalUrl(carrier, "https://www.example.com", new ModalQueryParam("modal"));

			Assert.Equal("https://www.example.com/page", result);
		}

		// ── ExtractModalUrl (new: AppendSuffix restores the page extension) ──

		[Fact]
		public void ExtractModalUrl_AppendsSuffixToExtensionlessTarget()
		{
			// Carrier's modal param value is an extension-less path (+ its own inner
			// query). With AppendSuffix ".html" the decoded, query-stripped target
			// gets the page extension so it resolves to the real page rather than the
			// bare (non-resolving) node form.
			var carrier =
				"https://www.example.com/section/carrier.html" +
				"?modal=%2Fsection%2Fcarrier%2Flightboxes%2Ffragment%3Fn%3Dtrue%26stref%3Dbox";

			var result = Query.ExtractModalUrl(
				carrier, "https://www.example.com", new ModalQueryParam("modal", ".html"));

			Assert.Equal(
				"https://www.example.com/section/carrier/lightboxes/fragment.html",
				result);
		}

		[Fact]
		public void ExtractModalUrl_SuffixIsIdempotentWhenTargetAlreadyHasExtension()
		{
			// Decoded target already ends in .html — must NOT become .html.html.
			var carrier = "https://www.example.com/page?modal=%2Fsome%2Fpage.html";

			var result = Query.ExtractModalUrl(
				carrier, "https://www.example.com", new ModalQueryParam("modal", ".html"));

			Assert.Equal("https://www.example.com/some/page.html", result);
		}

		[Fact]
		public void ExtractModalUrl_SuffixNotAppendedToDirectoryForm()
		{
			// Target ends in '/' (directory form) — suffix must not be appended.
			var carrier = "https://www.example.com/page?modal=%2Fsome%2Fdir%2F";

			var result = Query.ExtractModalUrl(
				carrier, "https://www.example.com", new ModalQueryParam("modal", ".html"));

			Assert.Equal("https://www.example.com/some/dir/", result);
		}

		[Fact]
		public void ExtractModalUrl_EmptySuffixLeavesExtensionlessTargetUnchanged()
		{
			// Explicit empty suffix == legacy behavior: extension-less stays extension-less.
			var carrier = "https://www.example.com/page?modal=%2Flightboxes%2Fbox1";

			var result = Query.ExtractModalUrl(
				carrier, "https://www.example.com", new ModalQueryParam("modal", ""));

			Assert.Equal("https://www.example.com/lightboxes/box1", result);
		}

		// ── ModalQueryParam converter (string | object, non-breaking) ───────

		[Fact]
		public void Converter_DeserializesBareStringAsParamWithEmptySuffix()
		{
			var list = JsonSerializer.Deserialize<List<ModalQueryParam>>("[\"lightbox\"]");

			Assert.NotNull(list);
			Assert.Single(list!);
			Assert.Equal("lightbox", list![0].Param);
			Assert.Equal("", list[0].AppendSuffix);
		}

		[Fact]
		public void Converter_DeserializesObjectWithSuffix()
		{
			var list = JsonSerializer.Deserialize<List<ModalQueryParam>>(
				"[{\"Param\":\"lightbox\",\"AppendSuffix\":\".html\"}]");

			Assert.NotNull(list);
			Assert.Single(list!);
			Assert.Equal("lightbox", list![0].Param);
			Assert.Equal(".html", list[0].AppendSuffix);
		}

		[Fact]
		public void Converter_DeserializesMixedArray()
		{
			var list = JsonSerializer.Deserialize<List<ModalQueryParam>>(
				"[\"overlay\",{\"Param\":\"lightbox\",\"AppendSuffix\":\".html\"}]");

			Assert.NotNull(list);
			Assert.Equal(2, list!.Count);
			Assert.Equal("overlay", list[0].Param);
			Assert.Equal("", list[0].AppendSuffix);
			Assert.Equal("lightbox", list[1].Param);
			Assert.Equal(".html", list[1].AppendSuffix);
		}

		[Fact]
		public void Converter_ObjectMissingParam_Throws()
		{
			Assert.Throws<JsonException>(() =>
				JsonSerializer.Deserialize<List<ModalQueryParam>>("[{\"AppendSuffix\":\".html\"}]"));
		}

		[Fact]
		public void Converter_EmptyStringParam_Throws()
		{
			Assert.Throws<JsonException>(() =>
				JsonSerializer.Deserialize<List<ModalQueryParam>>("[\"\"]"));
		}
	}
}
