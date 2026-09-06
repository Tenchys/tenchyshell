namespace TenchyShell.Tui;

/// <summary>Estado de selección y desplazamiento para una lista de una línea por elemento.</summary>
public sealed class TuiListViewport
{
    public int SelectedIndex { get; private set; } = -1;
    public int Offset { get; private set; }
    public int VisibleCount { get; private set; }
    public int TotalCount { get; private set; }

    public void SetItems(int totalCount, int visibleCount, bool resetSelection = false)
    {
        TotalCount = Math.Max(0, totalCount);
        VisibleCount = Math.Max(0, visibleCount);
        if (resetSelection || SelectedIndex < 0) SelectedIndex = TotalCount == 0 ? -1 : 0;
        Normalize();
    }

    public void Move(int amount)
    {
        if (TotalCount == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + amount, 0, TotalCount - 1);
        EnsureSelectedVisible();
    }

    public void PageUp() => Move(-Math.Max(1, VisibleCount));
    public void PageDown() => Move(Math.Max(1, VisibleCount));

    public void Home()
    {
        if (TotalCount == 0) return;
        SelectedIndex = 0;
        EnsureSelectedVisible();
    }

    public void End()
    {
        if (TotalCount == 0) return;
        SelectedIndex = TotalCount - 1;
        EnsureSelectedVisible();
    }

    public IEnumerable<int> VisibleIndexes()
    {
        var end = Math.Min(TotalCount, Offset + VisibleCount);
        for (var index = Offset; index < end; index++) yield return index;
    }

    public string RangeText() => TotalCount == 0
        ? "Atajos 0–0 de 0"
        : $"Atajos {Offset + 1}–{Math.Min(TotalCount, Offset + VisibleCount)} de {TotalCount}";

    public bool HasMoreBelow => Offset + VisibleCount < TotalCount;
    public bool HasMoreAbove => Offset > 0;

    private void Normalize()
    {
        if (TotalCount == 0) { SelectedIndex = -1; Offset = 0; return; }
        SelectedIndex = Math.Clamp(SelectedIndex, 0, TotalCount - 1);
        var maxOffset = Math.Max(0, TotalCount - VisibleCount);
        Offset = Math.Clamp(Offset, 0, maxOffset);
        EnsureSelectedVisible();
    }

    private void EnsureSelectedVisible()
    {
        if (SelectedIndex < 0 || VisibleCount <= 0) { Offset = 0; return; }
        if (SelectedIndex < Offset) Offset = SelectedIndex;
        else if (SelectedIndex >= Offset + VisibleCount) Offset = SelectedIndex - VisibleCount + 1;
        Offset = Math.Clamp(Offset, 0, Math.Max(0, TotalCount - VisibleCount));
    }
}
