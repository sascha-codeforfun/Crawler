namespace Crawler.Security
{
	using System;

	/// <summary>
	/// Pure CSV formula-injection guard for the export surface. A spreadsheet
	/// application (Excel, LibreOffice Calc, Sheets, Numbers) evaluates a cell
	/// whose first character is a formula trigger (= + - @, or a leading TAB / CR
	/// that the app trims before parsing) as a live formula. The CSV file on disk
	/// is inert text; the risk exists only the moment a human opens an export in a
	/// spreadsheet. <see cref="Neutralize"/> prepends the spreadsheet-native text
	/// escape (a single apostrophe) so the cell is shown verbatim and never
	/// evaluated; <see cref="Denormalize"/> is its exact inverse for the rare
	/// reader that round-trips a neutralized content field. No I/O, no logging, no
	/// throwing — deterministic so it can be exhaustively tested.
	/// </summary>
	public static class CsvInjectionGuard
	{
		// [KEEP] Security boundary — under the project threat model an in-scope
		// origin may be hostile and chooses the strings the crawler records (URLs,
		// file names, content excerpts). SHA-naming of the write surface pushed
		// those attacker-influenced strings out of the filesystem and into the
		// _comma / _semicolon exports, so the export is now the live formula-
		// injection surface. Neutralize is applied at the single compose chokepoint
		// so every export is covered; the apostrophe is the OWASP-recommended,
		// display-clean escape. Fail-safe direction: when in doubt, neutralize.

		/// <summary>
		/// Returns true when <paramref name="c"/> is a leading character a
		/// spreadsheet would treat as the start of a formula. TAB (U+0009) and CR
		/// (U+000D) are included because spreadsheet apps trim them before parsing,
		/// so a value like "\t=cmd()" is still evaluated.
		/// </summary>
		private static bool IsFormulaTrigger(char c) =>
			c is '=' or '+' or '-' or '@' or '\t' or '\r';

		/// <summary>
		/// Prepends a single apostrophe when <paramref name="field"/> begins with a
		/// formula trigger, leaving every other value untouched. Operates on the raw
		/// field (call before any control-character sanitisation) so a leading TAB /
		/// CR is caught before it is folded to a space. Returns the input unchanged
		/// when null or empty.
		/// </summary>
		public static string Neutralize(string? field)
		{
			if (string.IsNullOrEmpty(field))
			{
				return field ?? string.Empty;
			}

			return IsFormulaTrigger(field[0]) ? "'" + field : field;
		}

		/// <summary>
		/// Exact inverse of <see cref="Neutralize"/>: strips a single leading
		/// apostrophe only when it is immediately followed by a formula trigger, so
		/// a value the guard added is restored while a genuine value that merely
		/// starts with an apostrophe (e.g. "'tis") is preserved. Returns the input
		/// unchanged when null or empty.
		/// </summary>
		public static string Denormalize(string? field)
		{
			if (string.IsNullOrEmpty(field))
			{
				return field ?? string.Empty;
			}

			if (field.Length >= 2 && field[0] == '\'' && IsFormulaTrigger(field[1]))
			{
				return field[1..];
			}

			return field;
		}
	}
}
