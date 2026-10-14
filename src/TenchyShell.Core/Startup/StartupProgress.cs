namespace TenchyShell.Core.Startup;

/// <summary>Fases ordenadas de la preparación de una sesión interactiva.</summary>
public enum StartupModule
{
    MigrationAndConfiguration = 1,
    CoreServices = 2,
    WorkspacesAndWindowTracking = 3,
    DockAndStatusPanel = 4,
    LayoutWindowSelectorAndHooks = 5,
    TrayNetworkWallpaperAndLanguage = 6,
    LocalApiAndWatchers = 7,
    Hotkeys = 8,
    BenchmarkNotificationsAndTui = 9,
    FinalSessionPreparation = 10
}

public enum StartupProgressStatus
{
    NotStarted,
    InProgress,
    Ready,
    Failed,
    Closed
}

public sealed record StartupProgressSnapshot(
    StartupProgressStatus Status,
    StartupModule? CurrentModule,
    string Message,
    IReadOnlyList<StartupModule> CompletedModules,
    string? ErrorMessage = null,
    Exception? Error = null)
{
    public bool IsReady => Status is StartupProgressStatus.Ready or StartupProgressStatus.Closed;

    public bool IsFailed => Status == StartupProgressStatus.Failed;

    public bool IsClosed => Status == StartupProgressStatus.Closed;
}

/// <summary>
/// Contrato pequeño y comprobable que separa el progreso de arranque de Win32.
/// Las actualizaciones sólo avanzan por el orden definido por StartupModule.
/// </summary>
public interface IStartupProgressReporter
{
    StartupProgressSnapshot Snapshot { get; }

    event Action<StartupProgressSnapshot>? Changed;

    void ReportStarted(StartupModule module, string message);

    void ReportCompleted(StartupModule module);

    void ReportError(StartupModule module, string message, Exception? error = null);

    void Close();
}

public sealed class StartupProgressState : IStartupProgressReporter
{
    private readonly object syncRoot = new();
    private readonly List<StartupModule> completedModules = new();
    private StartupProgressSnapshot snapshot = new(
        StartupProgressStatus.NotStarted,
        null,
        "Preparando TenchyShell…",
        Array.Empty<StartupModule>());

    public StartupProgressSnapshot Snapshot
    {
        get
        {
            lock (syncRoot) return snapshot;
        }
    }

    public event Action<StartupProgressSnapshot>? Changed;

    public void ReportStarted(StartupModule module, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Update(() =>
        {
            EnsureOpen();
            if (snapshot.Status == StartupProgressStatus.Failed)
                throw new InvalidOperationException("El progreso de arranque ya está en estado de error.");
            if (snapshot.CurrentModule is not null)
                throw new InvalidOperationException($"La fase {snapshot.CurrentModule} aún está en curso.");

            var expected = (StartupModule)(completedModules.Count + 1);
            if (module != expected)
                throw new InvalidOperationException($"La fase de arranque esperada es {expected}, no {module}.");

            snapshot = CreateSnapshot(StartupProgressStatus.InProgress, module, message);
        });
    }

    public void ReportCompleted(StartupModule module)
    {
        Update(() =>
        {
            EnsureOpen();
            if (snapshot.Status != StartupProgressStatus.InProgress || snapshot.CurrentModule != module)
                throw new InvalidOperationException($"La fase {module} no está en curso.");

            completedModules.Add(module);
            var isReady = completedModules.Count == Enum.GetValues<StartupModule>().Length;
            snapshot = CreateSnapshot(
                isReady ? StartupProgressStatus.Ready : StartupProgressStatus.NotStarted,
                null,
                isReady ? "Sesión preparada" : snapshot.Message);
        });
    }

    public void ReportError(StartupModule module, string message, Exception? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Update(() =>
        {
            EnsureOpen();
            if (snapshot.Status != StartupProgressStatus.InProgress || snapshot.CurrentModule != module)
                throw new InvalidOperationException($"La fase {module} no está en curso y no puede fallar.");

            snapshot = CreateSnapshot(StartupProgressStatus.Failed, module, message, message, error);
        });
    }

    public void Close()
    {
        StartupProgressSnapshot changed;
        lock (syncRoot)
        {
            if (snapshot.IsClosed) return;
            snapshot = snapshot with { Status = StartupProgressStatus.Closed };
            changed = snapshot;
        }

        Changed?.Invoke(changed);
    }

    private void Update(Action update)
    {
        StartupProgressSnapshot changed;
        lock (syncRoot)
        {
            update();
            changed = snapshot;
        }

        Changed?.Invoke(changed);
    }

    private StartupProgressSnapshot CreateSnapshot(
        StartupProgressStatus status,
        StartupModule? currentModule,
        string message,
        string? errorMessage = null,
        Exception? error = null) => new(
        status,
        currentModule,
        message,
        completedModules.ToArray(),
        errorMessage,
        error);

    private void EnsureOpen()
    {
        if (snapshot.IsClosed)
            throw new InvalidOperationException("No se puede actualizar el progreso después de cerrar el contexto.");
    }
}
