namespace Crawler.SpellCheck
{
	using HtmlAgilityPack;

	/// <summary>
	/// Origin kind of a <see cref="TextRun"/>. This is the ONLY thing that branches
	/// on source in the new pipeline, and it only matters when an excerpt is rendered
	/// for a confirmed finding — the spell check itself is identical across kinds.
	/// Body text and attribute values are the same problem until the context line is drawn.
	/// </summary>
	public enum RunSource
	{
		/// <summary>Text node content inside an element (labelled e.g. "p[#text]").</summary>
		TextNode,

		/// <summary>An attribute value (labelled e.g. "img[@alt]").</summary>
		Attribute,

		/// <summary>A &lt;meta&gt; content value (labelled e.g. "meta[@name=description]").</summary>
		Meta,

		/// <summary>
		/// A decoded string literal lifted from inline &lt;script&gt; JavaScript (labelled
		/// e.g. "script[L12:34]" — line:column of the literal within the script body). Its
		/// excerpt context cannot be re-derived from the DOM node (the node holds the raw,
		/// undecoded body), so a Script finding carries its own excerpt text on the finding.
		/// </summary>
		Script
	}

	/// <summary>
	/// A unit of text harvested from the DOM, bound to the node it came from.
	/// Provenance travels with the text from this point on; it is never reconstructed
	/// by a later string lookup. <see cref="RawText"/> is the verbatim node/attribute
	/// text — canonicalization is a downstream stage and does not mutate the run.
	/// </summary>
	public sealed record TextRun(
		HtmlNode Node,
		RunSource Source,
		string SourcePath,
		string RawText)
	{
		/// <summary>
		/// For <see cref="RunSource.Script"/> runs only: a window of the RAW script/handler source
		/// around the extracted literal, built once at extraction time (where the body and the
		/// literal's offset are both in hand) and carried for triage display. It is NOT checked —
		/// <see cref="RawText"/> remains the decoded literal the spell-checker tokenizes. This exists
		/// because the surrounding code (the assignment, the call) is the context an operator needs to
		/// judge a bare technical id, and it cannot be re-derived from the DOM node afterwards: the
		/// node holds the raw, undecoded body, not the decoded string. Null for every non-script run.
		/// </summary>
		public string? ScriptContext { get; init; }
	}

	/// <summary>
	/// A token cut from a <see cref="TextRun"/>, retaining its character span back into
	/// the run's text so the originating node and position stay recoverable. There is no
	/// word-keyed map anywhere in this pipeline — a token always carries its own origin,
	/// so the occurrence cannot be confused with another occurrence of the same word.
	/// </summary>
	public sealed record Token(
		TextRun Run,
		string Text,
		int Start,
		int Length);
}
