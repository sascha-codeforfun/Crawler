using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ConfigDeltaWriter — the config.private.delta producer
	/// introduced in fileset #317.
	///
	/// Three layers of coverage:
	///   1. Pass 1 structural diff — JsonNode comparison (ComputeObjectDiff,
	///      JsonNodesEqual). Pure data shapes, no file I/O.
	///   2. Pass 3 comment injection (InjectAdjacentComments) — adjacent //
	///      comments from the source travel with surviving entries, with
	///      strict-adjacency rule (blank or non-// stops the walk).
	///   3. End-to-end Write() — file I/O, no-op-when-no-private, the
	///      resulting file content has the expected shape.
	/// </summary>
	[Collection("Logger")]
	public class ConfigDeltaWriterTests : IDisposable
	{
		private readonly string _tempDir;

		public ConfigDeltaWriterTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"cfg-delta-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Pass 1: structural diff ────────────────────────────────────────

		[Fact]
		public void ComputeObjectDiff_IdenticalObjects_ReturnsEmpty()
		{
			var baseObj = (JsonObject)JsonNode.Parse("""{ "A": 1, "B": "two" }""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""{ "A": 1, "B": "two" }""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);
			Assert.Empty(diff);
		}

		[Fact]
		public void ComputeObjectDiff_ScalarDifference_EmitsOnlyDiff()
		{
			var baseObj = (JsonObject)JsonNode.Parse("""{ "A": 1, "B": "two" }""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""{ "A": 99, "B": "two" }""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);
			Assert.Single(diff);
			Assert.Equal(99, diff["A"]!.GetValue<int>());
		}

		[Fact]
		public void ComputeObjectDiff_KeyOnlyInPrivate_AlwaysEmitted()
		{
			// Override for a field baseline doesn't declare → always emit.
			var baseObj = (JsonObject)JsonNode.Parse("""{ "A": 1 }""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""{ "A": 1, "PrivateOnly": "x" }""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);
			Assert.Single(diff);
			Assert.Equal("x", diff["PrivateOnly"]!.GetValue<string>());
		}

		[Fact]
		public void ComputeObjectDiff_NestedObject_RecursesAndEmitsOnlyDifferingFields()
		{
			// A nested object with one differing sub-field. The diff should
			// contain the parent object holding only the differing sub-field,
			// not the whole baseline-matching siblings.
			var baseObj = (JsonObject)JsonNode.Parse("""
				{
					"Outer": { "A": 1, "B": "same", "C": true }
				}
				""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""
				{
					"Outer": { "A": 99, "B": "same", "C": true }
				}
				""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);

			Assert.Single(diff);
			var outer = diff["Outer"] as JsonObject;
			Assert.NotNull(outer);
			Assert.Single(outer!);
			Assert.Equal(99, outer["A"]!.GetValue<int>());
		}

		[Fact]
		public void ComputeObjectDiff_NestedObject_NoSubDifference_OmitsParent()
		{
			// Nested object where all sub-fields match → parent omitted entirely.
			var baseObj = (JsonObject)JsonNode.Parse("""
				{
					"Outer": { "A": 1, "B": "same" },
					"TopLevel": "diff-here"
				}
				""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""
				{
					"Outer": { "A": 1, "B": "same" },
					"TopLevel": "different"
				}
				""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);

			Assert.Single(diff);
			Assert.True(diff.ContainsKey("TopLevel"));
			Assert.False(diff.ContainsKey("Outer"));
		}

		[Fact]
		public void ComputeObjectDiff_ListPositionalEquality_SameContentSameOrder_OmitsList()
		{
			// Strict positional equality on parsed values. Same elements in
			// same positions → equal regardless of source-text whitespace.
			var baseObj = (JsonObject)JsonNode.Parse("""{ "L": ["p","h1","h2"] }""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""{ "L": [ "p", "h1", "h2" ] }""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);
			Assert.Empty(diff);
		}

		[Fact]
		public void ComputeObjectDiff_ListPositionalEquality_SameContentDifferentOrder_EmitsList()
		{
			// Same elements in different positions → NOT equal. Operator's
			// chosen order IS the override per #317's Q4 decision.
			var baseObj = (JsonObject)JsonNode.Parse("""{ "L": ["p","h1","h2"] }""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""{ "L": ["h1","p","h2"] }""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);
			Assert.Single(diff);
		}

		[Fact]
		public void ComputeObjectDiff_ListsOfDifferentLength_EmitsList()
		{
			var baseObj = (JsonObject)JsonNode.Parse("""{ "L": ["a","b"] }""")!;
			var privateObj = (JsonObject)JsonNode.Parse("""{ "L": ["a","b","c"] }""")!;
			var diff = ConfigDeltaWriter.ComputeObjectDiff(baseObj, privateObj);
			Assert.Single(diff);
		}

		[Fact]
		public void JsonNodesEqual_BothNull_True()
		{
			Assert.True(ConfigDeltaWriter.JsonNodesEqual(null, null));
		}

		[Fact]
		public void JsonNodesEqual_OneNull_False()
		{
			var node = JsonNode.Parse("1");
			Assert.False(ConfigDeltaWriter.JsonNodesEqual(node, null));
			Assert.False(ConfigDeltaWriter.JsonNodesEqual(null, node));
		}

		[Fact]
		public void JsonNodesEqual_ListsWithWhitespaceDifferences_True()
		{
			// Pure whitespace difference in source text — parsed values equal.
			var a = JsonNode.Parse("""["p","h1"]""");
			var b = JsonNode.Parse("""[ "p" , "h1" ]""");
			Assert.True(ConfigDeltaWriter.JsonNodesEqual(a, b));
		}

		// ── Pass 3: comment injection ───────────────────────────────────────

		[Fact]
		public void InjectAdjacentComments_CommentAboveTopLevelProperty_Preserved()
		{
			var source = new List<string>
			{
				"{",
				"\t// Comment above Url",
				"\t\"Url\": \"https://example.com\",",
				"\t\"BaseDirectory\": \"D:\\\\X\"",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Url\": \"https://example.com\"",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.Contains("// Comment above Url", result);
			// The comment must appear ABOVE the Url line.
			var urlIdx = result.IndexOf("\"Url\"", StringComparison.Ordinal);
			var commentIdx = result.IndexOf("// Comment above Url", StringComparison.Ordinal);
			Assert.True(commentIdx < urlIdx, "Comment must appear above the property it annotates.");
		}

		[Fact]
		public void InjectAdjacentComments_BlankLineBetweenCommentAndProperty_CommentDropped()
		{
			// Strict adjacency: blank line stops the upward walk. The comment
			// describes something different, not the Url field below the blank.
			var source = new List<string>
			{
				"{",
				"\t// This comment is not adjacent to Url",
				"",
				"\t\"Url\": \"https://example.com\"",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Url\": \"https://example.com\"",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.DoesNotContain("not adjacent", result);
		}

		[Fact]
		public void InjectAdjacentComments_MultipleConsecutiveCommentLines_AllPreserved()
		{
			var source = new List<string>
			{
				"{",
				"\t// Line one of the comment block",
				"\t// Line two of the comment block",
				"\t// Line three of the comment block",
				"\t\"Url\": \"https://example.com\"",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Url\": \"https://example.com\"",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.Contains("Line one", result);
			Assert.Contains("Line two", result);
			Assert.Contains("Line three", result);
			// Order preserved: line one before line two before line three before Url.
			var idx1 = result.IndexOf("Line one", StringComparison.Ordinal);
			var idx2 = result.IndexOf("Line two", StringComparison.Ordinal);
			var idx3 = result.IndexOf("Line three", StringComparison.Ordinal);
			var idxUrl = result.IndexOf("\"Url\"", StringComparison.Ordinal);
			Assert.True(idx1 < idx2);
			Assert.True(idx2 < idx3);
			Assert.True(idx3 < idxUrl);
		}

		[Fact]
		public void InjectAdjacentComments_NonCommentLineStopsWalk()
		{
			// "Other": ... is a non-comment, non-blank line. It stops the walk.
			// The far-above comment doesn't attach to Url.
			var source = new List<string>
			{
				"{",
				"\t// This comment is for Other, not Url",
				"\t\"Other\": 1,",
				"\t\"Url\": \"https://example.com\"",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Url\": \"https://example.com\"",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.DoesNotContain("This comment is for Other", result);
		}

		[Fact]
		public void InjectAdjacentComments_NoMatchingSourceLine_NoComments()
		{
			// The emitted output has a property that doesn't appear in source
			// (shouldn't really happen, but defensive: no crash, no comments).
			var source = new List<string>
			{
				"{",
				"\t\"OtherField\": 1",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Url\": \"https://example.com\"",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.Contains("\"Url\"", result);
			// Just no crash + no spurious comments.
		}

		[Fact]
		public void InjectAdjacentComments_HomogeneousListEntries_CommentsTrackUniquely()
		{
			// A list of objects where each has a "Name" field at the same
			// depth, each with its own comment. Without the consumed-line
			// tracking, every emitted "Name" would pick up the FIRST comment.
			// This test confirms each consumed source line is skipped on
			// subsequent lookups.
			var source = new List<string>
			{
				"{",
				"\t\"Rules\": [",
				"\t\t{",
				"\t\t\t// First rule comment",
				"\t\t\t\"Name\": \"alpha\"",
				"\t\t},",
				"\t\t{",
				"\t\t\t// Second rule comment",
				"\t\t\t\"Name\": \"beta\"",
				"\t\t}",
				"\t]",
				"}",
			};
			// Emitted output declares both Names at the same depth.
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Rules\": [",
				"    {",
				"      \"Name\": \"alpha\"",
				"    },",
				"    {",
				"      \"Name\": \"beta\"",
				"    }",
				"  ]",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			// Both comments present.
			Assert.Contains("First rule comment", result);
			Assert.Contains("Second rule comment", result);
			// First comment precedes "alpha", second precedes "beta".
			var firstIdx = result.IndexOf("First rule comment", StringComparison.Ordinal);
			var alphaIdx = result.IndexOf("\"alpha\"", StringComparison.Ordinal);
			var secondIdx = result.IndexOf("Second rule comment", StringComparison.Ordinal);
			var betaIdx = result.IndexOf("\"beta\"", StringComparison.Ordinal);
			Assert.True(firstIdx < alphaIdx);
			Assert.True(alphaIdx < secondIdx);
			Assert.True(secondIdx < betaIdx);
		}

		// ── End-to-end Write() ─────────────────────────────────────────────

		[Fact]
		public void Write_NoPrivateFile_NoOp()
		{
			// Per #317 Q5: no private file = no delta. Per the convention,
			// even an empty delta is not produced when there's nothing to
			// diff against.
			var basePath = Path.Combine(_tempDir, "config.json");
			var privatePath = Path.Combine(_tempDir, "config.private.json");
			var deltaPath = Path.Combine(_tempDir, "config.private.delta");

			File.WriteAllText(basePath, """{ "A": 1 }""");
			// privatePath deliberately not created.

			ConfigDeltaWriter.Write(basePath, privatePath);

			Assert.False(File.Exists(deltaPath));
		}

		[Fact]
		public void Write_IdenticalConfigs_EmptyDeltaWritten()
		{
			var basePath = Path.Combine(_tempDir, "config.json");
			var privatePath = Path.Combine(_tempDir, "config.private.json");
			var deltaPath = Path.Combine(_tempDir, "config.private.delta");

			File.WriteAllText(basePath, """{ "A": 1, "B": "two" }""");
			File.WriteAllText(privatePath, """{ "A": 1, "B": "two" }""");

			ConfigDeltaWriter.Write(basePath, privatePath);

			Assert.True(File.Exists(deltaPath));
			var content = File.ReadAllText(deltaPath).Trim();
			Assert.Equal("{}", content);
		}

		[Fact]
		public void Write_ScalarDiffWithComment_EmitsDeltaContainingDiffAndComment()
		{
			var basePath = Path.Combine(_tempDir, "config.json");
			var privatePath = Path.Combine(_tempDir, "config.private.json");
			var deltaPath = Path.Combine(_tempDir, "config.private.delta");

			File.WriteAllText(basePath, """
				{
					"A": 1,
					"B": "baseline"
				}
				""");
			File.WriteAllText(privatePath, """
				{
					"A": 1,
					// Local override — short-circuit for QA
					"B": "private"
				}
				""");

			ConfigDeltaWriter.Write(basePath, privatePath);

			Assert.True(File.Exists(deltaPath));
			var content = File.ReadAllText(deltaPath);

			// A matches → not in delta.
			Assert.DoesNotContain("\"A\":", content);
			// B differs → in delta.
			Assert.Contains("\"B\":", content);
			Assert.Contains("\"private\"", content);
			// Operator's comment travels with B.
			Assert.Contains("Local override", content);
		}

		[Fact]
		public void Write_OverwritesExistingDelta()
		{
			// Per #317 convention: delta is overwritten every run.
			var basePath = Path.Combine(_tempDir, "config.json");
			var privatePath = Path.Combine(_tempDir, "config.private.json");
			var deltaPath = Path.Combine(_tempDir, "config.private.delta");

			File.WriteAllText(basePath, """{ "A": 1 }""");
			File.WriteAllText(privatePath, """{ "A": 2 }""");
			// Pre-existing stale delta with unrelated content.
			File.WriteAllText(deltaPath, "STALE CONTENT FROM PREVIOUS RUN");

			ConfigDeltaWriter.Write(basePath, privatePath);

			var content = File.ReadAllText(deltaPath);
			Assert.DoesNotContain("STALE", content);
			Assert.Contains("\"A\"", content);
		}

		// ── #317a: comment injection inside arrays ──────────────────────────

		[Fact]
		public void InjectAdjacentComments_CommentAboveScalarListEntry_Preserved()
		{
			// The motivating case from #317a — comment above a bare string
			// entry inside a list. Pre-fix this comment was dropped because
			// only property declarations triggered comment lookup.
			var source = new List<string>
			{
				"{",
				"\t\"Tags\": [",
				"\t\t\"a\",",
				"\t\t// Form controls — too many false positives",
				"\t\t\"input\",",
				"\t\t\"select\"",
				"\t]",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Tags\": [",
				"    \"a\",",
				"    \"input\",",
				"    \"select\"",
				"  ]",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.Contains("Form controls", result);
			// Comment appears above "input", not above "a" or "select".
			var commentIdx = result.IndexOf("Form controls", StringComparison.Ordinal);
			var aIdx = result.IndexOf("\"a\"", StringComparison.Ordinal);
			var inputIdx = result.IndexOf("\"input\"", StringComparison.Ordinal);
			var selectIdx = result.IndexOf("\"select\"", StringComparison.Ordinal);
			Assert.True(aIdx < commentIdx);
			Assert.True(commentIdx < inputIdx);
			Assert.True(inputIdx < selectIdx);
		}

		[Fact]
		public void InjectAdjacentComments_CommentAboveObjectInArray_Preserved()
		{
			// Comment above a `{` opening an array element — the case for
			// rule lists like ContentUnwantedPatterns where the operator
			// documents each rule above its opening brace.
			var source = new List<string>
			{
				"{",
				"\t\"Rules\": [",
				"\t\t// First rule documents the security check",
				"\t\t{",
				"\t\t\t\"Name\": \"alpha\"",
				"\t\t},",
				"\t\t// Second rule covers the CMS editor errors",
				"\t\t{",
				"\t\t\t\"Name\": \"beta\"",
				"\t\t}",
				"\t]",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Rules\": [",
				"    {",
				"      \"Name\": \"alpha\"",
				"    },",
				"    {",
				"      \"Name\": \"beta\"",
				"    }",
				"  ]",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.Contains("First rule", result);
			Assert.Contains("Second rule", result);
			// Each comment precedes its respective opening brace.
			var firstIdx = result.IndexOf("First rule", StringComparison.Ordinal);
			var alphaIdx = result.IndexOf("\"alpha\"", StringComparison.Ordinal);
			var secondIdx = result.IndexOf("Second rule", StringComparison.Ordinal);
			var betaIdx = result.IndexOf("\"beta\"", StringComparison.Ordinal);
			Assert.True(firstIdx < alphaIdx);
			Assert.True(alphaIdx < secondIdx);
			Assert.True(secondIdx < betaIdx);
		}

		[Fact]
		public void InjectAdjacentComments_MultipleConsecutiveScalarEntriesWithComments_AllTravel()
		{
			// Several consecutive scalar list entries, each with its own
			// preceding comment block. Each should anchor to its own comment.
			var source = new List<string>
			{
				"{",
				"\t\"Items\": [",
				"\t\t// Reason for item one",
				"\t\t\"one\",",
				"\t\t// Reason for item two",
				"\t\t\"two\",",
				"\t\t// Reason for item three",
				"\t\t\"three\"",
				"\t]",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Items\": [",
				"    \"one\",",
				"    \"two\",",
				"    \"three\"",
				"  ]",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.Contains("Reason for item one", result);
			Assert.Contains("Reason for item two", result);
			Assert.Contains("Reason for item three", result);
			// Each reason precedes its corresponding item.
			var r1 = result.IndexOf("Reason for item one", StringComparison.Ordinal);
			var i1 = result.IndexOf("\"one\"", StringComparison.Ordinal);
			var r2 = result.IndexOf("Reason for item two", StringComparison.Ordinal);
			var i2 = result.IndexOf("\"two\"", StringComparison.Ordinal);
			var r3 = result.IndexOf("Reason for item three", StringComparison.Ordinal);
			var i3 = result.IndexOf("\"three\"", StringComparison.Ordinal);
			Assert.True(r1 < i1);
			Assert.True(i1 < r2);
			Assert.True(r2 < i2);
			Assert.True(i2 < r3);
			Assert.True(r3 < i3);
		}

		[Fact]
		public void InjectAdjacentComments_DuplicateScalarsInDifferentLists_CommentsTrackUniquely()
		{
			// The same value appearing in two different lists must pick up
			// the *correct* comment (each its own, not crossover). Tests the
			// consumed-source-line tracking for scalar list entries.
			var source = new List<string>
			{
				"{",
				"\t\"ListA\": [",
				"\t\t// Comment for first occurrence",
				"\t\t\"shared\"",
				"\t],",
				"\t\"ListB\": [",
				"\t\t// Comment for second occurrence",
				"\t\t\"shared\"",
				"\t]",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"ListA\": [",
				"    \"shared\"",
				"  ],",
				"  \"ListB\": [",
				"    \"shared\"",
				"  ]",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.Contains("Comment for first occurrence", result);
			Assert.Contains("Comment for second occurrence", result);
			// First comment precedes the first "shared", second precedes second.
			var c1 = result.IndexOf("Comment for first occurrence", StringComparison.Ordinal);
			var c2 = result.IndexOf("Comment for second occurrence", StringComparison.Ordinal);
			var listAIdx = result.IndexOf("ListA", StringComparison.Ordinal);
			var listBIdx = result.IndexOf("ListB", StringComparison.Ordinal);
			Assert.True(listAIdx < c1);
			Assert.True(c1 < listBIdx);
			Assert.True(listBIdx < c2);
		}

		[Fact]
		public void InjectAdjacentComments_NoCommentAboveScalarEntry_NoComment()
		{
			// Sanity check: scalar entries without preceding comments don't
			// pick up spurious content.
			var source = new List<string>
			{
				"{",
				"\t\"Items\": [",
				"\t\t\"a\",",
				"\t\t\"b\"",
				"\t]",
				"}",
			};
			var emitted = string.Join("\n", new[]
			{
				"{",
				"  \"Items\": [",
				"    \"a\",",
				"    \"b\"",
				"  ]",
				"}",
			});
			var result = ConfigDeltaWriter.InjectAdjacentComments(emitted, source);
			Assert.DoesNotContain("//", result);
		}

		// ── #317b: encoder produces natural escapes and literal non-ASCII ──

		[Fact]
		public void Write_StringWithEmbeddedQuotes_RendersAsBackslashQuote()
		{
			// The motivating case from #317b — operator wrote class=\"h2\"
			// in their config (a CSS-class-name fragment for an XPath); the
			// default System.Text.Json encoder rendered the embedded quote
			// as \u0022, which made the diff look like a divergence from
			// the source. The relaxed encoder produces \" matching the
			// source convention. Both are semantically equivalent JSON;
			// matching the source style keeps the rename-to-overwrite
			// workflow clean.
			var basePath = Path.Combine(_tempDir, "config.json");
			var privatePath = Path.Combine(_tempDir, "config.private.json");
			var deltaPath = Path.Combine(_tempDir, "config.private.delta");

			File.WriteAllText(basePath, """
				{
					"V": "default"
				}
				""");
			File.WriteAllText(privatePath, """
				{
					"V": "class=\"h2\""
				}
				""");

			ConfigDeltaWriter.Write(basePath, privatePath);

			var content = File.ReadAllText(deltaPath);
			// Standard JSON escape, not \uXXXX.
			Assert.Contains("\\\"h2\\\"", content);
			Assert.DoesNotContain("\\u0022", content);
		}

		[Fact]
		public void Write_StringWithNonAscii_RendersAsLiteralCharacter()
		{
			// Non-ASCII content (German umlauts, em dashes, etc.) should
			// emit as literal Unicode characters, not \uXXXX escapes. The
			// UTF-8 BOM on the file declares the encoding so downstream
			// readers don't have to guess.
			var basePath = Path.Combine(_tempDir, "config.json");
			var privatePath = Path.Combine(_tempDir, "config.private.json");
			var deltaPath = Path.Combine(_tempDir, "config.private.delta");

			File.WriteAllText(basePath, """
				{
					"V": "default"
				}
				""", new UTF8Encoding(true));
			File.WriteAllText(privatePath, """
				{
					"V": "Wörterbücher — geprüft"
				}
				""", new UTF8Encoding(true));

			ConfigDeltaWriter.Write(basePath, privatePath);

			var content = File.ReadAllText(deltaPath);
			// Literal characters, not Unicode escapes.
			Assert.Contains("Wörterbücher", content);
			Assert.Contains("—", content);
			Assert.DoesNotContain("\\u00f6", content);  // ö
			Assert.DoesNotContain("\\u00fc", content);  // ü
			Assert.DoesNotContain("\\u2014", content);  // em dash
		}
	}
}
