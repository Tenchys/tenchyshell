namespace TenchyShell.Core.Processes;

public interface IProcessLauncher
{
    ProcessLaunchResult Launch(ProcessLaunchRequest request);
}
