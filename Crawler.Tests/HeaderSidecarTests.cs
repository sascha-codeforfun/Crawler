using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the pure header-sidecar helpers (#339): SanitizeHeaderValue and
	/// FormatHeaderSidecar. The HTTP-typed extraction and file write live at the
	/// download call sites (Crawl.WriteHeaderSidecar) and are validated by a live
	/// crawl, not here. All inputs are synthetic — no real site values.
	/// </summary>
	public class HeaderSidecarTests
	{
		// ── SanitizeHeaderValue ──────────────────────────────────────────────

		[Fact]
		public void SanitizeHeaderValue_PlainValue_Unchanged()
		{
			Assert.Equal("application/pdf", HeaderSidecar.SanitizeHeaderValue("application/pdf"));
		}

		[Fact]
		public void SanitizeHeaderValue_NullOrEmpty_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, HeaderSidecar.SanitizeHeaderValue(null));
			Assert.Equal(string.Empty, HeaderSidecar.SanitizeHeaderValue(""));
		}

		[Fact]
		public void SanitizeHeaderValue_ColonsInValue_Preserved()
		{
			// A value may contain any number of colons (cookie payloads, URLs).
			// The sanitizer must not touch them — only a reader's first-colon split
			// matters, and that is the reader's concern.
			const string raw = "SESSION=ab:cd:ef::12; Path=/; Secure";
			Assert.Equal(raw, HeaderSidecar.SanitizeHeaderValue(raw));
		}

		[Fact]
		public void SanitizeHeaderValue_Tab_KeptVerbatim()
		{
			Assert.Equal("a\tb", HeaderSidecar.SanitizeHeaderValue("a\tb"));
		}

		[Fact]
		public void SanitizeHeaderValue_CrLf_RenderedAsMarkers()
		{
			Assert.Equal("a[CR][LF]b", HeaderSidecar.SanitizeHeaderValue("a\r\nb"));
		}

		[Fact]
		public void SanitizeHeaderValue_LineAndParagraphSeparators_RenderedAsMarkers()
		{
			Assert.Equal(
				"x[INVISIBLE LINE SEPARATOR U+2028]y[INVISIBLE PARAGRAPH SEPARATOR U+2029]z",
				HeaderSidecar.SanitizeHeaderValue("x\u2028y\u2029z"));
		}

		[Fact]
		public void SanitizeHeaderValue_ZeroWidthAndBom_RenderedAsMarkers()
		{
			Assert.Equal(
				"[INVISIBLE ZERO-WIDTH SPACE U+200B][INVISIBLE BOM U+FEFF]",
				HeaderSidecar.SanitizeHeaderValue("\u200B\uFEFF"));
		}

		[Fact]
		public void SanitizeHeaderValue_OtherC0Control_RenderedAsCodepointMarker()
		{
			// A NUL (U+0000) is an "other C0 control" — rendered, not dropped, so the
			// forensic fact survives.
			Assert.Equal("a[INVISIBLE CONTROL U+0000]b", HeaderSidecar.SanitizeHeaderValue("a\u0000b"));
		}

		[Fact]
		public void SanitizeHeaderValue_NoEmbeddedRealNewlineSurvives()
		{
			// The whole point: after sanitization the value cannot contain a raw
			// newline that would split the sidecar line.
			var cleaned = HeaderSidecar.SanitizeHeaderValue("first\nsecond");
			Assert.DoesNotContain('\n', cleaned);
			Assert.DoesNotContain('\r', cleaned);
		}

		// ── FormatHeaderSidecar ──────────────────────────────────────────────

		[Fact]
		public void FormatHeaderSidecar_TwoSections_WithStatusAndRequestLines()
		{
			var text = HeaderSidecar.FormatHeaderSidecar(
				"GET https://example.test/page",
				new[] { ("Accept", "text/html"), ("User-Agent", "TestAgent/1.0") },
				"HTTP/1.1 200 OK",
				new[] { ("Content-Type", "text/html"), ("Content-Length", "1234") });

			var lines = text.Split('\n');
			Assert.Equal("=== REQUEST ===", lines[0]);
			Assert.Equal("GET https://example.test/page", lines[1]);
			Assert.Equal("Accept: text/html", lines[2]);
			Assert.Equal("User-Agent: TestAgent/1.0", lines[3]);
			Assert.Equal("=== RESPONSE ===", lines[4]);
			Assert.Equal("HTTP/1.1 200 OK", lines[5]);
			Assert.Equal("Content-Type: text/html", lines[6]);
			Assert.Equal("Content-Length: 1234", lines[7]);
		}

		[Fact]
		public void FormatHeaderSidecar_RepeatedHeader_AllOccurrencesPreserved()
		{
			var text = HeaderSidecar.FormatHeaderSidecar(
				"GET https://example.test/x",
				System.Array.Empty<(string, string)>(),
				"HTTP/1.1 200 OK",
				new[]
				{
					("Set-Cookie", "A=1; Path=/"),
					("Set-Cookie", "B=2; Path=/")
				});

			Assert.Contains("Set-Cookie: A=1; Path=/", text);
			Assert.Contains("Set-Cookie: B=2; Path=/", text);
			// Two distinct Set-Cookie lines, not collapsed.
			var count = text.Split('\n').Count(l => l.StartsWith("Set-Cookie:"));
			Assert.Equal(2, count);
		}

		[Fact]
		public void FormatHeaderSidecar_ValueWithEmbeddedNewline_DoesNotBreakLineCount()
		{
			// A malformed header value carrying a newline must not add a physical
			// line — it is rendered as a marker inside its own single line.
			var text = HeaderSidecar.FormatHeaderSidecar(
				"GET https://example.test/x",
				System.Array.Empty<(string, string)>(),
				"HTTP/1.1 200 OK",
				new[] { ("X-Weird", "line1\nline2") });

			var weirdLines = text.Split('\n').Where(l => l.StartsWith("X-Weird:")).ToList();
			Assert.Single(weirdLines);
			Assert.Equal("X-Weird: line1[LF]line2", weirdLines[0]);
		}

		[Fact]
		public void FormatHeaderSidecar_SectionMarkerIsFullLine_NotSubstring()
		{
			// A value that happens to contain the marker text appears with its
			// "Name: " prefix, so a full-line marker match in a reader is unambiguous.
			var text = HeaderSidecar.FormatHeaderSidecar(
				"GET https://example.test/x",
				System.Array.Empty<(string, string)>(),
				"HTTP/1.1 200 OK",
				new[] { ("X-Note", "=== RESPONSE ===") });

			var noteLine = text.Split('\n').Single(l => l.StartsWith("X-Note:"));
			Assert.Equal("X-Note: === RESPONSE ===", noteLine);
			Assert.NotEqual("=== RESPONSE ===", noteLine);
		}
	}
}
