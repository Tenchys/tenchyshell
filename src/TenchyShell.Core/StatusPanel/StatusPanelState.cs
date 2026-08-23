using System.Globalization;

namespace TenchyShell.Core.StatusPanel;

public sealed class StatusPanelState
{
    public const int DefaultWorkspace = 1;

    public StatusPanelState()
    {
        Workspace = DefaultWorkspace;
    }

    public int Workspace { get; private set; }

    public string WorkspaceLabel => $"Workspace {Workspace}";

    public string GetTimeLabel(DateTime now) => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public void SetWorkspace(int workspace)
    {
        if (workspace < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(workspace), "El workspace debe ser mayor o igual a 1.");
        }

        Workspace = workspace;
    }
}

public sealed class StatusPanelVisibilityState
{
    private readonly HashSet<string> activeMenus = new(StringComparer.Ordinal);
    private DateTimeOffset? workspaceAnnouncementExpiresAt;

    public bool IsVisible { get; private set; }

    public bool IsPinnedByHotkey { get; private set; }

    public bool HasActiveMenus => activeMenus.Count > 0;

    public bool HasActiveWorkspaceAnnouncement(DateTimeOffset now) =>
        workspaceAnnouncementExpiresAt is { } expiresAt && now < expiresAt;

    public bool ToggleByHotkey()
    {
        workspaceAnnouncementExpiresAt = null;
        if (IsVisible && IsPinnedByHotkey)
        {
            IsVisible = false;
            IsPinnedByHotkey = false;
            return false;
        }

        IsVisible = true;
        IsPinnedByHotkey = true;
        return true;
    }

    public void ShowFromEdge()
    {
        IsVisible = true;
        IsPinnedByHotkey = false;
        workspaceAnnouncementExpiresAt = null;
    }

    public void ShowWorkspaceAnnouncement(DateTimeOffset now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "La duración del anuncio debe ser positiva.");
        }

        IsVisible = true;
        workspaceAnnouncementExpiresAt = now.Add(duration);
    }

    public bool HideWhenPointerLeaves(bool pointerInsidePanel, DateTimeOffset? now = null)
    {
        if (!IsVisible || IsPinnedByHotkey || HasActiveMenus || pointerInsidePanel ||
            HasActiveWorkspaceAnnouncement(now ?? DateTimeOffset.UtcNow))
        {
            return false;
        }

        IsVisible = false;
        return true;
    }

    public void Hide()
    {
        IsVisible = false;
        IsPinnedByHotkey = false;
        workspaceAnnouncementExpiresAt = null;
    }

    public void SetMenuActive(string menuId, bool isActive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuId);

        if (isActive)
        {
            activeMenus.Add(menuId);
        }
        else
        {
            activeMenus.Remove(menuId);
        }
    }
}

public readonly record struct StatusPanelPoint(int X, int Y);

public readonly record struct StatusPanelRectangle(int Left, int Top, int Right, int Bottom);

public static class StatusPanelEdgeDetector
{
    public static bool IsAtLeftEdge(StatusPanelPoint point, StatusPanelRectangle workArea, int edgeZone)
    {
        return edgeZone >= 0
            && point.X >= workArea.Left
            && point.X <= workArea.Left + edgeZone
            && point.Y >= workArea.Top
            && point.Y < workArea.Bottom;
    }

    public static bool IsInside(StatusPanelPoint point, StatusPanelRectangle rectangle) =>
        point.X >= rectangle.Left
            && point.X < rectangle.Right
            && point.Y >= rectangle.Top
            && point.Y < rectangle.Bottom;
}
