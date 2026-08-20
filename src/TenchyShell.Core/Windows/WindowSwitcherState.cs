namespace TenchyShell.Core.Windows;

public sealed record WindowSwitcherItem(IntPtr Handle, string Title);

public sealed class WindowSwitcherState
{
    private readonly IReadOnlyList<WindowSwitcherItem> items;

    public WindowSwitcherState(IEnumerable<WindowSwitcherItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        this.items = items
            .Where(item => item.Handle != IntPtr.Zero)
            .GroupBy(item => item.Handle)
            .Select(group => group.First())
            .ToArray();
    }

    public IReadOnlyList<WindowSwitcherItem> Items => items;

    public int SelectedIndex { get; private set; }

    public int FirstVisibleIndex { get; private set; }

    public WindowSwitcherItem? SelectedItem =>
        items.Count == 0 ? null : items[SelectedIndex];

    public void Move(bool backwards = false) => Move(backwards, items.Count);

    public void Move(int visibleItemCount) => Move(backwards: false, visibleItemCount: visibleItemCount);

    public void Move(bool backwards, int visibleItemCount)
    {
        if (items.Count == 0)
        {
            SelectedIndex = 0;
            FirstVisibleIndex = 0;
            return;
        }

        visibleItemCount = Math.Max(1, visibleItemCount);

        SelectedIndex = backwards
            ? (SelectedIndex - 1 + items.Count) % items.Count
            : (SelectedIndex + 1) % items.Count;

        EnsureSelectedVisible(visibleItemCount);
    }

    public void MovePage(bool backwards, int visibleItemCount)
    {
        if (items.Count == 0)
        {
            SelectedIndex = 0;
            FirstVisibleIndex = 0;
            return;
        }

        visibleItemCount = Math.Max(1, visibleItemCount);
        SelectedIndex = backwards
            ? Math.Max(0, SelectedIndex - visibleItemCount)
            : Math.Min(items.Count - 1, SelectedIndex + visibleItemCount);
        EnsureSelectedVisible(visibleItemCount);
    }

    public void SelectFirst(int visibleItemCount)
    {
        SelectedIndex = 0;
        EnsureSelectedVisible(Math.Max(1, visibleItemCount));
    }

    public void SelectLast(int visibleItemCount)
    {
        SelectedIndex = Math.Max(0, items.Count - 1);
        EnsureSelectedVisible(Math.Max(1, visibleItemCount));
    }

    public void EnsureSelectedVisibleForPainting(int visibleItemCount) =>
        EnsureSelectedVisible(Math.Max(1, visibleItemCount));

    private void EnsureSelectedVisible(int visibleItemCount)
    {
        var maximumFirstIndex = Math.Max(0, items.Count - visibleItemCount);

        if (SelectedIndex < FirstVisibleIndex)
        {
            FirstVisibleIndex = SelectedIndex;
        }
        else if (SelectedIndex >= FirstVisibleIndex + visibleItemCount)
        {
            FirstVisibleIndex = SelectedIndex - visibleItemCount + 1;
        }

        FirstVisibleIndex = Math.Clamp(FirstVisibleIndex, 0, maximumFirstIndex);
    }
}
