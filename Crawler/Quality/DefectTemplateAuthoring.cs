using System.Text;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	internal static class DefectTemplateAuthoring
	{
		/// <summary>
		/// Runs the architect-class CMS template authoring defect checks against the
		/// raw downloaded HTML. Emits three distinct IssueTypes routed to the architect
		/// log (the 22-cms-template-authoring-defects dual-locale CSV pair):
		///
		///   EMBEDDED_BOM_IN_BODY
		///     UTF-8 BOM (U+FEFF, byte sequence EF BB BF) appearing at any position
		///     other than offset 0. A leading BOM at offset 0 is the legitimate
		///     UTF-8 signature and is NOT flagged. Each subsequent BOM is an
		///     embedded BOM — almost always the residue of concatenating multiple
		///     UTF-8-with-signature template fragments without stripping the
		///     residual signature bytes. One finding per file with occurrence count.
		///
		///   INVISIBLE_CHAR_IN_BODY
		///     Zero-width characters, bidi control marks, line/paragraph separators,
		///     and C0/C1 control codes in body text whose parent element is NOT in
		///     <see cref="ContentQualityConfig.ContentQualityBlockElements"/>. The
		///     parent-element scope filter routes findings: invisibles inside
		///     p/h*/li/td/th are editor-paste-class (caught elsewhere or untreated);
		///     invisibles outside those are template-emitted (architect-class).
		///     One finding per (file, codepoint) — never per occurrence — with
		///     occurrence count and first container surfaced in the Detail/Excerpt.
		///
		///   WORD_SPLIT_BY_FORMATTING
		///     A word fractured for looks: several consecutive words each have
		///     their first letter wrapped in its own phrasing element (e.g.
		///     &lt;b&gt;I&lt;/b&gt;nternational &lt;b&gt;B&lt;/b&gt;ank
		///     &lt;b&gt;A&lt;/b&gt;ccount), which splits the word apart for screen
		///     readers and search engines. Flagged when THREE OR MORE such
		///     single-letter phrasing elements, each glued (no whitespace) to a
		///     lowercase word continuation, occur within ONE block. The >=3
		///     threshold passes over a lone drop-cap; the glued-lowercase condition
		///     passes over math (&lt;i&gt;x&lt;/i&gt; + &lt;i&gt;y&lt;/i&gt;) and
		///     single-letter emphasis. Deliberately distinct in NAME and (architect)
		///     LOG from SPLIT_WORD_ANCHOR: an anchor closing mid-word is a
		///     FUNCTIONAL link defect (main log), per-letter formatting is an
		///     AESTHETIC markup defect — different fix, different reader workflow,
		///     so they are never bucketed together. One finding per block.
		///
		/// Why raw bytes for BOM: the UTF-8 string decoder silently consumes a
		/// leading BOM and may also normalise embedded ones, masking the very bug
		/// we want to detect. Byte-level scan sees every occurrence faithfully.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckCmsTemplateAuthoringDefects(
			string filename, byte[] rawBytes, string html, ContentQualityConfig config)
		{
			// ── EMBEDDED_BOM_IN_BODY ──────────────────────────────────────────
			// UTF-8 BOM byte sequence: EF BB BF.
			var bomOffsets = FindUtf8BomOffsets(rawBytes);
			if (bomOffsets.Count > 0)
			{
				bool leadingBomPresent = bomOffsets[0] == 0;
				var embeddedOffsets = leadingBomPresent
					? bomOffsets.GetRange(1, bomOffsets.Count - 1)
					: bomOffsets;

				if (embeddedOffsets.Count > 0)
				{
					var detail = leadingBomPresent
						? $"BOM (U+FEFF) found at {embeddedOffsets.Count} embedded position(s); file also has the legitimate leading BOM at offset 0."
						: $"BOM (U+FEFF) found at {embeddedOffsets.Count} embedded position(s); no leading BOM.";

					// Excerpt = first embedded offset with surrounding bytes rendered as text.
					var firstOffset = embeddedOffsets[0];
					yield return new QualityIssue(
						filename,
						"EMBEDDED_BOM_IN_BODY",
						detail,
						BuildBomExcerpt(rawBytes, firstOffset));
				}
			}

			// ── INVISIBLE_CHAR_IN_BODY ────────────────────────────────────────
			// Walk DOM text nodes; restrict to text whose parent element is NOT
			// in ContentQualityBlockElements (editor-authored prose) and NOT in
			// the head/script/style/noscript skip set.
			var blockElements = config.ContentQualityBlockElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			// Aggregate findings: one per (codepoint) for this file. The value is
			// (count, first container start tag, first excerpt) — used to build the
			// single finding emitted per codepoint with occurrence count surfaced.
			var perCodepoint = new Dictionary<int, (int Count, string FirstContainerTag, string FirstExcerpt, string Name)>();

			var doc = new HtmlDocument();
			doc.LoadHtml(html);

			foreach (var textNode in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Text))
			{
				var parent = textNode.ParentNode;
				if (parent == null)
				{
					continue;
				}

				// Skip head/script/style/noscript subtrees.
				if (IsInsideArchitectScopeSkipAncestor(parent))
				{
					continue;
				}

				// Skip editor-class prose containers.
				if (blockElements.Contains(parent.Name))
				{
					continue;
				}

				var text = textNode.InnerText;
				if (string.IsNullOrEmpty(text))
				{
					continue;
				}

				foreach (var ch in text)
				{
					if (!DefectDetectionHelpers.IsArchitectClassInvisible(ch))
					{
						continue;
					}

					var name = NameArchitectInvisible(ch);

					if (perCodepoint.TryGetValue(ch, out var existing))
					{
						perCodepoint[ch] = (existing.Count + 1, existing.FirstContainerTag, existing.FirstExcerpt, existing.Name);
					}
					else
					{
						var containerTag = DefectDetectionHelpers.FormatContainerStartTag(parent);
						var excerpt = $"[{containerTag}] {RenderTextWithInvisibleMarkers(text)}";
						perCodepoint[ch] = (1, containerTag, excerpt, name);
					}
				}
			}

			foreach (var (codepoint, agg) in perCodepoint)
			{
				yield return new QualityIssue(
					filename,
					"INVISIBLE_CHAR_IN_BODY",
					$"{agg.Name} found at {agg.Count} position(s) in non-editorial container content (first inside {agg.FirstContainerTag}).",
					agg.FirstExcerpt);
			}

			// ── WORD_SPLIT_BY_FORMATTING ──────────────────────────────────────
			// A word fractured for looks: each of several consecutive words has its
			// first letter wrapped in its own phrasing element (<b>I</b>nternational
			// <b>B</b>ank <b>A</b>ccount …), splitting the word for screen readers and
			// search engines. Counted per block; emitted once per block at >=3. The
			// same DOM is reused (no second parse). Blocks recorded in first-seen
			// document order so the log is deterministic.
			var splitBlocks = new List<HtmlNode>();
			var splitCounts = new Dictionary<HtmlNode, int>();

			foreach (var el in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element && WordSplitPhrasingTags.Contains(n.Name)))
			{
				if (IsInsideArchitectScopeSkipAncestor(el))
				{
					continue;
				}

				// The element's own text must be exactly one letter (a bolded initial),
				// ignoring any incidental whitespace inside the tag (e.g. "<b> A</b>").
				var letter = (el.InnerText ?? string.Empty).Trim();
				if (letter.Length != 1 || !char.IsLetter(letter[0]))
				{
					continue;
				}

				// The continuation must be glued: the immediately following text node
				// begins, with NO whitespace, with a lowercase letter (the rest of the
				// word). This excludes math ("<i>x</i> + …") and lone emphasis.
				var next = el.NextSibling;
				if (next == null || next.NodeType != HtmlNodeType.Text)
				{
					continue;
				}

				var tail = next.InnerText;
				if (string.IsNullOrEmpty(tail) || char.IsWhiteSpace(tail[0]) || !char.IsLower(tail[0]))
				{
					continue;
				}

				var parent = el.ParentNode;
				if (parent == null)
				{
					continue;
				}

				if (!splitCounts.ContainsKey(parent))
				{
					splitBlocks.Add(parent);
					splitCounts[parent] = 0;
				}

				splitCounts[parent]++;
			}

			foreach (var block in splitBlocks)
			{
				if (splitCounts[block] < WordSplitByFormattingThreshold)
				{
					continue;
				}

				// Context shows the REASSEMBLED block text (InnerText glues the phrasing
				// children back into the words a reader sees), windowed to the excerpt radius.
				var blockText = System.Net.WebUtility.HtmlDecode(block.InnerText).Trim();
				var excerptText = blockText.Length > config.ContentQualityExcerptRadius
					? blockText[..config.ContentQualityExcerptRadius] + "…"
					: blockText;

				yield return new QualityIssue(
					filename,
					"WORD_SPLIT_BY_FORMATTING",
					"The first letters of several words in a row are each formatted on their own (for example, "
						+ "each letter bolded), which splits the words apart for screen readers and search engines. "
						+ "To highlight an abbreviation, wrap the full term in an <abbr> tag instead.",
					$"[{DefectDetectionHelpers.FormatContainerStartTag(block)}] {excerptText}");
			}
		}

		/// <summary>
		/// Phrasing/formatting elements counted by WORD_SPLIT_BY_FORMATTING — the "make it
		/// pretty" wrappers an editor uses to style a single letter. Mirrors the safe-core
		/// glue set used by the spell traverser (minus the void &lt;wbr&gt;, which holds no
		/// letter). Case-insensitive.
		/// </summary>
		private static readonly HashSet<string> WordSplitPhrasingTags =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"b", "i", "em", "strong", "mark", "small", "u", "s", "span",
			};

		/// <summary>
		/// Minimum single-letter phrasing elements (each glued to a lowercase continuation)
		/// within one block before WORD_SPLIT_BY_FORMATTING fires. Three separates a systemic
		/// per-letter authoring pattern from a lone drop-cap or one-off emphasis.
		/// </summary>
		private const int WordSplitByFormattingThreshold = 3;

		/// <summary>
		/// Returns byte offsets of UTF-8 BOM occurrences (EF BB BF) in <paramref name="bytes"/>.
		/// </summary>
		internal static List<int> FindUtf8BomOffsets(byte[] bytes)
		{
			var offsets = new List<int>();
			if (bytes == null || bytes.Length < 3)
			{
				return offsets;
			}

			for (int i = 0; i <= bytes.Length - 3; i++)
			{
				if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF)
				{
					offsets.Add(i);
					i += 2; // skip past this BOM; next iteration's i++ moves to next byte after
				}
			}
			return offsets;
		}

		/// <summary>Human-readable name for an architect-class invisible codepoint.</summary>
		internal static string NameArchitectInvisible(char ch)
		{
			if (ch == '\u200B')
			{
				return "ZWSP (U+200B)";
			}

			if (ch == '\u200C')
			{
				return "ZWNJ (U+200C)";
			}

			if (ch == '\u200D')
			{
				return "ZWJ (U+200D)";
			}

			if (ch == '\u2060')
			{
				return "WJ (U+2060)";
			}

			if (ch == '\uFEFF')
			{
				return "ZWNBSP/BOM (U+FEFF)";
			}

			if (ch == '\u2028')
			{
				return "LINE SEPARATOR (U+2028)";
			}

			if (ch == '\u2029')
			{
				return "PARAGRAPH SEPARATOR (U+2029)";
			}

			if (ch >= '\u202A' && ch <= '\u202E')
			{
				return $"bidi control (U+{(int)ch:X4})";
			}

			if (ch >= '\u2066' && ch <= '\u2069')
			{
				return $"bidi isolate (U+{(int)ch:X4})";
			}

			if (ch < 0x20)
			{
				return $"C0 control (U+{(int)ch:X4})";
			}

			if (ch >= 0x80 && ch <= 0x9F)
			{
				return $"C1 control (U+{(int)ch:X4})";
			}

			return $"invisible (U+{(int)ch:X4})";
		}

		private static readonly HashSet<string> ArchitectScopeSkipAncestors =
			new(StringComparer.OrdinalIgnoreCase) { "head", "title", "meta", "script", "style", "noscript" };

		/// <summary>
		/// True if <paramref name="node"/> or any ancestor is in head / title / meta /
		/// script / style / noscript. Used to suppress architect-scope invisible-char
		/// findings inside head metadata (covered by CONTROL_CHARS_IN_CONTENT, editor-
		/// targeted) and inside script/style/noscript (raw content, not body prose).
		/// </summary>
		internal static bool IsInsideArchitectScopeSkipAncestor(HtmlNode node)
		{
			for (var cur = node; cur != null; cur = cur.ParentNode)
			{
				if (ArchitectScopeSkipAncestors.Contains(cur.Name))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Renders a text string with architect-class invisible characters replaced by
		/// readable markers (e.g. [BOM U+FEFF], [ZWSP U+200B]), so the architect can
		/// see exactly what's there. Caps the output length to keep log lines readable.
		/// </summary>
		internal static string RenderTextWithInvisibleMarkers(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return s;
			}

			var sb = new StringBuilder(s.Length + 16);
			foreach (var ch in s)
			{
				if (DefectDetectionHelpers.IsArchitectClassInvisible(ch))
				{
					sb.Append('[').Append(NameArchitectInvisible(ch)).Append(']');
				}
				else
				{
					sb.Append(ch);
				}
			}
			var result = sb.ToString().Trim();
			const int max = 200;
			if (result.Length > max)
			{
				result = result[..(max - 1)] + "…";
			}

			return result;
		}

		/// <summary>
		/// Builds an excerpt for an EMBEDDED_BOM_IN_BODY finding centred on the first
		/// embedded BOM offset, with surrounding bytes decoded as UTF-8 and the BOM
		/// itself rendered as [BOM U+FEFF] so it's visible in the log.
		/// </summary>
		internal static string BuildBomExcerpt(byte[] bytes, int bomOffset)
		{
			const int radius = 40;
			int start = Math.Max(0, bomOffset - radius);
			int end = Math.Min(bytes.Length, bomOffset + 3 + radius);

			// Decode segments either side of the BOM separately to avoid the
			// decoder consuming the BOM byte sequence as a marker.
			string before = start < bomOffset
				? SafeDecodeUtf8(bytes, start, bomOffset - start)
				: string.Empty;
			string after = bomOffset + 3 < end
				? SafeDecodeUtf8(bytes, bomOffset + 3, end - (bomOffset + 3))
				: string.Empty;

			return $"offset {bomOffset}: …{before}[BOM U+FEFF]{after}…";
		}

		private static string SafeDecodeUtf8(byte[] bytes, int index, int count)
		{
			try
			{
				var raw = Encoding.UTF8.GetString(bytes, index, count);
				// Render any architect-class invisibles inside the excerpt too.
				return RenderTextWithInvisibleMarkers(raw);
			}
			catch { return "<decode-error>"; }
		}
	}
}
