namespace Crawler
{
	using System;
	using System.Collections.Generic;

	/// <summary>
	/// 650 — a configuration halt that is the OPERATOR'S to resolve: the run cannot proceed because of
	/// a setup choice, not a crash and not a bug. It carries a structured, teaching payload (a Heading
	/// and body Lines) so the entry point can render a calm, actionable SETUP screen instead of dumping
	/// a raw exception string.
	///
	/// The tone is deliberate and is the whole point of the type: a halt here means "you have not yet
	/// told the tool something it needs", never "you did something wrong". The tool ships opinion-free
	/// and asks the operator to aim it; the screen should empower that, not scold. Rendered with the
	/// yellow action block (an action-needed tone), never the red error block (a failure tone).
	///
	/// Subclasses InvalidOperationException so every existing catch/logging path treats it exactly like
	/// any other config halt — the Heading/Lines are a pure addition the renderer consumes when present.
	/// </summary>
	public sealed class ConfigHaltException : InvalidOperationException
	{
		/// <summary>Short, yellow-highlighted screen title (e.g. "SETUP NEEDED · …").</summary>
		public string Heading { get; }

		/// <summary>Body lines, emitted verbatim under the heading — wording, blank lines and indents
		/// are authored by the thrower so the screen reads exactly as intended.</summary>
		public IReadOnlyList<string> Lines { get; }

		public ConfigHaltException(string heading, IReadOnlyList<string> lines)
			: base(Flatten(heading, lines))
		{
			Heading = heading ?? string.Empty;
			Lines = lines ?? Array.Empty<string>();
		}

		// A flattened message keeps the exception fully usable anywhere a plain message is expected
		// (the run log, a non-interactive/silent run, a test assertion) without the structured renderer.
		private static string Flatten(string heading, IReadOnlyList<string> lines)
			=> (heading ?? string.Empty)
				+ Environment.NewLine
				+ string.Join(Environment.NewLine, lines ?? Array.Empty<string>());
	}
}
