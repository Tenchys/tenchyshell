namespace MinimalShell.Core.Logging;

public sealed class FileLogger : ILogger
{
    private readonly object syncRoot = new();
    private readonly string logFilePath;

    public FileLogger(string? logDirectory = null)
    {
        var directory = logDirectory ?? GetDefaultLogDirectory();
        Directory.CreateDirectory(directory);
        logFilePath = Path.Combine(directory, "minimalshell.log");
    }

    public string LogFilePath => logFilePath;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private static string GetDefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MinimalShell",
        "logs");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";

        lock (syncRoot)
        {
            File.AppendAllText(logFilePath, line);
        }
    }
}
