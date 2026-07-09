using Xunit;
using System.Collections.Generic;
using System.Linq;
using Crawler;
using Crawler.Quality;
using HtmlAgilityPack;

namespace Crawler.Tests.Quality
{
	/// <summary>
	/// Locks the STABLE IDENTITY of CONTROL_CHARS_IN_CONTENT findings.
	///
	/// The defect this guards against: the finding's tracking Word used to be the
	/// display prose ("Found &lt;name&gt; in &lt;li&gt; text"), which is IDENTICAL
	/// for every &lt;li&gt; on a page. N distinct list items therefore collapsed to
	/// ONE ledger Key — IssueTracking.Merge groups the detected set by Key and keeps
	/// First(), so N-1 findings were silently dropped and never reached TicketText,
	/// even on an unchanged re-crawl.
	///
	/// The fix keys the Word on a stable identity payload the detector packs into
	/// Detail: a location token plus the FULL, marker-encoded, untruncated element
	/// text (LogExcerpt.EncodeForIdentity). These tests prove:
	///   - distinct elements get distinct identities (no collapse),
	///   - an identical re-crawl yields identical identities (ticket survives Merge),
	///   - the encoding is lossless where the ledger's own sanitisation is lossy
	///     (a literal '|' does not collide with '/', and literal "[…]" text does not
	///     collide with a real marker),
	///   - long elements sharing a long prefix stay distinct (no truncation),
	///   - the encoded identity is delimiter-safe (no raw '|') so it round-trips the
	///     pipe-delimited logs unchanged.
	///
	/// All fixtures are synthetic — invented text and example.com URLs. No content
	/// from any real crawl appears here.
	/// </summary>
	public class ControlCharsIdentityTests
	{
		private const char Zwsp = '\u200B';
		private const char LineSep = '\u2028';

		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		// Reproduces how ContentQualityTriage builds the tracking Key from a
		// detector finding: split the Detail on the identity separator, key on the
		// payload. Kept in the test so the round-trip (detector Detail -> Word) is
		// asserted end to end without standing up the whole triage UI.
		private static string WordFromDetail(string detail)
		{
			var i = detail.IndexOf(ControlChars.IdentitySeparator, System.StringComparison.Ordinal);
			var payload = i >= 0
				? detail[(i + ControlChars.IdentitySeparator.Length)..]
				: detail;
			return "CONTROL_CHARS_IN_CONTENT:" + payload;
		}

		private static string KeyFor(string url, string detail)
			=> $"QUALITY|{url}|{WordFromDetail(detail)}";

		// ── EncodeForIdentity: the encoder's own contract ───────────────────

		[Fact]
		public void Encode_RendersInvisiblesAsReadableMarkersInPlace()
		{
			// Leading vs trailing invisible must differ (position preserved).
			var trailing = LogExcerpt.EncodeForIdentity("word" + Zwsp);
			var leading = LogExcerpt.EncodeForIdentity(Zwsp + "word");

			Assert.Contains("[INVISIBLE ZERO-WIDTH SPACE U+200B]", trailing);
			Assert.NotEqual(trailing, leading);
		}

		[Fact]
		public void Encode_IsPipeFree_SoItSurvivesPipeDelimitedLogs()
		{
			var encoded = LogExcerpt.EncodeForIdentity("a|b|c");
			Assert.DoesNotContain("|", encoded);
			Assert.Contains("[PIPE]", encoded);
		}

		[Fact]
		public void Encode_LiteralPipe_DistinctFromSlash()
		{
			// The ledger's SanitizeField turns '|' into '/', which is lossy: without
			// pre-encoding, "a|b" and "a/b" would collide. Encoding keeps them apart.
			Assert.NotEqual(
				LogExcerpt.EncodeForIdentity("a|b"),
				LogExcerpt.EncodeForIdentity("a/b"));
		}

		[Fact]
		public void Encode_RealPipe_DistinctFromLiteralPipeMarkerText()
		{
			// Grammar closure: '[' is escaped first, so content that literally
			// contains "[PIPE]" cannot be confused with an encoded real pipe.
			Assert.NotEqual(
				LogExcerpt.EncodeForIdentity("a|b"),
				LogExcerpt.EncodeForIdentity("a[PIPE]b"));
		}

		[Fact]
		public void Encode_DoesNotTruncate_LongSharedPrefixStaysDistinct()
		{
			var prefix = string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet. ", 20)); // > 400 chars
			var a = LogExcerpt.EncodeForIdentity(prefix + "ALPHA" + LineSep);
			var b = LogExcerpt.EncodeForIdentity(prefix + "BRAVO" + LineSep);

			Assert.NotEqual(a, b);
			Assert.DoesNotContain("…", a); // no truncation ellipsis in an identity
		}

		// ── Detector: distinct elements → distinct identities ───────────────

		[Fact]
		public void FourListItems_SamePage_ProduceFourDistinctKeys()
		{
			// The canonical collapse case: four <li> each with a trailing ZWSP.
			// Old behaviour: one Key for all four. Fixed: four distinct Keys.
			var doc = Doc(
				"<html><body><ul>" +
				$"<li>item one{Zwsp}</li>" +
				$"<li>item two{Zwsp}</li>" +
				$"<li>item three{Zwsp}</li>" +
				$"<li>item four{Zwsp}</li>" +
				"</ul></body></html>");

			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Equal(4, issues.Count);

			var keys = issues.Select(i => KeyFor("https://example.com/a", i.Detail)).ToList();
			Assert.Equal(4, keys.Distinct().Count());
		}

