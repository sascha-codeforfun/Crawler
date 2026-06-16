namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using HtmlAgilityPack;

	/// <summary>
	/// Builds the human-facing context ("Context:" line) for a single located finding. This is
	/// the piece whose absence produced the original bug — a flagged word with no visible
	/// context. Here context is INTRINSIC: it is derived from the finding's own node and span,
	/// not reconstructed from a word-keyed lookup, so it cannot point at the wrong occurrence.
	///
	/// The excerpt is built from CANONICAL text (same rule the tokenizer and span use), so the
	/// flagged word always appears in the excerpt exactly as flagged — no soft-hyphen / dash /
	/// decode mismatch between the word and its surrounding text.
	///
	/// SourceKind decides the shape of the context:
	///   * TextNode  → the surrounding BLOCK's text, windowed around the occurrence so the word
	///                 sits in its sentence rather than at the start of a long block.
	///   * Attribute → the attribute value itself IS the context (an attribute has no
	///                 surrounding block).
	///   * Meta      → the meta content value itself.
	///
	/// The block-element set defines which ancestors count as a "block" for the block-ancestor
	/// walk, so an excerpt is the nearest enclosing block's text around the word.
	/// </summary>
	public static class ExcerptBuilder
	{
		private const int WindowRadius = 75; // chars of context on each side of the word

		private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
		{
			"p", "h1", "h2", "h3", "h4", "h5", "h6",
			"li", "td", "th", "dt", "dd", "figcaption",
			"blockquote", "div", "section", "article",
			"header", "footer", "aside", "main", "nav"
		};

		public static string Build(SpellFinding finding)
		{
			switch (finding.Source)
			{
				case RunSource.Meta:
					// A meta finding's value lives in the element's "content" attribute, regardless
					// of how the path is labelled (e.g. meta[@name=description]).
					return Canonicalizer.Canonicalize(finding.Node.GetAttributeValue("content", string.Empty));

				case RunSource.Attribute:
					// The value is the context. Canonicalize so it reads as the word was checked.
					return Canonicalizer.Canonicalize(finding.Node.GetAttributeValue(AttributeNameFromPath(finding.SourcePath), string.Empty));

				case RunSource.Script:
					// A script finding's context is a raw-source window around the literal — the
					// surrounding assignment/call that lets an operator judge a bare technical id —
					// built at extraction time and carried on the finding. It is returned as-is: it is
					// already a bounded, single-line window centred on the literal's known position, so
					// re-windowing here (which would re-centre on the decoded word and cannot align with
					// the raw, possibly-escaped source) is neither needed nor correct.
					return finding.ExcerptText ?? string.Empty;

				case RunSource.TextNode:
				default:
					return BuildBlockExcerpt(finding);
			}
		}

		private static string BuildBlockExcerpt(SpellFinding finding)
		{
			HtmlNode? block = finding.Node;
			while (block != null && block.NodeType != HtmlNodeType.Document)
			{
				if (block.NodeType == HtmlNodeType.Element && BlockElements.Contains(block.Name))
				{
					break;
				}

				block = block.ParentNode;
			}

			// Block found → its canonical text is the context source. No block ancestor (e.g. a
			// <title> or a stray text node) → the finding node's own text is the context.
			string source = (block != null && block.NodeType == HtmlNodeType.Element)
				? Canonicalizer.Canonicalize(block.InnerText)
				: Canonicalizer.Canonicalize(finding.Node.InnerText);

			if (source.Length == 0)
			{
				return string.Empty;
			}

			return Window(source, finding.Word);
		}

		/// <summary>
		/// Center a window on the first WHOLE-WORD occurrence of <paramref name="word"/>, located by
		/// <see cref="SpellTokenizer.IndexOfWholeWord"/> so it agrees with the tokenizer: a flagged
		/// word that is a prefix/substring of a longer word ("Adress" in "Adressen") or part of a
		/// hyphenated compound ("Adress" in "Adress-daten") earlier in the block does not pull the
		/// window onto the wrong span. Ordinal (case-sensitive): the flagged word appears in
		/// canonical block text exactly as flagged.
		/// </summary>
		private static string Window(string text, string word)
		{
			int at = SpellTokenizer.IndexOfWholeWord(text, word);
			if (at < 0 || text.Length <= 2 * WindowRadius + word.Length)
			{
				return text; // short enough to show whole, or word not locatable as a whole word
			}

			int start = Math.Max(0, at - WindowRadius);
			int end = Math.Min(text.Length, at + word.Length + WindowRadius);

			string slice = text.Substring(start, end - start);
			if (start > 0)
			{
				slice = "…" + slice;
			}

			if (end < text.Length)
			{
				slice += "…";
			}

			return slice;
		}

		/// <summary>Extract the attribute name from a source path like "img[@alt]" or "meta[@name=description]".</summary>
		private static string AttributeNameFromPath(string sourcePath)
		{
			int at = sourcePath.IndexOf("[@", StringComparison.Ordinal);
			if (at < 0)
			{
				return string.Empty;
			}

			int start = at + 2;
			int end = start;
			while (end < sourcePath.Length && sourcePath[end] != ']' && sourcePath[end] != '=')
			{
				end++;
			}

			return sourcePath.Substring(start, end - start);
		}
	}
}
