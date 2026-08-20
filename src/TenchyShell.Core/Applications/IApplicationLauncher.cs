using TenchyShell.Core.Processes;

namespace TenchyShell.Core.Applications;

public interface IApplicationLauncher
{
    ProcessLaunchResult Launch(ApplicationEntry application);
}
