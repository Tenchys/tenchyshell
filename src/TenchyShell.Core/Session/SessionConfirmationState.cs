namespace TenchyShell.Core.Session;

public enum SessionConfirmationStatus { Idle, AwaitingConfirmation, Executing, Completed, Failed }

public sealed class SessionConfirmationStateMachine
{
    public SessionConfirmationStatus Status { get; private set; } = SessionConfirmationStatus.Idle;
    public SessionAction? PendingAction { get; private set; }
    public string? Error { get; private set; }
    public bool Request(SessionAction action)
    {
        if (Status == SessionConfirmationStatus.Executing)
            return false;

        // A different destructive action must never inherit confirmation from
        // the previous one. The caller has to ask again explicitly.
        if (Status == SessionConfirmationStatus.AwaitingConfirmation)
        {
            if (PendingAction == action)
                return false;

            Cancel();
            return false;
        }

        PendingAction = action;
        Error = null;
        Status = SessionConfirmationStatus.AwaitingConfirmation;
        return true;
    }
    public bool Cancel() { if (Status != SessionConfirmationStatus.AwaitingConfirmation) return false; PendingAction = null; Status = SessionConfirmationStatus.Idle; return true; }
    public async Task<bool> ConfirmAsync(Func<SessionAction, Task<bool>> execute)
    {
        if (Status != SessionConfirmationStatus.AwaitingConfirmation || PendingAction is not { } action) return false;
        Status = SessionConfirmationStatus.Executing;
        try
        {
            var success = await execute(action).ConfigureAwait(false);
            Status = success ? SessionConfirmationStatus.Completed : SessionConfirmationStatus.Failed;
            if (!success) Error = "La acción de sesión fue rechazada por Windows.";
            return success;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Status = SessionConfirmationStatus.Failed;
            return false;
        }
    }

    public Task<bool> ConfirmAsync(SessionActionService service) =>
        ConfirmAsync(action => Task.FromResult(service.Execute(action, confirmed: true).Succeeded));
    public void Reset() { Status = SessionConfirmationStatus.Idle; PendingAction = null; Error = null; }
}
