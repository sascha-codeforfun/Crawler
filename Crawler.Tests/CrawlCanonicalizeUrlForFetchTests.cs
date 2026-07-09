using Xunit;
using Crawler.Urls;

namespace Crawler.Tests
{
	/// <summary>
	/// Locks the behavior of <see cref="Crawl.CanonicalizeUrlForFetch"/>, the
	/// pre-fetch URL canonicalization step that decides:
	///   - whether to treat the URL as a modal carrier (unwrap via
	///     <c>Query.ExtractModalUrl</c>), or
	///   - to strip the query string entirely (blanket <c>Split('?')[0]</c>).
	///
	/// Regression here silently double-downloads (canonicalization broken) or
	/// drops URLs the operator wanted kept (over-strips). Locking these tests
	/// catches both classes of drift.
	///
	/// The modal-parameter list is now <c>List&lt;ModalQueryParam&gt;</c>; the
	/// tests below use bare parameter names (empty AppendSuffix), so the expected
	/// results are identical to the pre-change string-list behavior. Suffix
	/// application is covered in <c>QueryTests.ExtractModalUrl_*Suffix*</c>.
	///
	/// No HTTP, no filesystem — pure-function tests.
	/// </summary>
	public class CrawlCanonicalizeUrlForFetchTests
	{
		// ── No modal parameter configured → blanket query strip ──────────────

		[Fact]
		public void NoModalParams_WithQueryString_StripsQueryString()
		{
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?utm_source=email&utm_medium=newsletter",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: []);

			Assert.Equal("https://www.example.com/page.html", result);
		}

		[Fact]
		public void NoModalParams_WithoutQueryString_ReturnsUrlUnchanged()
		{
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: []);

			Assert.Equal("https://www.example.com/page.html", result);
		}

		[Fact]
		public void EmptyModalParams_WithFragmentAndQuery_StripsFromQuestionMark()
		{
			// Behavioral lock: Split('?')[0] drops everything from the '?' onward,
			// including any fragment that follows the query. Fragments rarely arrive
			// at the fetch layer (browsers don't send them), but the canonicalization
			// is deterministic on whatever string it receives.
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?q=foo#section",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: []);

			Assert.Equal("https://www.example.com/page.html", result);
		}

		// ── Modal parameter configured but not present in URL → strip path ──

		[Fact]
		public void ModalParamConfigured_NotInUrl_StripsQueryString()
		{
			// 'lightbox' is configured but the URL does not contain it → fall
			// through to the blanket query strip.
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?utm_source=email",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: [new ModalQueryParam("lightbox")]);

			Assert.Equal("https://www.example.com/page.html", result);
		}

		// ── Modal parameter present → unwrap via ExtractModalUrl ─────────────

		[Fact]
		public void ModalParamPresent_UrlEncodedAbsoluteTarget_UnwrapsToInnerUrl()
		{
			// Modal carrier with the inner URL URL-encoded in the lightbox param.
			// Canonicalization should unwrap to the inner URL.
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?lightbox=https%3A%2F%2Fwww.example.com%2Fmodal%2Fbox1.htm",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: [new ModalQueryParam("lightbox")]);

			Assert.Equal("https://www.example.com/modal/box1.htm", result);
		}

		[Fact]
		public void ModalParamPresent_RelativeTarget_ResolvedAgainstWebsiteUrl()
		{
			// Modal carrier with a relative inner URL: ExtractModalUrl resolves it
			// by prefixing the websiteUrl base. The lock here protects that
			// resolution: a regression that, say, returned the relative path
			// verbatim would break the downstream fetch.
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?lightbox=%2Fmodal%2Fbox1.htm",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: [new ModalQueryParam("lightbox")]);

			Assert.Equal("https://www.example.com/modal/box1.htm", result);
		}

		[Fact]
		public void ModalParamPresent_CaseInsensitiveContainsMatch_StillUnwraps()
		{
			// The Contains check uses OrdinalIgnoreCase: 'LightBox' configured vs
			// 'lightbox' in the URL should still match. Regression to a case-
			// sensitive comparison would silently fall through to the strip path
			// and cause double-downloads (carrier vs inner).
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?lightbox=https%3A%2F%2Fwww.example.com%2Fmodal%2Fbox1.htm",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: [new ModalQueryParam("LightBox")]);

			Assert.Equal("https://www.example.com/modal/box1.htm", result);
		}

		[Fact]
		public void ModalParamPresent_MultipleConfigured_FirstMatchWins()
		{
			// FirstOrDefault: the order of modalQueryParameters matters when more
			// than one matches. The first match in the list wins; the rest are
			// ignored. This locks the order-sensitivity in the current code so a
			// refactor to e.g. LastOrDefault or longest-match-wins would surface.
			//
			// URL contains both 'modal' and 'lightbox'; modal is first in config →
			// ExtractModalUrl is called with 'modal' as the parameter name.
			var result = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?modal=%2Ffrom-modal.htm&lightbox=%2Ffrom-lightbox.htm",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: [new ModalQueryParam("modal"), new ModalQueryParam("lightbox")]);

			Assert.Equal("https://www.example.com/from-modal.htm", result);
		}

		// ── Defensive: helper does not mutate input list ─────────────────────

		[Fact]
		public void CanonicalizeUrlForFetch_DoesNotMutateModalParametersList()
		{
			// Defensive lock: the helper should not reorder or modify the modal-
			// parameters list (operators may share a single list across many
			// calls). Verified by checking list identity and contents after call.
			var modalParams = new List<ModalQueryParam>
			{
				new("lightbox"),
				new("modal"),
			};
			var snapshot = modalParams.ToList();

			_ = Crawl.CanonicalizeUrlForFetch(
				url: "https://www.example.com/page.html?lightbox=%2Fbox.htm",
				websiteUrl: "https://www.example.com",
				modalQueryParameters: modalParams);

			Assert.Equal(snapshot, modalParams);
		}
	}
}
