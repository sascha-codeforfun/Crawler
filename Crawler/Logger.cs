namespace Crawler
{
	public static class Logger
	{
		private static string? logFilePath;
		private static bool _silent = false;
		private static bool _consoleQuiet = false;
		private static readonly object logLock = new();

		/// <summary>
		/// Suppresses INFO/WARNING console echo for the duration of the returned scope
		/// (file logging is unaffected; ERROR still echoes). Used to keep the console
		/// clean during the analysis phase, where a polished per-step summary is printed
		/// via <see cref="ConsoleUi"/> instead of the raw timestamped lines. Restores the
		/// previous state on dispose; nests safely.
		/// </summary>
		public static IDisposable QuietConsole()
		{
			lock (logLock)
			{
				bool previous = _consoleQuiet;
				_consoleQuiet = true;
				return new QuietConsoleScope(previous);
			}
		}

		private sealed class QuietConsoleScope(bool previous) : IDisposable
		{
			public void Dispose()
			{
				lock (logLock)
				{
					_consoleQuiet = previous;
				}
			}
		}

		/// <summary>
		/// Shared lock for console output. Use this when writing multi-step console
		/// sequences outside of Logger (e.g. interactive prompts in Program.cs) to
		/// prevent interleaving with concurrent log writes.
		/// </summary>
		public static object ConsoleLock => logLock;

		public static void Initialize(string fileName, bool silent = false)
		{
			_silent = silent;

			if (string.IsNullOrWhiteSpace(fileName))
			{
				throw new ArgumentException("Log file name cannot be null or empty", nameof(fileName));
			}

			string directoryPath = AppDomain.CurrentDomain.BaseDirectory;
			logFilePath = Path.Combine(directoryPath, fileName);

			if (File.Exists(logFilePath))
			{
				File.Delete(logFilePath);
				// Log after deletion so the warning appears in the new log file too.
				LogWarning($"Existing log file '{logFilePath}' was deleted and recreated.");
			}

			// File-only: the console surfaces this via the CRAWLER banner instead.
			using (QuietConsole())
			{
				LogInfo("Logger initialized. Logging will be written to: " + logFilePath);
			}
		}

		/// <summary>The resolved application.log path, or null before Initialize.</summary>
		public static string? LogFilePath => logFilePath;

		public static void LogError(string message) => Log("ERROR", message);

		public static void LogWarning(string message) => Log("WARNING", message);

		public static void LogInfo(string message) => Log("INFO", message);

		/// <summary>
		/// 651 — writes a multi-line detail block to the LOG FILE only, never the console. Pairs with a
		/// calm ConsoleUi render for halts whose exhaustive detail belongs in the log (and is the only
		/// record in a silent run) but must not red-wall the screen. Continuation lines are indented so
		/// the block reads as a single entry.
		/// </summary>
		public static void LogDetailToFile(string message)
		{
			try
			{
				lock (logLock)
				{
					if (logFilePath is null)
					{
						return;
					}

					Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
					var stamp = $"{DateTime.UtcNow:O}: [ERROR] ";
					var body = (message ?? string.Empty).Replace("\n", "\n    ");
					File.AppendAllText(logFilePath, stamp + body + Environment.NewLine);
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Logging failed: {ex.Message}");
			}
		}

		/// <summary>
		/// Logs a per-file spell-check result. The error count and filename are coloured
		/// on console; file logging goes through the standard Log() path.
		///  0 errors   → silent
		///  1–4 errors → DarkCyan
		///  5–9 errors → Cyan
		/// 10+ errors  → White
		/// </summary>
		public static void LogSpellResult(string filename, int errorCount)
		{
			if (errorCount == 0)
			{
				return;
			}

			var message = $"{errorCount} spelling error(s) found in: {filename}";
			var logEntry = $"{DateTime.UtcNow:O}: [INFO] {message}";

			var color = errorCount >= 10 ? ConsoleColor.White
					  : errorCount >= 5 ? ConsoleColor.Cyan
					  : ConsoleColor.DarkCyan;

			try
			{
				lock (logLock)
				{
					// File write
					Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
					File.AppendAllText(logFilePath!, logEntry + Environment.NewLine);

					// Console write — inside the same lock so no other Log() or
					// LogSpellResult() call can interleave between our Write() calls.
					if (!_silent)
					{
						try
						{
							Console.ResetColor();
							Console.Write($"{DateTime.UtcNow:O}: [INFO] ");
							Console.ForegroundColor = color;
							Console.Write(message);
							Console.ResetColor();
							Console.WriteLine();
						}
						catch (IOException)
						{
							Console.ResetColor();
							Console.WriteLine(logEntry);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Logging failed: {ex.Message}");
			}
		}

		private static void Log(string logLevel, string message)
		{
			if (string.IsNullOrEmpty(logFilePath))
			{
				throw new InvalidOperationException("Log file path must be initialized.");
			}

			var logEntry = $"{DateTime.UtcNow:O}: [{logLevel}] {message}";

			try
			{
				lock (logLock)
				{
					// Console and file writes share the same lock so multi-step console
					// sequences in LogSpellResult cannot interleave with single-line writes here.
					if (!_silent && (!_consoleQuiet || logLevel == "ERROR"))
					{
						try
						{
							Console.ForegroundColor = logLevel switch
							{
								"ERROR" => ConsoleColor.Red,
								"WARNING" => ConsoleColor.DarkYellow,
								_ => ConsoleColor.Gray,
							};
							Console.WriteLine(logEntry);
							Console.ResetColor();
						}
						catch (IOException)
						{
							Console.ResetColor();
							Console.WriteLine(logEntry);
						}
					}

					var directoryPath = Path.GetDirectoryName(logFilePath);
					if (string.IsNullOrWhiteSpace(directoryPath))
					{
						throw new InvalidOperationException("The directory path for the log file is not valid.");
					}

					Directory.CreateDirectory(directoryPath);
					File.AppendAllText(logFilePath, logEntry + Environment.NewLine);
				}
			}
			catch (Exception ex)
			{
				// Always write logging failures to console regardless of silent mode.
				try { Console.ResetColor(); } catch { }
				Console.WriteLine($"Logging failed: {ex.Message}");
			}
		}
	}
}
