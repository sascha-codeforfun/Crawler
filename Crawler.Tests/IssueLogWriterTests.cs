using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for IssueLogWriter — fileset #286 central sanitization-aware
	/// writer for all delimited crawler logs. Sanitization is the security
	/// property under test; line-composition correctness is the functional
	/// property.
	/// </summary>
	public class IssueLogWriterTests
	{
		// ── Sanitization — pipe delimiter ──────────────────────────────────

		[Fact]
		public void SanitizeField_PlainText_Unchanged()
		{
			var (cleaned, changed) = IssueLogWriter.SanitizeField("hello world", '|');
			Assert.Equal("hello world", cleaned);
			Assert.False(changed);
		}

		[Fact]
		public void SanitizeField_EmptyAndNull_HandledGracefully()
		{
			Assert.Equal(string.Empty, IssueLogWriter.SanitizeField(null, '|').Cleaned);
			Assert.False(IssueLogWriter.SanitizeField(null, '|').Changed);
			Assert.Equal(string.Empty, IssueLogWriter.SanitizeField("", '|').Cleaned);
			Assert.False(IssueLogWriter.SanitizeField("", '|').Changed);
		}

		[Fact]
		public void SanitizeField_Newlines_ReplacedWithSpace()
		{
			// Each newline character becomes a single space.
			var (cleaned, changed) = IssueLogWriter.SanitizeField("line1\nline2\rline3\r\nline4", '|');
			Assert.Equal("line1 line2 line3  line4", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_Tab_ReplacedWithSpace()
		{
			var (cleaned, changed) = IssueLogWriter.SanitizeField("col1\tcol2", '|');
			Assert.Equal("col1 col2", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_PipeDelimiter_Replaced()
		{
			var (cleaned, changed) = IssueLogWriter.SanitizeField("a|b|c", '|');
			Assert.Equal("a/b/c", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_C0Controls_Stripped()
		{
			// SOH (U+0001), STX (U+0002), BS (U+0008) — all stripped.
			var (cleaned, changed) = IssueLogWriter.SanitizeField("a\u0001b\u0002c\u0008d", '|');
			Assert.Equal("abcd", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_C1Controls_Stripped()
		{
			// U+0080 through U+009F — C1 controls.
			var (cleaned, changed) = IssueLogWriter.SanitizeField("a\u0080b\u009Fc", '|');
			Assert.Equal("abc", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_ZeroWidthChars_Stripped()
		{
			// ZWSP (U+200B), ZWNJ (U+200C), ZWJ (U+200D), BOM/ZWNBSP (U+FEFF).
			var (cleaned, changed) = IssueLogWriter.SanitizeField(
				"a\u200Bb\u200Cc\u200Dd\uFEFFe", '|');
			Assert.Equal("abcde", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_BidiControls_Stripped()
		{
			// U+202A LRE, U+202E RLO, U+2066 LRI, U+2069 PDI.
			var (cleaned, changed) = IssueLogWriter.SanitizeField(
				"a\u202Ab\u202Ec\u2066d\u2069e", '|');
			Assert.Equal("abcde", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_UnicodeLineSeparators_ReplacedWithSpace()
		{
			// Fileset #286b regression: U+2028 LINE SEPARATOR and U+2029
			// PARAGRAPH SEPARATOR found in CMS-pasted meta description content
			// on a real-world page. .NET's StreamReader.ReadLine doesn't split
			// on these, but text editors render them as visual line breaks and
			// other tooling may split. Treat them as line breaks for
			// sanitization purposes — replace with space.
			var (cleaned, changed) = IssueLogWriter.SanitizeField(
				"section.\u2028 Tip: Start the setup process\u2029next paragraph", '|');
			Assert.Equal("section.  Tip: Start the setup process next paragraph", cleaned);
			Assert.True(changed);
			// Crucially: no Unicode line-break characters remain.
			Assert.DoesNotContain('\u2028', cleaned);
			Assert.DoesNotContain('\u2029', cleaned);
		}

		[Fact]
		public void SanitizeField_HighUnicode_Preserved()
		{
			// Legitimate non-ASCII content (German umlauts, Polish diacritics,
			// Czech háčky) must pass through unchanged.
			var input = "Größe Łódź příliš žluťoučký";
			var (cleaned, changed) = IssueLogWriter.SanitizeField(input, '|');
			Assert.Equal(input, cleaned);
			Assert.False(changed);
		}

		// ── Sanitization — string delimiter (SEO @@@) ──────────────────────

		[Fact]
		public void SanitizeField_StringDelimiter_ReplacesAllOccurrences()
		{
			var (cleaned, changed) = IssueLogWriter.SanitizeField("a@@@b@@@c", "@@@");
			Assert.Equal("a///b///c", cleaned);
			Assert.True(changed);
		}

		[Fact]
		public void SanitizeField_StringDelimiter_AlsoHandlesControlChars()
		{
			var (cleaned, changed) = IssueLogWriter.SanitizeField("line1\nline2@@@end", "@@@");
			Assert.Equal("line1 line2///end", cleaned);
			Assert.True(changed);
		}

		// ── Line composition ──────────────────────────────────────────────

		[Fact]
		public void ComposeLine_PlainFields_JoinedWithDelimiter()
		{
			var line = IssueLogWriter.ComposeLine('|', "field1", "field2", "field3");
			Assert.Equal("field1|field2|field3", line);
		}

		[Fact]
		public void ComposeLine_FieldWithNewline_SanitizedBeforeJoin()
		{
			var line = IssueLogWriter.ComposeLine('|',
				"page.html",
				"POTENTIAL_TRANSLATION",
				"meta[@name=description] (passes en dictionary)",
				"For a hassle-free holiday\n- Our Credit Card.");
			// THE bug fix: embedded newlines must be replaced before the
			// composed line is written. The composed line must have NO newlines.
			Assert.DoesNotContain('\n', line);
			Assert.DoesNotContain('\r', line);
			// Pipe-count must equal field-count minus 1.
			Assert.Equal(3, line.Split('|').Length - 1);
		}

		[Fact]
		public void ComposeLine_NullFieldsTreatedAsEmpty()
		{
			var line = IssueLogWriter.ComposeLine('|', "a", null, "c");
			Assert.Equal("a||c", line);
		}

		[Fact]
		public void ComposeLine_WithStringDelimiter_JoinedCorrectly()
		{
			var line = IssueLogWriter.ComposeLine("@@@", "url", "robots", "title");
			Assert.Equal("url@@@robots@@@title", line);
		}

		// ── File operations ───────────────────────────────────────────────

		[Fact]
		public void Append_SingleRecord_WritesOneLineToFile()
		{
			var path = Path.GetTempFileName();
			try
			{
				IssueLogWriter.Append(path, '|', "a", "b", "c");
				var lines = File.ReadAllLines(path);
				Assert.Single(lines);
				Assert.Equal("a|b|c", lines[0]);
			}
			finally { File.Delete(path); }
		}

		[Fact]
		public void Append_RecordWithEmbeddedNewline_StillOneLineInFile()
		{
			// Regression test for the IssueTracking bug. A field containing a
			// newline MUST NOT split into multiple physical lines when written.
			var path = Path.GetTempFileName();
			try
			{
				IssueLogWriter.Append(path, '|',
					"page.html",
					"POTENTIAL_TRANSLATION",
					"meta[@name=description] (passes en dictionary)",
					"For a hassle-free holiday \n- Our Credit Card.");
				var lines = File.ReadAllLines(path);
				// Exactly ONE line, despite the newline in the input field.
				Assert.Single(lines);
				// And the line splits into exactly 4 fields on '|'.
				Assert.Equal(4, lines[0].Split('|').Length);
			}
			finally { File.Delete(path); }
		}

		[Fact]
		public void AppendMany_MultipleRecords_OnePerLine()
		{
			var path = Path.GetTempFileName();
			try
			{
				var records = new List<string?[]>
				{
					new string?[] { "a", "b" },
					new string?[] { "c", "d" },
					new string?[] { "e", "f" },
				};
				IssueLogWriter.AppendMany(path, '|', records);
				var lines = File.ReadAllLines(path);
				Assert.Equal(3, lines.Length);
				Assert.Equal("a|b", lines[0]);
				Assert.Equal("c|d", lines[1]);
				Assert.Equal("e|f", lines[2]);
			}
			finally { File.Delete(path); }
		}

		[Fact]
		public void Write_OverwritesExistingFile()
		{
			var path = Path.GetTempFileName();
			try
			{
				File.WriteAllText(path, "old content\nold content 2\n");
				IssueLogWriter.Write(path, '|', new[] { new string?[] { "new", "data" } });
				var lines = File.ReadAllLines(path);
				Assert.Single(lines);
				Assert.Equal("new|data", lines[0]);
			}
			finally { File.Delete(path); }
		}

		// ── Round-trip — the actual bug as a test ─────────────────────────

		[Fact]
		public void RoundTrip_RecordWithNewline_ParsesBackAsOneRecord()
		{
			// End-to-end regression for the original failure: write a record
			// containing an embedded newline, read the file back, confirm
			// each physical line maps to one record.
			var path = Path.GetTempFileName();
			try
			{
				var record = new string?[]
				{
					"page.html",
					"POTENTIAL_TRANSLATION",
					"meta[@name=description] (passes en dictionary)",
					"For a hassle-free holiday \n- Our Credit Card."
				};
				IssueLogWriter.Append(path, '|', record);

				var physicalLines = File.ReadAllLines(path);
				Assert.Single(physicalLines);

				var fields = physicalLines[0].Split('|');
				Assert.Equal(4, fields.Length);
				// Field 0 must still be the filename — not corrupted by the
				// newline that previously split the line into multiple lines.
				Assert.Equal("page.html", fields[0]);
				Assert.Equal("POTENTIAL_TRANSLATION", fields[1]);
				// Field 3 (the excerpt) must contain BOTH halves of the
				// originally-multiline content, with the newline replaced.
				Assert.Contains("hassle-free holiday", fields[3]);
				Assert.Contains("Our Credit Card.", fields[3]);
				Assert.DoesNotContain('\n', fields[3]);
			}
			finally { File.Delete(path); }
		}
	}
}
