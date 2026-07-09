namespace Crawler.SpellCheck
{
	using System;

	// Detects the hamzat al-waṣl / hamzat al-qaṭʿ spelling error in an Arabic token: a
	// word written with an initial hamza-marked alif (إ U+0625 or أ U+0623) where the
	// correct form takes a BARE alif (ا U+0627). Canonical case: the CAPTCHA label
	// "إضغط" ("press"), which should be "اضغط" — one of the most common Arabic
	// orthographic slips, made even by native speakers.
	//
	// MECHANISM — a dictionary roundtrip; no Arabic morphology is encoded here. The `ar`
	// dictionary adjudicates, via an injected accept-check delegate:
	//   1. token starts with إ or أ,
	//   2. candidate = that initial replaced by bare alif ا,
	//   3. surface ONLY if `ar` REJECTS the original AND ACCEPTS the candidate.
	// That reject→accept flip is the whole safety story: it is what lets the detector run
	// broadly on every إ/أ-initial token WITHOUT false-positiving on legitimate qaṭʿ words
	// (أكل "ate", إسلام "Islam" — the dictionary accepts those as-is, so no flip, silence).
	// The detector never asserts "initial hamza is wrong"; it asserts "for THIS token the
	// bare-alif spelling is the one the dictionary recognises" — a fact the dictionary
	// confirmed, not a linguistic guess. Hence the enricher renders it as Medium / review.
	//
	// The accept-check is INJECTED (the detector stays ignorant of how `ar` is consulted),
	// so it is pure and unit-testable with a mock delegate — no live dictionary required.
	// STRUCTURAL ONLY: returns facts; the English operator copy lives in the enricher.
	public static class ArabicAlifHamzaDetector
	{
		// Stable identifier for this enrichment class.
		public const string Kind = "ARABIC_ALIF_HAMZA_WASL";

		private const char HamzaBelow = '\u0625'; // إ  ALEF WITH HAMZA BELOW
		private const char HamzaAbove = '\u0623'; // أ  ALEF WITH HAMZA ABOVE
		private const char BareAlif = '\u0627';   // ا  ALEF

		// Returns true and yields an ArabicAlifFinding when the token starts with إ/أ and
		// the bare-alif roundtrip flips reject→accept under the injected `arAccepts`.
		public static bool TryDetect(string token, Func<string, bool> arAccepts, out ArabicAlifFinding finding)
		{
			finding = null!;
			if (arAccepts == null || string.IsNullOrEmpty(token) || token.Length < 2)
			{
				return false;
			}

			var initial = token[0];
			if (initial != HamzaBelow && initial != HamzaAbove)
			{
				return false; // not an initial hamza-marked alif — out of scope
			}

			var candidate = BareAlif + token.Substring(1);

			// The roundtrip: original must REJECT and the bare-alif form must ACCEPT.
			if (arAccepts(token) || !arAccepts(candidate))
			{
				return false;
			}

			finding = new ArabicAlifFinding(token, candidate, initial.ToString(), initial);
			return true;
		}
	}

	// Result of a positive detection. Original is the as-found token; Suggestion is the
	// dictionary-confirmed bare-alif form; InitialChar/InitialCodePoint identify which
	// hamza form was found (إ U+0625 / أ U+0623). Structural facts only — no copy.
	public sealed record ArabicAlifFinding(
		string Original,
		string Suggestion,
		string InitialChar,
		int InitialCodePoint)
	{
		// Code point formatted as U+XXXX for display (e.g. "U+0625").
		public string CodePointLabel => "U+" + InitialCodePoint.ToString("X4");
	}
}
