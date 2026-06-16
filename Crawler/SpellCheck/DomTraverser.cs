namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Text;
	using HtmlAgilityPack;
	using Crawler.Boilerplate;

	/// <summary>
	/// Stage 1 of the new spell pipeline: raw bytes → DOM → uniform TextRun stream.
	///
	/// Ground truth in: bytes are decoded with the same detector used everywhere else
	/// (<see cref="DetectEncoding.FromBytes"/>), parsed once, and the resulting DOM is the
	/// single source of truth. Nothing here reads a pre-simplified or pre-normalized
	/// artifact — the redesign owns its own parse so a word's location is never lost.
	///
	/// The harvester emits one run per BLOCK-LEVEL TEXT SEGMENT and every non-empty
	/// attribute value as a run. A block-level text segment is the text of a block,
	/// with the text of any inline phrasing children (b/i/em/strong/…) glued in place —
	/// because an inline phrasing element does NOT create a word boundary in the rendered
	/// page, it only fractures the DOM into sibling text nodes. So "&lt;b&gt;I&lt;/b&gt;nternational"
	/// is harvested as the single word "International", not the fragments "I"+"nternational".
	/// Block elements and &lt;br&gt; DO create a boundary and end a segment. No attribute is
	/// ever included or excluded by NAME — whether a run's tokens are prose worth reporting
	/// is decided later by the value's shape and the checker, not by what the attribute is
	/// called. (This is the deliberate inversion of the old name-based allow/block lists.)
	///
	/// Chrome pruning (prose/skip classification of subtrees) is a separate, later stage.
	/// This stage intentionally does not prune; it produces the complete raw stream that
	/// downstream stages filter.
	/// </summary>
	public static class DomTraverser
	{
		/// <summary>
		/// HTML/ARIA attributes that are non-prose BY DEFINITION — their value type is, per spec, a
		/// CSS identifier, a URI, an IDREF (or IDREF list), an enumerated keyword, a token, a code,
		/// or a number — never natural-language text to spellcheck, on any document. The engine
		/// ignores these by default so the tool works with no configuration. An operator can REPLACE
		/// this set via SpellCheckEngineConfig.GlobalNonProseHtmlAttributesThatWillBeIgnored (e.g. for
		/// free-shape XML). Case-insensitive; matched by EXACT name.
		///
		/// EXCLUDES — deliberately kept CHECKABLE — every attribute that DOES carry author prose or is
		/// context-dependent: alt, title, placeholder, label, value, content, download, abbr,
		/// aria-label, aria-placeholder, aria-valuetext, aria-roledescription, aria-description,
		/// aria-braillelabel, aria-brailleroledescription, srcdoc. (meta "content" is governed
		/// separately by the meta-name allowlist below.) data-* is NOT covered here — custom
		/// attributes stay harvested (fail-loud); a site that emits non-prose data-* declares them
		/// via GlobalAttributesToIgnore.
		///
		/// Source: WHATWG HTML Living Standard attribute index (https://html.spec.whatwg.org/#attributes-3)
		/// and WAI-ARIA 1.2 states/properties (https://www.w3.org/TR/wai-aria-1.2/#state_prop_def),
		/// the content attributes whose value is non-prose by type, as of 2026-06. A future HTML/ARIA
		/// revision may add/remove entries — re-validate against the indexes above when updating, and
		/// keep the prose-bearing EXCLUDES above out of this set.
		/// </summary>
		public static readonly IReadOnlySet<string> DefaultNonProseAttributes =
			new HashSet<string>(new[]
			{
				// Structural identifiers
				"class", "id", "style",
				// URI / URI-list
				"href", "src", "srcset", "cite", "action", "formaction", "poster", "ping",
				"usemap", "manifest",
				// IDREF / IDREF-list (incl. ARIA relationships — space-separated id refs, NOT prose)
				"for", "form", "headers", "list", "itemref", "aria-activedescendant", "aria-controls",
				"aria-describedby", "aria-details", "aria-errormessage", "aria-flowto", "aria-labelledby",
				"aria-owns", "popovertarget",
				// Enumerated keywords / tokens
				"rel", "type", "target", "media", "method", "enctype", "formenctype", "formmethod",
				"formtarget", "scope", "role", "dir", "wrap", "autocomplete", "autocapitalize",
				"crossorigin", "decoding", "loading", "fetchpriority", "referrerpolicy", "sandbox",
				"inputmode", "kind", "preload", "shape", "as", "sizes", "translate", "spellcheck",
				"contenteditable", "draggable", "popover", "popovertargetaction", "accept",
				"accept-charset", "hreflang", "charset", "http-equiv", "property",
				// ARIA states / token-valued properties (true/false/tokens — never prose)
				"aria-atomic", "aria-autocomplete", "aria-busy", "aria-checked", "aria-current",
				"aria-disabled", "aria-dropeffect", "aria-expanded", "aria-grabbed", "aria-haspopup",
				"aria-hidden", "aria-invalid", "aria-live", "aria-modal", "aria-multiline",
				"aria-multiselectable", "aria-orientation", "aria-pressed", "aria-readonly",
				"aria-relevant", "aria-required", "aria-selected", "aria-sort",
				// Numeric (counts, spans, dimensions, integrity/nonce codes)
				"tabindex", "colspan", "rowspan", "span", "maxlength", "minlength", "size", "rows",
				"cols", "start", "min", "max", "step", "high", "low", "optimum", "width", "height",
				"integrity", "nonce", "aria-colcount", "aria-colindex", "aria-colspan", "aria-level",
				"aria-posinset", "aria-rowcount", "aria-rowindex", "aria-rowspan", "aria-setsize",
				"aria-valuemax", "aria-valuemin", "aria-valuenow",
				// Codes / machine identifiers
				"name", "itemprop", "itemtype", "itemid", "lang", "slot", "part", "is", "datetime",
				"accesskey", "coords", "dirname",
			}, StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// HTML boolean attributes. A boolean attribute's presence is the signal; its value is, per
		/// spec, only the empty string or the attribute name echoed (e.g. novalidate="novalidate",
		/// disabled="disabled") — never prose. So the value is always skipped. Kept SEPARATE from
		/// DefaultNonProseAttributes (different concern: presence-flags vs structural identifiers) and
		/// independently overridable via SpellCheckEngineConfig.GlobalBooleanHtmlAttributesThatWillBeIgnored.
		///
		/// Source: WHATWG HTML Living Standard attribute index (https://html.spec.whatwg.org/#attributes-3),
		/// the boolean-typed content attributes, as of 2026-06. Excludes obsolete/non-standard
		/// (scoped, seamless, typemustmatch, truespeed, frameborder, …) and the ENUMERATED-not-boolean
		/// attributes (contenteditable, draggable, spellcheck, translate, autocomplete). A future HTML
		/// revision may add/remove entries — re-validate against the index above when updating.
		/// Case-insensitive; matched by EXACT name (so "disabledtext" is untouched).
		/// </summary>
		public static readonly IReadOnlySet<string> DefaultBooleanAttributes =
			new HashSet<string>(new[]
			{
				"allowfullscreen", "async", "autofocus", "autoplay", "checked", "controls", "default",
				"defer", "disabled", "formnovalidate", "hidden", "inert", "ismap", "itemscope", "loop",
				"multiple", "muted", "nomodule", "novalidate", "open", "playsinline", "readonly",
				"required", "reversed", "selected",
			}, StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// The &lt;meta&gt; names whose content is human prose worth spellchecking: "description" and
		/// "keywords". Every other meta holds directives/tokens (viewport, robots, format-detection,
		/// http-equiv, og:*) and is ditched. This closed allowlist replaces the legacy approach of
		/// checking all meta and then subtracting noise via an exclusion denylist. Case-insensitive.
		/// </summary>
		public static readonly IReadOnlySet<string> DefaultMetaContentNames =
			new HashSet<string>(new[] { "description", "keywords" }, StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// The HTML event-handler content attributes ("on*"). Their values are JavaScript, not
		/// prose (e.g. onclick="return submit();") — but, like &lt;script&gt; bodies, they can hold
		/// prose string literals (onclick="alert('Bitte warten')"), so they are governed by the
		/// SpellCheckJavaScript switch rather than skipped unconditionally. EXACT names only (the
		/// spec's closed set) — never an "on" prefix match, so non-handler attributes like
		/// "ontology" or "once" are untouched. Case-insensitive.
		/// </summary>
		public static readonly IReadOnlySet<string> EventHandlerAttributes =
			new HashSet<string>(new[]
			{
				// Mouse / pointer
				"onclick", "ondblclick", "onmousedown", "onmouseup", "onmouseover", "onmousemove",
				"onmouseout", "onmouseenter", "onmouseleave", "oncontextmenu", "onwheel",
				"onpointerdown", "onpointerup", "onpointermove", "onpointerover", "onpointerout",
				"onpointerenter", "onpointerleave", "onpointercancel", "ongotpointercapture",
				"onlostpointercapture", "onauxclick",
				// Keyboard
				"onkeydown", "onkeyup", "onkeypress",
				// Form / input
				"onsubmit", "onreset", "onchange", "oninput", "oninvalid", "onsearch",
				"onfocus", "onblur", "onfocusin", "onfocusout", "onselect", "onbeforeinput",
				// Window / document / resource
				"onload", "onunload", "onbeforeunload", "onresize", "onscroll", "onerror",
				"onabort", "onhashchange", "onpopstate", "onpagehide", "onpageshow",
				"onreadystatechange", "onafterprint", "onbeforeprint", "onlanguagechange",
				"onmessage", "onmessageerror", "onoffline", "ononline", "onstorage",
				"onrejectionhandled", "onunhandledrejection",
				// Drag & drop
				"ondrag", "ondragend", "ondragenter", "ondragleave", "ondragover", "ondragstart",
				"ondrop",
				// Clipboard
				"oncopy", "oncut", "onpaste",
				// Media
				"oncanplay", "oncanplaythrough", "ondurationchange", "onemptied", "onended",
				"onloadeddata", "onloadedmetadata", "onloadstart", "onpause", "onplay",
				"onplaying", "onprogress", "onratechange", "onseeked", "onseeking", "onstalled",
				"onsuspend", "ontimeupdate", "onvolumechange", "onwaiting", "oncuechange",
				// Animation / transition
				"onanimationstart", "onanimationend", "onanimationiteration",
				"ontransitionstart", "ontransitionend", "ontransitionrun", "ontransitioncancel",
				// Touch
				"ontouchstart", "ontouchend", "ontouchmove", "ontouchcancel",
				// Misc / toggle
				"ontoggle", "onclose", "oncancel", "onslotchange", "onsecuritypolicyviolation",
			}, StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Inline phrasing elements that are TRANSPARENT for word assembly: their presence between
		/// two text fragments creates NO rendered word boundary, so the fragments are glued with no
		/// inserted space (e.g. "&lt;b&gt;I&lt;/b&gt;nternational" → "International"). This is the
		/// SAFE CORE only — pure formatting wrappers (and &lt;wbr&gt;, a soft-wrap hint) whose
		/// content is unambiguously part of the running word. Deliberately EXCLUDES elements whose
		/// gluing is ambiguous or wrong: &lt;sub&gt;/&lt;sup&gt; (footnote markers vs "H2O"),
		/// &lt;a&gt; (wraps whole words, rarely fractures), &lt;br&gt; (a break), and all block
		/// elements. Anything not in this set ends the current text segment (a boundary). The text
		/// content of an excluded element still surfaces — as its OWN segment — it just does not glue
		/// to its neighbours. Case-insensitive.
		/// </summary>
		public static readonly IReadOnlySet<string> InlinePhrasingGlue =
			new HashSet<string>(new[]
			{
				"b", "i", "em", "strong", "mark", "small", "u", "s", "span", "wbr",
			}, StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Block-level elements: an element that ends/starts a text segment (its text does not glue to
		/// text outside it). Used only to choose the OWNER node a reassembled text run is bound to —
		/// the nearest block ancestor, so the located excerpt shows the word in its block (paragraph,
		/// list item, cell, …) rather than the inline wrapper it happened to sit in. Membership
		/// MIRRORS the set in <see cref="ExcerptBuilder"/> so the owner the run is bound to and the
		/// block the excerpt is built from are the same element; deduplicating the two copies is a
		/// later cleanup. Case-insensitive.
		/// </summary>
		private static readonly HashSet<string> BlockElements =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"p", "h1", "h2", "h3", "h4", "h5", "h6",
				"li", "td", "th", "dt", "dd", "figcaption",
				"blockquote", "div", "section", "article",
				"header", "footer", "aside", "main", "nav",
			};

		/// <summary>
		/// True if <paramref name="node"/> is inside (or is) an element whose content must not be
		/// spell-checked: a &lt;style&gt; (always) or &lt;script&gt; (unless checkJavaScript) ancestor,
		/// or any ancestor whose tag is in <paramref name="tagsToIgnore"/> (config GlobalHtmlTagsToIgnore
		/// — svg/math/object/embed/input/select/…). Walks the full ancestor chain so deep subtree
		/// content (e.g. text within &lt;svg&gt;&lt;g&gt;&lt;text&gt;) is caught. Case-insensitive.
		/// </summary>
		private static bool IsNonProseElementContent(HtmlNode? node, bool checkJavaScript, IReadOnlySet<string>? tagsToIgnore)
		{
			for (HtmlNode? current = node; current != null && current.NodeType != HtmlNodeType.Document; current = current.ParentNode)
			{
				if (current.NodeType != HtmlNodeType.Element)
				{
					continue;
				}

				if (current.Name.Equals("style", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				if (!checkJavaScript && current.Name.Equals("script", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				if (tagsToIgnore != null && tagsToIgnore.Contains(current.Name))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Decode raw bytes and parse into a DOM — one parse, the source of truth.</summary>
		public static HtmlDocument Parse(byte[] rawBytes)
		{
			Encoding encoding = DetectEncoding.FromBytes(rawBytes);
			string html = encoding.GetString(rawBytes);
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		/// <summary>
		/// Walk the DOM in document order, yielding a <see cref="TextRun"/> for every block-level
		/// text segment (inline phrasing children glued in place) and every non-empty attribute value,
		/// each bound to its node.
		/// </summary>
		public static IEnumerable<TextRun> Traverse(HtmlDocument doc, IReadOnlySet<string>? skipAttributeNames = null, bool checkJavaScript = false, IReadOnlySet<string>? metaContentNames = null, BoilerplateMatcher? globalIgnore = null, IReadOnlySet<string>? htmlTagsToIgnore = null)
			=> Harvest(doc, pageMatcher: null, skipAttributeNames, checkJavaScript, metaContentNames, globalIgnore, htmlTagsToIgnore);

		/// <summary>
		/// Walk the DOM yielding runs, but PRUNE declared-boilerplate subtrees when this is not an
		/// entry page (§4). On an entry page, or when no selectors are declared, this yields exactly
		/// what the parameterless <see cref="Traverse(HtmlDocument, IReadOnlySet{string}, bool, IReadOnlySet{string}, BoilerplateMatcher, IReadOnlySet{string})"/>
		/// does — boilerplate suppression is opt-in and per-page. A node inside a boilerplate subtree
		/// is skipped along with its attributes; everything else is checked (the fail-loud default:
		/// undeclared content is always checked, never silently dropped).
		/// </summary>
		public static IEnumerable<TextRun> Traverse(HtmlDocument doc, BoilerplateMatcher? matcher, bool isEntryPage, IReadOnlySet<string>? skipAttributeNames = null, bool checkJavaScript = false, IReadOnlySet<string>? metaContentNames = null, BoilerplateMatcher? globalIgnore = null, IReadOnlySet<string>? htmlTagsToIgnore = null)
		{
			// Entry page, no matcher, or no selectors → page-boilerplate pruning is off (the global
			// technical-ignore still applies — it is orthogonal to boilerplate). Otherwise the page
			// matcher prunes declared boilerplate subtrees.
			BoilerplateMatcher? pageMatcher = (isEntryPage || matcher == null || matcher.IsEmpty) ? null : matcher;
			return Harvest(doc, pageMatcher, skipAttributeNames, checkJavaScript, metaContentNames, globalIgnore, htmlTagsToIgnore);
		}

		/// <summary>
		/// The single harvest implementation shared by both public overloads. <paramref name="pageMatcher"/>
		/// is the per-page boilerplate matcher (null = no page-boilerplate pruning); <paramref name="globalIgnore"/>
		/// is the always-on technical-ignore (tracking pixels etc.). Text segments are assembled across
		/// inline phrasing children and bound to their nearest block ancestor; attribute values are
		/// emitted per element, unchanged.
		/// </summary>
		private static IEnumerable<TextRun> Harvest(HtmlDocument doc, BoilerplateMatcher? pageMatcher, IReadOnlySet<string>? skipAttributeNames, bool checkJavaScript, IReadOnlySet<string>? metaContentNames, BoilerplateMatcher? globalIgnore, IReadOnlySet<string>? htmlTagsToIgnore)
		{
			IReadOnlySet<string> metaAllow = metaContentNames ?? DefaultMetaContentNames;

			var buffer = new StringBuilder();
			HtmlNode? ownerNode = null;     // node the buffered run is bound to (nearest block ancestor)
			string? ownerPath = null;       // SourcePath for the buffered run, e.g. "p[#text]"
			HtmlNode? lastTextNode = null;   // last real text node appended (for the gluability test)

			foreach (var node in doc.DocumentNode.Descendants())
			{
				if (node.NodeType == HtmlNodeType.Text)
				{
					string raw = node.InnerText;

					// Whitespace-only text node: carries no words; its only role is to separate
					// inline-wrapped words (e.g. "<b>a</b> <b>b</b>"). Append a single separating
					// space when buffered content precedes it. No prune/boilerplate check is run
					// here — a stray space inherited from a pruned subtree is harmless (the
					// canonicalizer collapses it, the tokenizer ignores it), and skipping the check
					// avoids invoking the boilerplate matcher on the DOM's very many whitespace nodes.
					if (string.IsNullOrWhiteSpace(raw))
					{
						if (buffer.Length > 0)
						{
							buffer.Append(' ');
							lastTextNode = node;
						}

						continue;
					}

					// Pruned non-whitespace text (script/style/ignored-tag/boilerplate subtree) — its
					// content is not prose. End the current segment (do not glue across removed content)
					// and skip the text itself.
					if (IsTextPruned(node, checkJavaScript, htmlTagsToIgnore, globalIgnore, pageMatcher))
					{
						if (buffer.Length > 0)
						{
							yield return new TextRun(ownerNode!, RunSource.TextNode, ownerPath!, buffer.ToString());
							buffer.Clear();
							ownerNode = null;
							ownerPath = null;
							lastTextNode = null;
						}

						continue;
					}

					// Inline <script> JavaScript, only when script checking is enabled and the node
					// survived pruning (so an ignored/boilerplate script is still skipped above):
					// lift its string literals and emit each as its own Script run — decoded, and
					// located by line:column within the script body. A script is a hard prose
					// boundary, so flush any pending segment first. The literals are NOT filtered
					// here; RunChecker gates each via ClassifyScriptLiteral, exactly as attribute
					// runs are gated there.
					if (checkJavaScript
						&& node.ParentNode != null
						&& node.ParentNode.Name.Equals("script", StringComparison.OrdinalIgnoreCase))
					{
						if (buffer.Length > 0)
						{
							yield return new TextRun(ownerNode!, RunSource.TextNode, ownerPath!, buffer.ToString());
							buffer.Clear();
							ownerNode = null;
							ownerPath = null;
							lastTextNode = null;
						}

						HtmlNode scriptElement = node.ParentNode;
						foreach (var literal in JsStringLiteralExtractor.Extract(raw))
						{
							yield return new TextRun(
								scriptElement,
								RunSource.Script,
								ScriptLiteralPath(raw, literal.RawStart),
								literal.Text)
							{
								ScriptContext = ScriptContextWindow(raw, literal.RawStart, literal.RawLength),
							};
						}

						continue;
					}

					// Real prose text. Glue to the buffer only if the buffered text and this node are
					// separated by inline phrasing alone (no block/br/<a>/… on the path between them).
					bool glue = buffer.Length > 0 && lastTextNode != null && Gluable(lastTextNode, node);
					if (buffer.Length > 0 && !glue)
					{
						yield return new TextRun(ownerNode!, RunSource.TextNode, ownerPath!, buffer.ToString());
						buffer.Clear();
						ownerNode = null;
						ownerPath = null;
					}

					buffer.Append(raw);
					if (ownerNode == null)
					{
						(ownerNode, string ownerName) = OwnerFor(node);
						ownerPath = $"{ownerName}[#text]";
					}

					lastTextNode = node;
					continue;
				}

				if (node.NodeType != HtmlNodeType.Element)
				{
					// Comments and other invisible nodes create no word boundary — leave the buffer.
					continue;
				}

				// A non-phrasing element (block, <br>, <a>, <sub>, <sup>, …) ends the current segment.
				// Flush BEFORE emitting this element's attributes so run order follows document order.
				if (!InlinePhrasingGlue.Contains(node.Name) && buffer.Length > 0)
				{
					yield return new TextRun(ownerNode!, RunSource.TextNode, ownerPath!, buffer.ToString());
					buffer.Clear();
					ownerNode = null;
					ownerPath = null;
					lastTextNode = null;
				}

				if (!node.HasAttributes)
				{
					continue;
				}

				// Element-level prune: global technical-ignore, ignored element types (and ancestors),
				// or a declared boilerplate subtree — skip the element's attributes too.
				if ((globalIgnore != null && globalIgnore.IsBoilerplate(node))
					|| IsNonProseElementContent(node, checkJavaScript, htmlTagsToIgnore)
					|| (pageMatcher != null && pageMatcher.IsBoilerplate(node)))
				{
					continue;
				}

				bool isMeta = node.Name.Equals("meta", StringComparison.OrdinalIgnoreCase);

				foreach (var attribute in node.Attributes)
				{
					string value = attribute.Value;
					if (string.IsNullOrWhiteSpace(value))
					{
						continue;
					}

					// Operator-declared technical attributes (e.g. salts) — value is never prose.
					if (skipAttributeNames != null && skipAttributeNames.Contains(attribute.Name))
					{
						continue;
					}

					// Inline event handlers (on*) are JavaScript — governed by the JS switch. When
					// script checking is OFF they are skipped wholesale (as before); when ON, the
					// handler is treated as JS: its string literals are lifted and emitted as Script
					// runs (decoded, located at the handler attribute), gated downstream by
					// ClassifyScriptLiteral exactly like a <script> body. So handler CODE — calls,
					// keywords, identifiers like return/this/fooBar — is discarded by the lexer and
					// only quoted prose is checked; the raw handler value is never tokenized.
					if (EventHandlerAttributes.Contains(attribute.Name))
					{
						if (!checkJavaScript)
						{
							continue;
						}

						foreach (var literal in JsStringLiteralExtractor.Extract(value))
						{
							yield return new TextRun(node, RunSource.Script, $"{node.Name}[@{attribute.Name}]", literal.Text)
							{
								ScriptContext = ScriptContextWindow(value, literal.RawStart, literal.RawLength),
							};
						}

						continue;
					}

					if (isMeta)
					{
						// On a <meta>, the ONLY prose-bearing attribute is the content of an allowlisted
						// name (default: description, keywords). Every other meta attribute — http-equiv,
						// charset, property, scheme, and the name value itself — is technical, never prose,
						// so it is skipped (completes the meta policy: ditch all meta except allowlisted
						// content). This is what stops http-equiv="content-type" leaking as a word.
						if (!attribute.Name.Equals("content", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						string metaName = node.GetAttributeValue("name", string.Empty).Trim();
						if (metaName.Length == 0 || !metaAllow.Contains(metaName))
						{
							continue;
						}

						yield return new TextRun(node, RunSource.Meta, $"meta[@name={metaName.ToLowerInvariant()}]", value);
					}
					else
					{
						yield return new TextRun(node, RunSource.Attribute, $"{node.Name}[@{attribute.Name}]", value);
					}
				}
			}

			// Flush the final segment.
			if (buffer.Length > 0)
			{
				yield return new TextRun(ownerNode!, RunSource.TextNode, ownerPath!, buffer.ToString());
			}
		}

		/// <summary>
		/// True if this text node's content must not be harvested as prose: inside a style/script/
		/// ignored-tag subtree, inside the always-on technical-ignore, or inside a declared page
		/// boilerplate subtree. Mirrors the per-overload predicates the old code applied inline.
		/// </summary>
		private static bool IsTextPruned(HtmlNode node, bool checkJavaScript, IReadOnlySet<string>? htmlTagsToIgnore, BoilerplateMatcher? globalIgnore, BoilerplateMatcher? pageMatcher)
		{
			if (IsNonProseElementContent(node.ParentNode, checkJavaScript, htmlTagsToIgnore))
			{
				return true;
			}

			if (globalIgnore != null && node.ParentNode != null && globalIgnore.IsBoilerplate(node.ParentNode))
			{
				return true;
			}

			if (pageMatcher != null && pageMatcher.IsBoilerplate(node))
			{
				return true;
			}

			return false;
		}

		/// <summary>
		/// The owner node + tag name a reassembled text run is bound to: the nearest block-level
		/// ancestor of <paramref name="textNode"/> (so the excerpt is built from the block the word
		/// lives in). When the text has no block ancestor (e.g. a &lt;title&gt; or a stray text node),
		/// the immediate parent owns it — preserving the previous "{parent}[#text]" label for those.
		/// </summary>
		private static (HtmlNode Node, string Name) OwnerFor(HtmlNode textNode)
		{
			for (HtmlNode? n = textNode.ParentNode; n != null && n.NodeType == HtmlNodeType.Element; n = n.ParentNode)
			{
				if (BlockElements.Contains(n.Name))
				{
					return (n, n.Name);
				}
			}

			HtmlNode? parent = textNode.ParentNode;
			return parent != null ? (parent, parent.Name) : (textNode, "?");
		}

		/// <summary>
		/// SourcePath label for a script literal: <c>script[L&lt;line&gt;:&lt;col&gt;]</c>, with 1-based
		/// line and column measured WITHIN the script body (newlines counted up to the literal's raw
		/// start offset). Self-contained — it does not depend on the parser's node line/column, so it
		/// is correct regardless of how the &lt;script&gt; sits in the document.
		/// </summary>
		// Maximum characters of raw source shown on each side of the literal in the triage context.
		private const int ScriptContextRadius = 80;

		/// <summary>
		/// Builds the raw-source context window carried on a Script run for triage display: the slice
		/// of <paramref name="body"/> around the literal at [<paramref name="rawStart"/>,
		/// rawStart+<paramref name="rawLength"/>), extended up to <see cref="ScriptContextRadius"/>
		/// characters each side BUT never across a newline — so the excerpt stays on the literal's own
		/// source line. An ellipsis is added only where the radius (not a line boundary) did the
		/// cutting. Internal whitespace (indentation, tabs) is collapsed to single spaces and the
		/// result trimmed, so a deeply-indented line reads cleanly on one row. The window is centred on
		/// the literal's KNOWN raw offset, never on a search for the decoded word, so it is correct even
		/// for escaped literals and always shows the source as written.
		/// </summary>
		private static string ScriptContextWindow(string body, int rawStart, int rawLength)
		{
			if (string.IsNullOrEmpty(body))
			{
				return string.Empty;
			}

			int len = body.Length;
			int start = rawStart < 0 ? 0 : (rawStart > len ? len : rawStart);
			int end = start + rawLength;
			if (end > len)
			{
				end = len;
			}

			// Left: up to radius chars, stopping at a newline if one is closer.
			int leftLimit = start - ScriptContextRadius;
			if (leftLimit < 0)
			{
				leftLimit = 0;
			}

			int left = leftLimit;
			for (int k = start - 1; k >= leftLimit; k--)
			{
				if (body[k] == '\n')
				{
					left = k + 1;
					break;
				}
			}

			bool leadingEllipsis = left > 0 && body[left - 1] != '\n';

			// Right: up to radius chars, stopping at a newline if one is closer.
			int rightLimit = end + ScriptContextRadius;
			if (rightLimit > len)
			{
				rightLimit = len;
			}

			int right = rightLimit;
			for (int k = end; k < rightLimit; k++)
			{
				if (body[k] == '\n')
				{
					right = k;
					break;
				}
			}

			bool trailingEllipsis = right < len && body[right] != '\n';

			string slice = CollapseWhitespace(body.Substring(left, right - left)).Trim();
			if (leadingEllipsis)
			{
				slice = "…" + slice;
			}

			if (trailingEllipsis)
			{
				slice += "…";
			}

			return slice;
		}

		// Collapse every run of whitespace (spaces, tabs — newlines never reach here) to a single space,
		// so indented source reads as one clean line in the triage view.
		private static string CollapseWhitespace(string s)
		{
			var sb = new System.Text.StringBuilder(s.Length);
			bool inSpace = false;
			foreach (char c in s)
			{
				if (char.IsWhiteSpace(c))
				{
					inSpace = true;
					continue;
				}

				if (inSpace && sb.Length > 0)
				{
					sb.Append(' ');
				}

				inSpace = false;
				sb.Append(c);
			}

			return sb.ToString();
		}

		private static string ScriptLiteralPath(string body, int rawStart)
		{
			int line = 1;
			int lineStart = 0;
			int upTo = rawStart < body.Length ? rawStart : body.Length;
			for (int k = 0; k < upTo; k++)
			{
				if (body[k] == '\n')
				{
					line++;
					lineStart = k + 1;
				}
			}

			int col = upTo - lineStart + 1;
			return $"script[L{line}:{col}]";
		}

		/// <summary>
		/// True if two text nodes may be glued into one word with no inserted boundary: every element
		/// on the path from each node up to (but excluding) their lowest common ancestor is an inline
		/// phrasing element. A block element, &lt;br&gt;, &lt;a&gt;, &lt;sub&gt;, &lt;sup&gt; — anything
		/// not in <see cref="InlinePhrasingGlue"/> — on either path means a rendered boundary, so the
		/// nodes belong to different segments. This catches the case the document-order flush misses:
		/// EXITING an excluded element ("…&lt;/a&gt;tail" or text after a nested block) where no new
		/// element is entered between the two text nodes.
		/// </summary>
		private static bool Gluable(HtmlNode a, HtmlNode b)
		{
			HtmlNode? lca = LowestCommonAncestor(a, b);
			if (lca == null)
			{
				return false;
			}

			for (HtmlNode? n = a.ParentNode; n != null && n != lca; n = n.ParentNode)
			{
				if (n.NodeType == HtmlNodeType.Element && !InlinePhrasingGlue.Contains(n.Name))
				{
					return false;
				}
			}

			for (HtmlNode? n = b.ParentNode; n != null && n != lca; n = n.ParentNode)
			{
				if (n.NodeType == HtmlNodeType.Element && !InlinePhrasingGlue.Contains(n.Name))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Lowest common ancestor of two nodes by reference identity, or null if unrelated.</summary>
		private static HtmlNode? LowestCommonAncestor(HtmlNode a, HtmlNode b)
		{
			var ancestors = new HashSet<HtmlNode>();
			for (HtmlNode? n = a; n != null; n = n.ParentNode)
			{
				ancestors.Add(n);
			}

			for (HtmlNode? n = b; n != null; n = n.ParentNode)
			{
				if (ancestors.Contains(n))
				{
					return n;
				}
			}

			return null;
		}

		/// <summary>Convenience: raw bytes straight to a run stream.</summary>
		public static IEnumerable<TextRun> RunsFromBytes(byte[] rawBytes) => Traverse(Parse(rawBytes));
	}
}
