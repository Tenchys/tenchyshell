namespace TenchyShell.Tui;

public sealed record TuiMenuItem(string Id, string Label);

public sealed class TuiMenu
{
    private readonly IReadOnlyList<TuiMenuItem> items;

    public TuiMenu(IEnumerable<TuiMenuItem> items)
    {
        this.items = items.ToArray();
        SelectedIndex = this.items.Count == 0 ? -1 : 0;
    }

    public IReadOnlyList<TuiMenuItem> Items => items;

    public int SelectedIndex { get; private set; }

    public TuiMenuItem? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < items.Count ? items[SelectedIndex] : null;

    public void MoveNext() => Move(1);

    public void MovePrevious() => Move(-1);

    public void Move(int amount)
    {
        if (items.Count == 0) return;
        var next = (SelectedIndex + amount) % items.Count;
        if (next < 0) next += items.Count;
        SelectedIndex = next;
    }
}
