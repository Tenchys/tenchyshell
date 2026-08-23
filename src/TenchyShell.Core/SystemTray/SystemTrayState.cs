namespace TenchyShell.Core.SystemTray;

public sealed record SystemTrayItem(string Id, string Title, string Description)
{
    public string IconText { get; init; } = "•";
}

public sealed class SystemTrayState
{
    private readonly IReadOnlyList<SystemTrayItem> items;

    public SystemTrayState(IEnumerable<SystemTrayItem> items, string? selectedId = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var index = Array.FindIndex(this.items.ToArray(), item => item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) SelectedIndex = index;
        }
    }

    public IReadOnlyList<SystemTrayItem> Items => items;

    public int SelectedIndex { get; private set; }

    public SystemTrayItem? SelectedItem => items.Count == 0 ? null : items[SelectedIndex];

    public void Move(bool backwards = false)
    {
        if (items.Count == 0)
        {
            SelectedIndex = 0;
            return;
        }

        SelectedIndex = backwards
            ? (SelectedIndex - 1 + items.Count) % items.Count
            : (SelectedIndex + 1) % items.Count;
    }

    /// <summary>Selecciona una fila visible sin alterar el estado ante un índice inválido.</summary>
    public bool TrySelect(int index)
    {
        if (index < 0 || index >= items.Count || index == SelectedIndex)
        {
            return false;
        }

        SelectedIndex = index;
        return true;
    }
}
