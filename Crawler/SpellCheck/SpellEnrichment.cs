namespace Crawler.SpellCheck
{
	using System.Collections.Generic;
	using System.Linq;

	// How sure an enrichment is. Drives the triage card's tone: a Certain finding
	// reads as a flat defect (a Cyrillic+Latin mix CANNOT be correct); a Medium one
	// reads as "possible — review before ticketing" (e.g. a dictionary-roundtrip
	// suggestion, where rare words could legitimately differ). The renderer picks
	// the marker/voice from this — providers never hard-code alarm level.
	public enum EnrichmentConfidence
	{
		Certain,
		Medium,
	}

	// What an enricher receives. The WHOLE finding, not just the word — a future
	// provider may need the language (e.g. "is an Arabic dictionary configured?")
	// or the source. Homoglyph uses only Word; that is fine. Pass-everything keeps
	// the contract stable as providers are added.
	public sealed record SpellEnrichmentContext(string Word, string Language, string Source);

	// An operator-facing explanation attached to one finding. Lines are calm
	// English prose (the payload — a non-fluent operator must be able to act).
	// HighlightOffsets are positions IN THE WORD whose characters are the offenders,
	// so the renderer can mark just those (empty when an enrichment highlights
	// nothing in the word itself).
	public sealed record SpellEnrichment(
		string Kind,
		EnrichmentConfidence Confidence,
		IReadOnlyList<string> Lines,
		IReadOnlyList<int> HighlightOffsets);

	// A pluggable explainer for a spelling finding. Returns true + an enrichment
	// when it has something to say about this finding; false to stay silent.
	public interface ISpellEnricher
	{
		bool TryEnrich(SpellEnrichmentContext context, out SpellEnrichment enrichment);
	}

	// First enricher: labels a Cyrillic+Latin mixed-alphabet token. Pure (no
	// dependencies) — wraps HomoglyphDetector and writes the calm operator copy.
	public sealed class HomoglyphEnricher : ISpellEnricher
	{
		public bool TryEnrich(SpellEnrichmentContext context, out SpellEnrichment enrichment)
		{
			enrichment = null!;
			if (!HomoglyphDetector.TryDetect(context.Word, out var finding))
			{
				return false;
			}

			var list = string.Join(", ",
				finding.Intruders.Select(c => $"'{c.Char}' {c.CodePointLabel} at position {c.Index}"));
			var n = finding.Intruders.Count;
			var letterWord = n == 1 ? "letter" : "letters";

			var lines = new List<string>
			{
				"Mixed-alphabet token — this is corruption, not a typo.",
				$"Mostly {finding.MajorityAlphabet}, but carries {n} {finding.IntruderAlphabet} {letterWord}: {list}. "
					+ "No word is spelled across two alphabets, so it can never match any dictionary.",
				$"Likely a look-alike substitution. Replace the highlighted {finding.IntruderAlphabet} "
					+ $"character{(n == 1 ? string.Empty : "s")} with the intended {finding.MajorityAlphabet} "
					+ "letter, then re-check.",
			};

			enrichment = new SpellEnrichment(
				HomoglyphDetector.Kind,
				EnrichmentConfidence.Certain,
				lines,
				finding.Intruders.Select(c => c.Index).ToList());
			return true;
		}
	}

	// Second enricher: labels a non-ASCII look-alike of a syntax character embedded
	// in a code identifier (e.g. U+FF70 in "paddingｰtop"). Pure (no dependencies) —
	// wraps ConfusablePunctuationDetector and writes code-defect copy. Sibling of
	// HomoglyphEnricher, kept separate on purpose (see the detector's class note).
	public sealed class ConfusablePunctuationEnricher : ISpellEnricher
	{
		public bool TryEnrich(SpellEnrichmentContext context, out SpellEnrichment enrichment)
		{
			enrichment = null!;

			// Code-context gate. Only fire on script-scan findings, where a non-ASCII
			// look-alike of ASCII syntax is unambiguous corruption. Script-scan Source
			// carries the "· reach" marker (e.g. "{bundle}.js · reach 5"); content
			// findings carry an element selector (p[#text]) instead. The gate is load-
			// bearing: a confusable hyphen between Latin letters CAN be legitimate
			// typography in prose (German "E‑Mail" uses U+2011), so firing on content
			// would false-positive. Prose confusables are a separate, deferred concern.
			if (!IsCodeSource(context.Source))
			{
				return false;
			}

			if (!ConfusablePunctuationDetector.TryDetect(context.Word, out var finding))
			{
				return false;
			}

			var n = finding.Hits.Count;
			var charWord = n == 1 ? "character" : "characters";
			var lookalikeWord = n == 1 ? "look-alike" : "look-alikes";
			var list = string.Join(", ",
				finding.Hits.Select(h =>
					$"position {h.Index}: '{h.Char}' {h.CodePointLabel} ({h.UnicodeName}), should be '{h.Canonical}'"));

			var lines = new List<string>
			{
				$"Confusable {charWord} in a code token — broken CSS/JS, not a spelling issue.",
				$"Contains {n} non-ASCII {lookalikeWord} of ASCII syntax — {list}. "
					+ "No code identifier uses these characters, so the token is malformed and the rule silently fails.",
				$"Likely correct token: '{finding.Suggestion}'. Ticket upstream to the source owner.",
			};

			enrichment = new SpellEnrichment(
				ConfusablePunctuationDetector.Kind,
				EnrichmentConfidence.Certain,
				lines,
				finding.Hits.Select(h => h.Index).ToList());
			return true;
		}

		// Script-scan findings carry the "· reach" marker (U+00B7) in their Source;
		// content findings carry an element selector. See the gate rationale above.
		private static bool IsCodeSource(string source) =>
			!string.IsNullOrEmpty(source)
			&& source.Contains("\u00B7 reach", System.StringComparison.Ordinal);
	}

	// Third enricher: the Arabic hamzat al-waṣl error (إ/أ where bare alif ا belongs).
	// UNLIKE the two pure enrichers, this one consults the `ar` dictionary (via
	// EnrichmentDictionaries) to run the roundtrip, and SELF-ACTIVATES only when an `ar`
	// dictionary is loaded — the presence of the delegate is the runtime "is it
	// configured?" answer. Medium confidence (the roundtrip is strong evidence, not
	// proof), so it renders as a calm review-suggestion, never a verdict.
	public sealed class ArabicAlifHamzaEnricher : ISpellEnricher
	{
		public bool TryEnrich(SpellEnrichmentContext context, out SpellEnrichment enrichment)
		{
			enrichment = null!;

			// Runtime presence gate: is an `ar` dictionary loaded (exact "ar" or an
			// "ar-*" locale variant)? Without it the roundtrip cannot run — stay silent.
			if (!EnrichmentDictionaries.TryResolveFamily("ar", out var arAccepts))
			{
				return false;
			}

			if (!ArabicAlifHamzaDetector.TryDetect(context.Word, arAccepts, out var finding))
			{
				return false;
			}

			var lines = new List<string>
			{
				"Possible Arabic spelling error — review before ticketing.",
				$"Token '{finding.Original}' begins with a hamza-marked alif ('{finding.InitialChar}' "
					+ $"{finding.CodePointLabel}). The bare-alif form '{finding.Suggestion}' is accepted by the "
					+ "ar dictionary; the as-found form is not. This is the common hamzat al-waṣl error — a "
					+ "connecting alif that should be written bare (ا) but was written as a cutting hamza (إ/أ).",
				$"Likely correct form: '{finding.Suggestion}'. Suggested action: ticket upstream to the "
					+ "locale-string owner.",
			};

			// Medium → ℹ marker, review-tone. Prose-only: no in-word highlight — the note
			// names the character and the fix, and an offset on an RTL token adds nothing
			// over the explicit copy.
			enrichment = new SpellEnrichment(
				ArabicAlifHamzaDetector.Kind,
				EnrichmentConfidence.Medium,
				lines,
				System.Array.Empty<int>());
			return true;
		}
	}

	// The registry + run point. Every consumer (triage card, ticket note, and the
	// passive log later) funnels through here so they all see the same enrichments.
	public static class SpellEnrichments
	{
		private static readonly IReadOnlyList<ISpellEnricher> Enrichers = new ISpellEnricher[]
		{
			new HomoglyphEnricher(),
			new ConfusablePunctuationEnricher(),
			new ArabicAlifHamzaEnricher(),
		};

		// All enrichments that apply to this finding (empty when none do).
		public static IReadOnlyList<SpellEnrichment> For(SpellEnrichmentContext context)
		{
			var result = new List<SpellEnrichment>();
			foreach (var enricher in Enrichers)
			{
				if (enricher.TryEnrich(context, out var enrichment))
				{
					result.Add(enrichment);
				}
			}

			return result;
		}

		// Union of in-word offender offsets across all enrichments — for the
		// two-colour occurrence render (offender characters marked, rest plain).
		public static IReadOnlyList<int> OffendersFor(SpellEnrichmentContext context) =>
			For(context).SelectMany(e => e.HighlightOffsets).Distinct().OrderBy(i => i).ToList();

		// The note that rides into IssueRecord.Comment on [T], so the operator's
		// comprehension travels with the ticket into TicketText.log. Empty when
		// there are no enrichments. Pure-text (no console colour).
		public static string TicketNote(SpellEnrichmentContext context)
		{
			// Join with a SPACE, not a newline. The note rides into IssueRecord.Comment,
			// and both the line-based ledger (IssueTracking.log) and the ticket renderer
			// run Comment through SanitizeField, which replaces \r/\n/\t with '/' to
			// protect the row format — so a newline-joined note would render as one
			// run-on line broken by stray '/'. The Lines are complete sentences, so a
			// space reads as clean prose and passes sanitization untouched. (The triage
			// CARD still renders the Lines individually; only this persisted note flattens.)
			var lines = For(context).SelectMany(e => e.Lines).ToList();
			return lines.Count == 0 ? string.Empty : string.Join(" ", lines);
		}
	}
}
