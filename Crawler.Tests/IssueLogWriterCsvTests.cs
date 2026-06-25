using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the RFC 4180 quoted-CSV pair <see cref="IssueLogWriter.ComposeCsvLine"/> /
	/// <see cref="IssueLogWriter.ParseCsvLine"/>, used by 08-seo-data.csv. Unlike ComposeLine
	/// (which strips the delimiter), these PRESERVE field content — fields holding the delimiter
	/// or a double-quote are quoted (internal quotes doubled) rather than mangled. Newlines are
	/// still sanitized out so records stay single-line and the reader stays line-by-line.
	/// </summary>
	public class IssueLogWriterCsvTests
	{
		// ── ComposeCsvLine ───────────────────────────────────────────────────

		[Fact]
		public void Compose_PlainFields_NotQuoted()
			=> Assert.Equal("a;b;c", IssueLogWriter.ComposeCsvLine(';', "a", "b", "c"));

		[Fact]
		public void Compose_FieldWithDelimiter_IsQuoted()
			=> Assert.Equal("\"a;b\";c", IssueLogWriter.ComposeCsvLine(';', "a;b", "c"));

		[Fact]
		public void Compose_FieldWithQuote_QuotedAndDoubled()
			=> Assert.Equal("\"a\"\"b\"", IssueLogWriter.ComposeCsvLine(';', "a\"b"));

		[Fact]
		public void Compose_FieldWithBoth_QuotedAndDoubled()
			=> Assert.Equal("\"a;\"\"b\"", IssueLogWriter.ComposeCsvLine(';', "a;\"b"));

		[Fact]
		public void Compose_NullField_EmptyCell()
			=> Assert.Equal("a;;c", IssueLogWriter.ComposeCsvLine(';', "a", null, "c"));

		[Fact]
		public void Compose_StripsNewlines_StaysSingleLine()
		{
			var line = IssueLogWriter.ComposeCsvLine(';', "a\r\nb", "c");
			Assert.DoesNotContain('\n', line);
			Assert.DoesNotContain('\r', line);
		}

		// ── ParseCsvLine ─────────────────────────────────────────────────────

		[Fact]
		public void Parse_PlainFields()
			=> Assert.Equal(new[] { "a", "b", "c" }, IssueLogWriter.ParseCsvLine("a;b;c", ';'));

		[Fact]
		public void Parse_QuotedFieldWithDelimiter()
			=> Assert.Equal(new[] { "a;b", "c" }, IssueLogWriter.ParseCsvLine("\"a;b\";c", ';'));

		[Fact]
		public void Parse_DoubledQuotesInsideQuotedField()
			=> Assert.Equal(new[] { "a\"b" }, IssueLogWriter.ParseCsvLine("\"a\"\"b\"", ';'));

		[Fact]
		public void Parse_EmptyTrailingField()
			=> Assert.Equal(new[] { "a", "" }, IssueLogWriter.ParseCsvLine("a;", ';'));

		[Fact]
		public void Parse_AllEmptyFields()
			=> Assert.Equal(new[] { "", "", "" }, IssueLogWriter.ParseCsvLine(";;", ';'));

		// ── Round-trip (clean fields — no control chars — survive exactly) ────

		[Theory]
		[InlineData("simple")]
		[InlineData("with;delimiter")]
		[InlineData("with\"quote")]
		[InlineData("with;both\"here")]
		[InlineData("trailing;")]
		[InlineData("")]
		public void RoundTrip_SingleField(string value)
		{
			var line = IssueLogWriter.ComposeCsvLine(';', value);
			var parsed = IssueLogWriter.ParseCsvLine(line, ';');
			Assert.Single(parsed);
			Assert.Equal(value, parsed[0]);
		}

		[Fact]
		public void RoundTrip_SeoLikeGermanRow_SurvivesByteForByte()
		{
			// A German title carrying the ';' delimiter and a literal quote — the case that
			// motivated quoted CSV over a stripped delimiter. Content must survive intact.
			var fields = new[]
			{
				"https://x.test/seite",
				"index,follow",
				"Home; Über uns \"Test\"",
				"21",
				"Größe & Stil — alles über Qualität",
				"34",
				"a,b,c",
				"crawl",
				"1",
			};

			var line = IssueLogWriter.ComposeCsvLine(';', fields);
			var parsed = IssueLogWriter.ParseCsvLine(line, ';');

			Assert.Equal(fields, parsed);
		}

		// ── Comma delimiter ──────────────────────────────────────────────────
		// The comma twin (xx-name_comma.csv) goes through the SAME ComposeCsvLine, only the
		// delimiter char differs. Comma occurs freely in real fields — "index,follow", URL query
		// strings, keyword lists — so it is no safer to split than ';'; quoting is what makes it
		// round-trip. These mirror the ';' matrix for ','.

		[Fact]
		public void Compose_Comma_PlainFields_NotQuoted()
			=> Assert.Equal("a,b,c", IssueLogWriter.ComposeCsvLine(',', "a", "b", "c"));

		[Fact]
		public void Compose_Comma_FieldWithDelimiter_IsQuoted()
			=> Assert.Equal("\"index,follow\",x",
				IssueLogWriter.ComposeCsvLine(',', "index,follow", "x"));

		[Fact]
		public void Compose_Comma_FieldWithQuote_QuotedAndDoubled()
			=> Assert.Equal("\"a\"\"b\"", IssueLogWriter.ComposeCsvLine(',', "a\"b"));

		[Fact]
		public void Compose_Comma_FieldWithBoth_QuotedAndDoubled()
			=> Assert.Equal("\"a,\"\"b\"", IssueLogWriter.ComposeCsvLine(',', "a,\"b"));

		[Fact]
		public void Parse_Comma_QuotedFieldWithDelimiter()
			=> Assert.Equal(new[] { "index,follow", "x" },
				IssueLogWriter.ParseCsvLine("\"index,follow\",x", ','));

		[Fact]
		public void Parse_Comma_QuotedUrlWithQueryString()
			=> Assert.Equal(new[] { "https://x.test/p?a=1,b=2", "ok" },
				IssueLogWriter.ParseCsvLine("\"https://x.test/p?a=1,b=2\",ok", ','));

		[Theory]
		[InlineData("index,follow")]
		[InlineData("https://x.test/p?a=1,b=2")]
		[InlineData("with,both\"here")]
		[InlineData("trailing,")]
		[InlineData("plain")]
		[InlineData("")]
		public void RoundTrip_Comma_SingleField(string value)
		{
			var line = IssueLogWriter.ComposeCsvLine(',', value);
			var parsed = IssueLogWriter.ParseCsvLine(line, ',');
			Assert.Single(parsed);
			Assert.Equal(value, parsed[0]);
		}

		[Fact]
		public void RoundTrip_Comma_SeoLikeGermanRow_SurvivesByteForByte()
		{
			var fields = new[]
			{
				"https://x.test/seite?ref=a,b",
				"index,follow",
				"Home, Über \"uns\"",
				"21",
				"Größe & Stil, alles über Qualität",
				"34",
				"a,b,c",
				"crawl",
				"1",
			};

			var line = IssueLogWriter.ComposeCsvLine(',', fields);
			var parsed = IssueLogWriter.ParseCsvLine(line, ',');

			Assert.Equal(fields, parsed);
		}

		[Theory]
		[InlineData(';')]
		[InlineData(',')]
		public void RoundTrip_EitherDelimiter_RecoversSameFields(char delimiter)
		{
			// The same record, written and read under either delimiter, recovers identically —
			// proving the comma twin and the semicolon file are both faithful, not that either
			// is "safe to split".
			var fields = new[] { "u,1;2", "index,follow", "T;t,\"q\"", "9" };

			var line = IssueLogWriter.ComposeCsvLine(delimiter, fields);
			var parsed = IssueLogWriter.ParseCsvLine(line, delimiter);

			Assert.Equal(fields, parsed);
		}

		// ── WriteCsvPair (dual-locale output) ────────────────────────────────

		[Fact]
		public void WriteCsvPair_WritesBothLocaleFiles_BomPrefixed_RoundTrippable()
		{
			var basePath = Path.Combine(Path.GetTempPath(), $"csvpair_{Guid.NewGuid():N}");
			var semicolon = basePath + IssueLogWriter.CsvSemicolonSuffix;
			var comma = basePath + IssueLogWriter.CsvCommaSuffix;
			try
			{
				var records = new[]
				{
					new[] { "Url", "Robots", "Title" },
					new[] { "https://x.test/p?a=1,b=2", "index,follow", "Über; uns \"Test\"" },
				};

				IssueLogWriter.WriteCsvPair(basePath, records);

				Assert.True(File.Exists(semicolon), "semicolon file not written");
				Assert.True(File.Exists(comma), "comma file not written");

				foreach (var path in new[] { semicolon, comma })
				{
					var bytes = File.ReadAllBytes(path);
					Assert.True(bytes.Length >= 3);
					Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
				}

				// Each file, parsed under its own delimiter, recovers the identical records —
				// quoting (not the delimiter choice) is what makes both faithful.
				AssertParsesBackTo(semicolon, ';', records);
				AssertParsesBackTo(comma, ',', records);
			}
			finally
			{
				foreach (var path in new[] { semicolon, comma })
				{
					if (File.Exists(path))
					{
						File.Delete(path);
					}
				}
			}
		}

		private static void AssertParsesBackTo(string path, char delimiter, string[][] expected)
		{
			var lines = File.ReadAllLines(path); // UTF-8 read strips the BOM
			Assert.Equal(expected.Length, lines.Length);
			for (int i = 0; i < expected.Length; i++)
			{
				Assert.Equal(expected[i], IssueLogWriter.ParseCsvLine(lines[i], delimiter));
			}
		}
	}
}