		[Fact]
		public void IdenticalReCrawl_ProducesIdenticalKeys_SoTicketsSurviveMerge()
		{
			HtmlDocument Build() => Doc(
				"<html><body><ul>" +
				$"<li>alpha{Zwsp}</li>" +
				$"<li>bravo{Zwsp}</li>" +
				"</ul></body></html>");

			var run1 = ControlChars.Check("f.html", Build(), new ContentQualityConfig())
				.Select(i => KeyFor("https://example.com/a", i.Detail)).ToList();
			var run2 = ControlChars.Check("f.html", Build(), new ContentQualityConfig())
				.Select(i => KeyFor("https://example.com/a", i.Detail)).ToList();

			Assert.Equal(run1, run2);
		}

		[Fact]
		public void SameVisibleText_DifferentInvisibleSignature_AreDistinct()
		{
			// One vs two trailing ZWSP on the same visible text — different
			// invisible state, so a different identity (surfaced separately).
			var doc = Doc(
				"<html><body>" +
				$"<h3>Same Heading{Zwsp}</h3>" +
				$"<h2>Same Heading{Zwsp}{Zwsp}</h2>" +
				"</body></html>");

			var issues = ControlChars.Check("f.html", doc, new ContentQualityConfig()).ToList();
			Assert.Equal(2, issues.Count);
			var keys = issues.Select(i => KeyFor("https://example.com/a", i.Detail)).ToList();
			Assert.Equal(2, keys.Distinct().Count());
		}

		[Fact]
		public void DisplayDetail_StillHumanReadable_PayloadHiddenBehindSeparator()
		{
			var doc = Doc(
				"<html><head>" +
				$"<meta name=\"description\" content=\"free{'\n'}shipping\">" +
				"</head></html>");

			var issue = ControlChars.Check("f.html", doc, new ContentQualityConfig()).Single();

			// Prose (display) portion is intact and readable...
			var sepIndex = issue.Detail.IndexOf(ControlChars.IdentitySeparator, System.StringComparison.Ordinal);
			Assert.True(sepIndex > 0);
			var display = issue.Detail[..sepIndex];
			Assert.Contains("LF", display);
			Assert.Contains("meta", display);

			// ...and the identity payload is present and pipe-free.
			var payload = issue.Detail[(sepIndex + ControlChars.IdentitySeparator.Length)..];
			Assert.DoesNotContain("|", payload);
			Assert.Contains("meta[name=description]", payload);
		}

		// ── Merge survival end to end ───────────────────────────────────────

		[Fact]
		public void TicketedRow_SurvivesMerge_OnUnchangedReCrawl()
		{
			// Build a pending (ticketed) row from run 1, then Merge against the
			// detected set of an identical run 2. The pending row must survive
			// because its Key is present in the detected set.
			HtmlDocument Build() => Doc(
				$"<html><body><li>keep me{Zwsp}</li></body></html>");

			var detail1 = ControlChars.Check("f.html", Build(), new ContentQualityConfig()).Single().Detail;
			var word = WordFromDetail(detail1);

			var ticketed = new IssueTracking.IssueRecord
			{
				Source = "triage",
				Type = "QUALITY",
				Url = "https://example.com/a",
				Status = "pending",
				Word = word,
			};

			var detail2 = ControlChars.Check("f.html", Build(), new ContentQualityConfig()).Single().Detail;
			var detected = new IssueTracking.IssueRecord
			{
				Source = "auto",
				Type = "QUALITY",
				Url = "https://example.com/a",
				Status = "new",
				Word = WordFromDetail(detail2),
			};

			var merged = IssueTracking.Merge(
				existing: new List<IssueTracking.IssueRecord> { ticketed },
				detected: new List<IssueTracking.IssueRecord> { detected });

			// The ticketed row is retained verbatim (still 'pending'), not dropped.
			var survivor = merged.SingleOrDefault(r => r.Url == "https://example.com/a");
			Assert.NotNull(survivor);
			Assert.Equal("pending", survivor!.Status);
		}

		[Fact]
		public void FourTicketedListItems_AllSurviveMerge_NoneDropped()
		{
			// The regression in one assertion: four distinct <li> tickets must all
			// survive an unchanged re-crawl. Under the old prose-based Word they
			// shared one Key and three were lost.
			HtmlDocument Build() => Doc(
				"<html><body><ul>" +
				$"<li>one{Zwsp}</li><li>two{Zwsp}</li><li>three{Zwsp}</li><li>four{Zwsp}</li>" +
				"</ul></body></html>");

			var words = ControlChars.Check("f.html", Build(), new ContentQualityConfig())
				.Select(i => WordFromDetail(i.Detail)).ToList();

			var ticketed = words.Select(w => new IssueTracking.IssueRecord
			{
				Source = "triage",
				Type = "QUALITY",
				Url = "https://example.com/a",
				Status = "pending",
				Word = w,
			}).ToList();

			var detected = ControlChars.Check("f.html", Build(), new ContentQualityConfig())
				.Select(i => new IssueTracking.IssueRecord
				{
					Source = "auto",
					Type = "QUALITY",
					Url = "https://example.com/a",
					Status = "new",
					Word = WordFromDetail(i.Detail),
				}).ToList();

			var merged = IssueTracking.Merge(ticketed, detected);

			var survivors = merged.Where(r =>
				r.Url == "https://example.com/a" && r.Status == "pending").ToList();
			Assert.Equal(4, survivors.Count);
		}
	}
}
