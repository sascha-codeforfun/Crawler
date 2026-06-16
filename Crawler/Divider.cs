namespace Crawler
{
	/// <summary>
	/// Horizontal-rule primitives shared by console output and ticket-text
	/// generation. Each named primitive is a full-width (<see cref="DefaultWidth"/>)
	/// run of a single character; <see cref="Of"/> builds an arbitrary-width run
	/// for call sites that need a narrower or wider rule (e.g. the 60-wide body
	/// divider in ticket text).
	///
	/// Primitives carry no trailing newline — the caller controls line breaks
	/// (e.g. via StringBuilder.AppendLine). Named primitives are cached as
	/// static readonly strings since they are reused across many ticket entries
	/// and console frames; <see cref="Of"/> allocates fresh because it is the
	/// ad-hoc escape hatch, not a hot path.
	///
	/// Naming: the dash rule is <see cref="Line"/> (not "Dash") and the equals
	/// rule is <see cref="DoubleLine"/> (not "Equals") to avoid colliding with
	/// object.Equals and to read naturally at call sites. The remaining three
	/// are named for their character.
	///
	/// Coexists deliberately with ConsoleUi.Separator / ConsoleUi.Divider,
	/// which are Unicode box-drawing rules (═ U+2550, ─ U+2500) for terminal
	/// display. This class is the ASCII family ('-', '=', '_', '#', '+') for
	/// ticket-text .log files, which are copy-pasted into external trackers
	/// where ASCII is the safe lowest common denominator. The two are scoped
	/// distinctly (Divider.Line vs ConsoleUi.Divider) and are NOT meant to be
	/// merged — they target different output surfaces with different
	/// character-set assumptions.
	/// </summary>
	public static class Divider
	{
		/// <summary>
		/// Default width for all named primitives. Promoted to a named constant
		/// so a future global width change is a single edit. Ticket-text body
		/// dividers that want 60 use <see cref="Of"/> explicitly at the call
		/// site, where the layout decision lives.
		/// </summary>
		public const int DefaultWidth = 80;

		/// <summary>Single line: a run of '-'. (Renamed from "Dash".)</summary>
		public static readonly string Line = new('-', DefaultWidth);

		/// <summary>Double line: a run of '='. (Renamed from "Equals" to avoid the object.Equals clash.)</summary>
		public static readonly string DoubleLine = new('=', DefaultWidth);

		/// <summary>A run of '_'.</summary>
		public static readonly string Underscore = new('_', DefaultWidth);

		/// <summary>A run of '#'.</summary>
		public static readonly string Hash = new('#', DefaultWidth);

		/// <summary>A run of '+'.</summary>
		public static readonly string Plus = new('+', DefaultWidth);

		/// <summary>
		/// Builds a rule of <paramref name="count"/> copies of
		/// <paramref name="character"/>. Escape hatch for any width the named
		/// primitives don't cover (e.g. Of('-', 60) for the ticket-text body
		/// divider). Returns an empty string for non-positive counts.
		/// </summary>
		public static string Of(char character, int count)
			=> count > 0 ? new string(character, count) : string.Empty;
	}
}
