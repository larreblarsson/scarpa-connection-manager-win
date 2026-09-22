using System;
using System.IO;

public class TerminalLogger
{
    private readonly string _filePath;
    private readonly bool _includeTimestamps;
    private readonly object _lock = new object();

    public TerminalLogger(string folderPath, string sessionName, bool append, bool includeTimestamps)
    {
        _includeTimestamps = includeTimestamps;

        // Ensure the directory exists
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        _filePath = Path.Combine(folderPath, $"{sessionName}_log.txt");

        // If set to overwrite, clear the file contents when the session starts
        if (!append && File.Exists(_filePath))
        {
            File.WriteAllText(_filePath, string.Empty);
        }
    }

    public void LogOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_lock)
        {
            string output = _includeTimestamps
                ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}"
                : text;

            File.AppendAllText(_filePath, output);
        }
    }
}
