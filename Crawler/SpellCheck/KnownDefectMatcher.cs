namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// Mutes known, accepted chrome/template language defects from findings (config
	/// <see cref="SpellCheckEngineConfig.KnownChromeLanguageDefects"/>). The CMS emits the same
	/// offending text from the same element sitewide; rather than reporting it on every page (noise)
	/// or collapsing findings (lossy dedup), the operator declares the exact (source-path, text) so
	/// only that specific defect is muted while everything else from the same element still surfaces.
	///
	/// A declared pattern is matched against the run's VALUE: exact, or — if it ends with '*' — by
	/// prefix on the literal before the '*'. When it matches, only the words WITHIN the literal
	/// portion are muted; a trailing varying part (e.g. a per-page title after "Springe zu*") is left
	/// to be checked normally, so genuine per-page content defects are never hidden.
	/// </summary>
	public sealed class KnownDefectMatcher
	{
		// source-path -> list of (literal words to mute, prefix?, literal text) per declared pattern.
		private readonly Dictionary<string, List<Pattern>> _byPath;

		private readonly record struct Pattern(string Literal, bool IsPrefix, HashSet<string> LiteralWords);

		public KnownDefectMatcher(IReadOnlyDictionary<string, List<string>>? config)
		{
			_byPath = new Dictionary<string, List<Pattern>>(StringComparer.OrdinalIgnoreCase);
			if (config == null)
			{
				return;
			}

			foreach (var (path, patterns) in config)
			{
				if (patterns == null)
				{
					continue;
				}

				var compiled = new List<Pattern>();
				foreach (var raw in patterns)
				{
					if (string.IsNullOrEmpty(raw))
					{
						continue;
					}

					bool isPrefix = raw.EndsWith("*", StringComparison.Ordinal);
					string literal = isPrefix ? raw[..^1] : raw;

					// The words of the literal portion are the mute-set: anything after the '*'
					// (the varying tail) is intentionally NOT here, so it stays checked.
					var words = SpellTokenizer
						.Tokenize(new TextRun(null!, RunSource.TextNode, "literal", literal))
						.Select(t => t.Text)
						.ToHashSet(StringComparer.Ordinal);

					compiled.Add(new Pattern(literal, isPrefix, words));
				}

				if (compiled.Count > 0)
				{
					_byPath[path] = compiled;
				}
			}
		}

		public bool IsEmpty => _byPath.Count == 0;

		/// <summary>
		/// True if a finding for <paramref name="word"/>, sourced from <paramref name="sourcePath"/> in
		/// a run whose value is <paramref name="runValue"/>, is a declared known chrome defect to mute.
		/// </summary>
		public bool IsKnownDefect(string sourcePath, string runValue, string word)
		{
			if (_byPath.Count == 0 || !_byPath.TryGetValue(sourcePath, out var patterns))
			{
				return false;
			}

			foreach (var p in patterns)
			{
				bool matches = p.IsPrefix
					? runValue.StartsWith(p.Literal, StringComparison.Ordinal)
					: string.Equals(runValue, p.Literal, StringComparison.Ordinal);

				if (matches && p.LiteralWords.Contains(word))
				{
					return true; // word is part of the declared defect text → mute
				}
			}

			return false;
		}
	}
}
