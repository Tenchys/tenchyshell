namespace TenchyShell.Core.Processes;

public sealed class ProcessLaunchRequest
{
    public ProcessLaunchRequest(string fileName, IEnumerable<string>? arguments = null, string? workingDirectory = null)
    {
        FileName = fileName;
        Arguments = (arguments ?? Array.Empty<string>()).ToArray();
        WorkingDirectory = workingDirectory;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }
}
