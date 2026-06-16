namespace Crawler
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Text.Json;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Renders the calm "CONFIG CHECK · Configuration file" halt when the config file is not valid JSON
	/// (a hand-edit slip: a dropped quote, a missing comma, an unclosed bracket). Replaces the raw
	/// red System.Text.Json wall with a what / where / why / fix screen, the same skin as the dictionary
	/// integrity halt. The full technical message (path, byte position, the .NET error text) goes to the
	/// log via <see cref="Logger.LogDetailToFile"/>, not the console.
	///
	/// The "where" is built from the exception's STRUCTURED signal — <see cref="JsonException.Path"/> and
	/// <see cref="JsonException.LineNumber"/> — never by scraping the message. The "why" is deliberately
	/// GENERIC: System.Text.Json reports the first place the parse breaks, which for a large file is often
	/// just downstream of the real slip (a missing quote runs the string forward until it hits the next
	/// illegal character, and THAT position is what gets reported). A specific "line break inside a value"
	/// diagnosis would point at an innocent newline; the generic text names the usual causes and tells the
	/// operator the slip may sit just above the reported line — which is true for every case.
	/// </summary>
	internal static class ConfigJsonErrorScreen
	{
		// "$.A.B.C[30]" -> "A → B → C, entry #31". Array indices become 1-based human ordinals attached to
		// the preceding segment; dotted segments become arrowed. Null/blank path -> a neutral placeholder.
		internal static string FriendlyPath(string? jsonPath)
		{
			if (string.IsNullOrWhiteSpace(jsonPath))
			{
				return "(location not reported)";
			}

			string p = jsonPath.Trim();
			if (p.StartsWith("$.", StringComparison.Ordinal)) p = p.Substring(2);
			else if (p.StartsWith("$", StringComparison.Ordinal)) p = p.Substring(1);

			if (p.Length == 0)
			{
				return "(top level)";
			}

			// [N] -> ", entry #(N+1)" (1-based for humans), then dotted segments -> arrows.
			p = Regex.Replace(p, @"\[(\d+)\]", m =>
				long.TryParse(m.Groups[1].Value, out long i) ? $", entry #{i + 1}" : m.Value);
			p = p.Replace(".", " \u2192 ");
			return p;
		}

		// System.Text.Json LineNumber is ZERO-based (lines read before the error), so a human, 1-based
		// editor line is LineNumber + 1. Null -> placeholder.
		internal static string HumanLine(long? zeroBasedLineNumber) =>
			zeroBasedLineNumber.HasValue ? (zeroBasedLineNumber.Value + 1).ToString() : "(not reported)";

		internal static void Render(string configFilePath, JsonException ex)
		{
			string file = Path.GetFileName(configFilePath);
			string setting = FriendlyPath(ex.Path);
			string line = HumanLine(ex.LineNumber);

			// Full technical detail to the log only — keeps the audit, calms the face.
			Logger.LogDetailToFile(
				$"Config JSON parse error in {configFilePath}: {ex.Message} " +
				$"| Path: {ex.Path} | LineNumber(0-based): {ex.LineNumber} | BytePositionInLine: {ex.BytePositionInLine}");

			var blocks = new List<ConsoleUi.CheckBlock>
			{
				new("Problem", new List<ConsoleUi.CheckLine>
				{
					new(ConsoleUi.CheckTone.Prose, "The configuration file has a formatting error, so the run can't start until it's fixed."),
					new(ConsoleUi.CheckTone.Data, $"File      {file}"),
					new(ConsoleUi.CheckTone.Data, $"Setting   {setting}"),
					new(ConsoleUi.CheckTone.Data, $"Line      {line}"),
				}),
				new("Why", new List<ConsoleUi.CheckLine>
				{
					new(ConsoleUi.CheckTone.Prose,
						"The file isn't valid JSON at this point. Common causes are a missing comma between entries, "
						+ "a missing or extra quote or bracket, or a line break inside a value. The line is where parsing "
						+ "stopped \u2014 the actual slip is sometimes just above it."),
				}),
				new("Fix", new List<ConsoleUi.CheckLine>
				{
					new(ConsoleUi.CheckTone.Accent,
						$"Open {file} and check that line and the lines just above it, focusing on the entry named above."),
					new(ConsoleUi.CheckTone.Data, "Full technical detail is in the log."),
				}),
			};

			ConsoleUi.WriteConfigCheck("Configuration file", blocks);
		}
	}
}
