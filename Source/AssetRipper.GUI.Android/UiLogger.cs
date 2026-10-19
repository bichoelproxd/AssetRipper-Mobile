using System;
using System.Collections.Generic;
using AssetRipper.Import.Logging;

namespace AssetRipper.Mobile;

/// <summary>Keeps the latest log lines so the native screen can show them.</summary>
internal sealed class UiLogger : ILogger
{
	private const int MaxLines = 400;
	private static readonly object Lock = new();
	private static readonly LinkedList<string> Lines = new();

	public static UiLogger Instance { get; } = new();
	public event Action<string>? LineAdded;

	public static string[] Snapshot()
	{
		lock (Lock)
		{
			string[] result = new string[Lines.Count];
			Lines.CopyTo(result, 0);
			return result;
		}
	}

	public void Log(LogType type, LogCategory category, string message)
	{
		string prefix = type switch
		{
			LogType.Error => "[ERR] ",
			LogType.Warning => "[WARN] ",
			_ => ""
		};
		string line = category == LogCategory.None ? prefix + message : $"{prefix}[{category}] {message}";
		Append(line);
	}

	public void BlankLine(int numLines) { }

	private void Append(string line)
	{
		lock (Lock)
		{
			Lines.AddLast(line);
			while (Lines.Count > MaxLines)
			{
				Lines.RemoveFirst();
			}
		}
		LineAdded?.Invoke(line);
	}
}
