using MinimalShell.Core.Processes;

namespace MinimalShell.Core.Applications;

public interface IApplicationLauncher
{
    ProcessLaunchResult Launch(ApplicationEntry application);
}
