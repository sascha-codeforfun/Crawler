using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for <see cref="CrawlHistoryDiagnostic.ExtractRequestUrl"/>.
	/// Pure-function tests — no I/O. Targets the sidecar-format parser whose
	/// bug-shape we already hit once (the path-construction issue in #455a)
	/// and whose BOM handling is the kind of thing that breaks under different
	/// file encodings.
	/// </summary>
	public class CrawlHistoryDiagnosticExtractRequestUrlTests
	{
		// ── Well-formed sidecars ──────────────────────────────────────────────

		[Fact]
		public void ExtractRequestUrl_WellFormedSidecar_ReturnsUrl()
		{
			var text =
				"=== REQUEST ===\n" +
				"GET https://example.com/page.html\n" +
				"=== RESPONSE ===\n" +
				"HTTP/1.1 200 OK\n";

			var url = CrawlHistoryDiagnostic.ExtractRequestUrl(text);

			Assert.Equal("https://example.com/page.html", url);
		}

		[Fact]
		public void ExtractRequestUrl_UrlWithQueryStringAndFragment_ReturnsFullUrl()
		{
			var text =
				"=== REQUEST ===\n" +
				"GET https://example.com/search?q=foo&page=2#results\n" +
				"=== RESPONSE ===\n";

			var url = CrawlHistoryDiagnostic.ExtractRequestUrl(text);

			Assert.Equal("https://example.com/search?q=foo&page=2#results", url);
		}

		[Fact]
		public void ExtractRequestUrl_TrailingWhitespaceOnGetLine_TrimmedAway()
		{
			// Defensive: stray whitespace on the GET line should be trimmed.
			var text =
				"=== REQUEST ===\n" +
				"GET https://example.com/page.html   \n" +
				"=== RESPONSE ===\n";

			var url = CrawlHistoryDiagnostic.ExtractRequestUrl(text);

			Assert.Equal("https://example.com/page.html", url);
		}

		// ── BOM handling ──────────────────────────────────────────────────────

		[Fact]
		public void ExtractRequestUrl_BomPrefixedFirstLine_StripsBomAndExtracts()
		{
			// Header sidecars are written by File.WriteAllText with
			// Encoding.UTF8 which emits a BOM. StringReader does not auto-strip
			// it like StreamReader does, so the parser strips the BOM
			// defensively on the first line.
			var text =
				"\uFEFF=== REQUEST ===\n" +
				"GET https://example.com/page.html\n" +
				"=== RESPONSE ===\n";

			var url = CrawlHistoryDiagnostic.ExtractRequestUrl(text);

			Assert.Equal("https://example.com/page.html", url);
		}

		// ── Malformed / edge inputs ───────────────────────────────────────────

		[Fact]
		public void ExtractRequestUrl_EmptyString_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, CrawlHistoryDiagnostic.ExtractRequestUrl(""));
		}

		[Fact]
		public void ExtractRequestUrl_OnlyNewlines_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, CrawlHistoryDiagnostic.ExtractRequestUrl("\n\n\n"));
		}

		[Fact]
		public void ExtractRequestUrl_NoRequestBlock_ReturnsEmpty()
		{
			// Header file missing the REQUEST framing entirely.
			var text =
				"HTTP/1.1 200 OK\n" +
				"Content-Type: text/html\n";

			Assert.Equal(string.Empty, CrawlHistoryDiagnostic.ExtractRequestUrl(text));
		}

		[Fact]
		public void ExtractRequestUrl_RequestBlockButNoGetLine_ReturnsEmptyAtResponseBoundary()
		{
			// REQUEST framing present but no GET line before the RESPONSE marker.
			var text =
				"=== REQUEST ===\n" +
				"=== RESPONSE ===\n" +
				"HTTP/1.1 200 OK\n";

			Assert.Equal(string.Empty, CrawlHistoryDiagnostic.ExtractRequestUrl(text));
		}

		[Fact]
		public void ExtractRequestUrl_GetLineOutsideRequestBlock_NotExtracted()
		{
			// A "GET ..." line that appears BEFORE the REQUEST framing is
			// ignored — the parser only honors GET lines inside the REQUEST
			// block. Defensive against header sidecars where a GET appears in
			// some other header line.
			var text =
				"GET https://wrong.example.com/notmyurl.html\n" +
				"=== REQUEST ===\n" +
				"GET https://example.com/right.html\n" +
				"=== RESPONSE ===\n";

			var url = CrawlHistoryDiagnostic.ExtractRequestUrl(text);

			Assert.Equal("https://example.com/right.html", url);
		}

		[Fact]
		public void ExtractRequestUrl_OnlyRequestMarkerNoContent_ReturnsEmpty()
		{
			// REQUEST marker present but reader hits EOF before finding GET.
			var text = "=== REQUEST ===\n";

			Assert.Equal(string.Empty, CrawlHistoryDiagnostic.ExtractRequestUrl(text));
		}

		// ── CRLF line endings (Windows-written sidecars) ──────────────────────

		[Fact]
		public void ExtractRequestUrl_CrlfLineEndings_ParsesCorrectly()
		{
			// File.WriteAllText on Windows produces CRLF line endings;
			// StringReader.ReadLine handles both LF and CRLF transparently.
			var text =
				"=== REQUEST ===\r\n" +
				"GET https://example.com/page.html\r\n" +
				"=== RESPONSE ===\r\n";

			var url = CrawlHistoryDiagnostic.ExtractRequestUrl(text);

			Assert.Equal("https://example.com/page.html", url);
		}
	}
}
