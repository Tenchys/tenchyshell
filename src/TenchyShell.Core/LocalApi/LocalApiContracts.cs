using System.Text.Json.Serialization;

namespace TenchyShell.Core.LocalApi;

public static class LocalApiDefaults
{
    public const int Version = 1;
    public const string PipeName = "TenchyShell.LocalApi.v1";
    public const int MaxMessageBytes = 64 * 1024;
    public const int OperationTimeoutMilliseconds = 5000;
    public const int MaxConnections = 4;
}

public sealed record LocalApiRequest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("method")] string? Method = null,
    [property: JsonPropertyName("params")] Dictionary<string, object?>? Parameters = null,
    [property: JsonPropertyName("topics")] IReadOnlyList<string>? Topics = null);

public sealed record LocalApiEnvelope(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("ok")] bool? Ok = null,
    [property: JsonPropertyName("result")] object? Result = null,
    [property: JsonPropertyName("error")] LocalApiError? Error = null,
    [property: JsonPropertyName("topic")] string? Topic = null,
    [property: JsonPropertyName("data")] object? Data = null);

public sealed record LocalApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record LocalApiSnapshot(
    [property: JsonPropertyName("activeWorkspace")] int ActiveWorkspace,
    [property: JsonPropertyName("workspaces")] IReadOnlyList<LocalApiWorkspace> Workspaces,
    [property: JsonPropertyName("windows")] IReadOnlyList<LocalApiWindow> Windows,
    [property: JsonPropertyName("applications")] IReadOnlyList<LocalApiApplication> Applications);

public sealed record LocalApiWorkspace(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("windowCount")] int WindowCount);

public sealed record LocalApiWindow(
    [property: JsonPropertyName("hwnd")] string Hwnd,
    [property: JsonPropertyName("workspace")] int Workspace,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("minimized")] bool Minimized,
    [property: JsonPropertyName("operable")] bool Operable);

public sealed record LocalApiApplication(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("activationKind")] string ActivationKind,
    [property: JsonPropertyName("appUserModelId")] string? AppUserModelId);

public interface ILocalApiStateProvider
{
    LocalApiSnapshot GetSnapshot();
    LocalApiResult SwitchWorkspace(int workspace);
    LocalApiResult MoveActiveWindow(int workspace);
    event EventHandler<LocalApiStateChangedEventArgs>? StateChanged;

    /// <summary>Informa qué tópicos tienen consumidores activos para evitar trabajo innecesario.</summary>
    void SetTopicInterest(string topic, bool interested) { }
}

public sealed record LocalApiResult(bool Succeeded, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static LocalApiResult Success() => new(true);
    public static LocalApiResult Failure(string code, string message) => new(false, code, message);
}

public sealed class LocalApiStateChangedEventArgs : EventArgs
{
    public LocalApiStateChangedEventArgs(string topic)
    {
        Topic = topic;
    }

    public string Topic { get; }
}
