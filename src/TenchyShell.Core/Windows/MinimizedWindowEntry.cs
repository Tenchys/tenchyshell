namespace TenchyShell.Core.Windows;

/// <summary>Representa una ventana minimizada que pertenece a un workspace.</summary>
public sealed record MinimizedWindowEntry(IntPtr Handle, string Title, int Workspace);

/// <summary>
/// Estado navegable del menú de ventanas minimizadas.
/// La clase sólo aplica reglas deterministas sobre los datos recibidos; no hace
/// llamadas Win32 ni decide si un HWND continúa existiendo.
/// </summary>
public sealed class MinimizedWindowMenuState
{
    private readonly List<MinimizedWindowEntry> items;

    public MinimizedWindowMenuState(IEnumerable<MinimizedWindowEntry> entries, int activeWorkspace)
    {
        ArgumentNullException.ThrowIfNull(entries);

        items = entries
            .Where(entry => entry.Handle != IntPtr.Zero)
            .Where(entry => entry.Workspace == activeWorkspace)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Title))
            .GroupBy(entry => entry.Handle)
            .Select(group => group.First())
            .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Title, StringComparer.Ordinal)
            .ThenBy(entry => entry.Handle.ToInt64())
            .ToList();
    }

    public IReadOnlyList<MinimizedWindowEntry> Items => items;

    public bool IsEmpty => items.Count == 0;

    public int SelectedIndex { get; private set; }

    public MinimizedWindowEntry? SelectedItem => items.Count == 0 ? null : items[SelectedIndex];

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

    /// <summary>Selecciona una fila sin modificar el estado ante un índice inválido.</summary>
    public bool TrySelect(int index)
    {
        if (index < 0 || index >= items.Count || index == SelectedIndex) return false;

        SelectedIndex = index;
        return true;
    }

    /// <summary>Quita una ventana que dejó de ser válida entre el listado y la acción.</summary>
    public bool Remove(IntPtr windowHandle)
    {
        var index = items.FindIndex(item => item.Handle == windowHandle);
        if (index < 0) return false;

        items.RemoveAt(index);
        if (items.Count == 0)
        {
            SelectedIndex = 0;
        }
        else if (SelectedIndex >= items.Count)
        {
            SelectedIndex = items.Count - 1;
        }

        return true;
    }
}
