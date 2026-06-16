using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Spell.RemoveTagsAndAttributes. The function is pure with respect
	/// to its inputs — no filesystem or logger — so tests construct minimal
	/// HtmlDocument fixtures inline.
	///
	/// Coverage targets the behavioural contract:
	///   - configured tags are removed (with their subtree)
	///   - configured attributes are removed from every element
	///   - the "name" attribute on <meta> is preserved (a deliberate exception)
	///   - "name" on non-meta elements is NOT preserved
	///   - matching is case-insensitive for both tag and attribute names
	///   - empty configuration lists are no-ops
	///   - the function returns the same document instance it was given
	/// </summary>
	public class SpellRemoveTagsAndAttributesTests
	{
		private static HtmlDocument LoadHtml(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		// ── Tag removal ──────────────────────────────────────────────────────

		[Fact]
		public void RemoveTagsAndAttributes_RemovesConfiguredTag_AndItsSubtree()
		{
			var doc = LoadHtml(
				"<html><body><p>keep</p><script>var x = 1;</script><p>also keep</p></body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: ["script"],
				attributesToRemoveBeforeSpellCheck: []);

			Assert.Null(doc.DocumentNode.SelectSingleNode("//script"));
			Assert.Equal(2, doc.DocumentNode.SelectNodes("//p").Count);
		}

		[Fact]
		public void RemoveTagsAndAttributes_RemovesMultipleTagTypes()
		{
			var doc = LoadHtml(
				"<html><body><p>keep</p><script>js</script><style>css</style><p>keep</p></body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: ["script", "style"],
				attributesToRemoveBeforeSpellCheck: []);

			Assert.Null(doc.DocumentNode.SelectSingleNode("//script"));
			Assert.Null(doc.DocumentNode.SelectSingleNode("//style"));
			Assert.Equal(2, doc.DocumentNode.SelectNodes("//p").Count);
		}

		[Fact]
		public void RemoveTagsAndAttributes_TagNotPresent_NoEffect()
		{
			var doc = LoadHtml("<html><body><p>only text</p></body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: ["script"],
				attributesToRemoveBeforeSpellCheck: []);

			Assert.NotNull(doc.DocumentNode.SelectSingleNode("//p"));
		}

		// ── Attribute removal ────────────────────────────────────────────────

		[Fact]
		public void RemoveTagsAndAttributes_RemovesConfiguredAttribute_FromAllElements()
		{
			var doc = LoadHtml(
				"<html><body>" +
				"<div class=\"a\">one</div>" +
				"<span class=\"b\">two</span>" +
				"<p class=\"c\">three</p>" +
				"</body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["class"]);

			foreach (var node in doc.DocumentNode.Descendants())
			{
				Assert.False(node.Attributes.Contains("class"),
					$"<{node.Name}> still has class attribute");
			}
		}

		[Fact]
		public void RemoveTagsAndAttributes_RemovesMultipleConfiguredAttributes_InOnePass()
		{
			var doc = LoadHtml(
				"<html><body>" +
				"<div class=\"a\" data-id=\"1\" title=\"keep\">x</div>" +
				"</body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["class", "data-id"]);

			var div = doc.DocumentNode.SelectSingleNode("//div");
			Assert.False(div.Attributes.Contains("class"));
			Assert.False(div.Attributes.Contains("data-id"));
			Assert.True(div.Attributes.Contains("title"), "title should be retained");
			Assert.Equal("keep", div.GetAttributeValue("title", ""));
		}

		[Fact]
		public void RemoveTagsAndAttributes_AttributeMatchIsCaseInsensitive()
		{
			// HAP normalises attribute names to lower-case on load, so 'CLASS' in the
			// source becomes 'class'. The function must match regardless of the casing
			// the caller provides in the configured list.
			var doc = LoadHtml("<html><body><div CLASS=\"a\">x</div></body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["CLASS"]);

			Assert.False(doc.DocumentNode.SelectSingleNode("//div").Attributes.Contains("class"));
		}

		[Fact]
		public void RemoveTagsAndAttributes_AttributeNotPresentOnAnyNode_NoEffect()
		{
			var doc = LoadHtml(
				"<html><body><div title=\"keep\">x</div></body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["data-missing"]);

			Assert.True(doc.DocumentNode.SelectSingleNode("//div").Attributes.Contains("title"));
		}

		// ── The <meta name="..."> exception ──────────────────────────────────

		[Fact]
		public void RemoveTagsAndAttributes_PreservesNameAttribute_OnMetaElement()
		{
			// Critical exception: ExtractTextForSpellCheck reads meta[@name] to decide
			// whether to spell-check the meta's content. Stripping it breaks that path.
			var doc = LoadHtml(
				"<html><head>" +
				"<meta name=\"description\" content=\"Some text to check\"/>" +
				"</head></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["name"]);

			var meta = doc.DocumentNode.SelectSingleNode("//meta");
			Assert.True(meta.Attributes.Contains("name"));
			Assert.Equal("description", meta.GetAttributeValue("name", ""));
		}

		[Fact]
		public void RemoveTagsAndAttributes_RemovesNameAttribute_OnNonMetaElements()
		{
			// The exception is scoped to <meta> only. <a name="anchor"> et al. still
			// lose the attribute.
			var doc = LoadHtml(
				"<html><body>" +
				"<a name=\"anchor\">link</a>" +
				"<input name=\"field\" />" +
				"</body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["name"]);

			Assert.False(doc.DocumentNode.SelectSingleNode("//a").Attributes.Contains("name"));
			Assert.False(doc.DocumentNode.SelectSingleNode("//input").Attributes.Contains("name"));
		}

		[Fact]
		public void RemoveTagsAndAttributes_MetaExceptionIsCaseInsensitive_OnBothAttributeAndElement()
		{
			// HAP lower-cases by default, but be defensive: the function must work
			// even if a caller passes "Name" or somehow the element name is "META".
			var doc = LoadHtml(
				"<html><head>" +
				"<meta name=\"keywords\" content=\"a, b, c\"/>" +
				"</head></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["Name"]);

			Assert.True(doc.DocumentNode.SelectSingleNode("//meta").Attributes.Contains("name"));
		}

		// ── Empty configurations ────────────────────────────────────────────

		[Fact]
		public void RemoveTagsAndAttributes_BothListsEmpty_NoChanges()
		{
			var html = "<html><body><p class=\"x\" id=\"y\">text</p><script>js</script></body></html>";
			var doc = LoadHtml(html);

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: []);

			Assert.NotNull(doc.DocumentNode.SelectSingleNode("//script"));
			var p = doc.DocumentNode.SelectSingleNode("//p");
			Assert.True(p.Attributes.Contains("class"));
			Assert.True(p.Attributes.Contains("id"));
		}

		// ── Return value & identity ─────────────────────────────────────────

		[Fact]
		public void RemoveTagsAndAttributes_ReturnsSameDocumentInstance()
		{
			// Callers chain operations on the returned document; the function must
			// mutate-and-return rather than rebuilding a new HtmlDocument.
			var doc = LoadHtml("<html><body><p>x</p></body></html>");

			var result = Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: []);

			Assert.Same(doc, result);
		}

		// ── Stress / shape ──────────────────────────────────────────────────

		[Fact]
		public void RemoveTagsAndAttributes_DocumentWithManyAttributesAcrossManyNodes()
		{
			// Builds a small but non-trivial tree where many nodes have multiple
			// attributes from the configured list. Exercises the "collect-then-remove"
			// inner pattern (more than one attribute removed per node) and ensures
			// the meta exception still fires correctly amid the noise.
			var doc = LoadHtml(
				"<html><head>" +
				"<meta name=\"description\" content=\"desc\"/>" +
				"<meta name=\"robots\" content=\"index\"/>" +
				"</head><body>" +
				"<div class=\"a\" data-id=\"1\" style=\"x\">one</div>" +
				"<div class=\"b\" data-id=\"2\" style=\"y\">two</div>" +
				"<a name=\"jump\" class=\"c\" href=\"/x\">link</a>" +
				"</body></html>");

			Spell.RemoveTagsAndAttributes(
				doc,
				tagsToRemoveBeforeSpellCheck: [],
				attributesToRemoveBeforeSpellCheck: ["class", "data-id", "style", "name"]);

			// All meta name attributes preserved.
			foreach (var meta in doc.DocumentNode.SelectNodes("//meta"))
			{
				Assert.True(meta.Attributes.Contains("name"), $"meta with content '{meta.GetAttributeValue("content", "")}' lost its name");
			}

			// All divs stripped of class/data-id/style.
			foreach (var div in doc.DocumentNode.SelectNodes("//div"))
			{
				Assert.False(div.Attributes.Contains("class"));
				Assert.False(div.Attributes.Contains("data-id"));
				Assert.False(div.Attributes.Contains("style"));
			}

			// <a> stripped of class AND name (no meta-exception applies).
			var a = doc.DocumentNode.SelectSingleNode("//a");
			Assert.False(a.Attributes.Contains("class"));
			Assert.False(a.Attributes.Contains("name"));
			Assert.True(a.Attributes.Contains("href"), "href is not in the config list and must be preserved");
		}
	}
}
