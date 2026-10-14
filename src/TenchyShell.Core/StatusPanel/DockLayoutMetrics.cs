namespace TenchyShell.Core.StatusPanel;

/// <summary>
/// Métricas compartidas por el renderizado y el hit-testing del menú del dock.
/// El espacio de confirmación se reserva siempre para que las filas no cambien
/// de posición al solicitar una acción de sesión.
/// </summary>
public static class DockLayoutMetrics
{
    public const int ConfirmationTextTop = 70;
    public const int FirstRowTop = 95;
    public const int BaseRowHeight = 19;
    public const int RowHeight = 22;
    public const int TextLeft = 18;
    public const int TextFontHeight = 18;
    public const int BottomPadding = 8;

    public static int GetRowCount(IEnumerable<DockNavigationCategory> categories) =>
        categories.Sum(category => 1 + Math.Max(1, category.Items.Count));

    public static int GetDockHeight(int minimumHeight, IEnumerable<DockNavigationCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        return Math.Max(minimumHeight, FirstRowTop + GetRowCount(categories) * RowHeight + BottomPadding);
    }

    public static bool TryGetRow(int y, out int row)
    {
        if (y < FirstRowTop)
        {
            row = -1;
            return false;
        }

        row = (y - FirstRowTop) / RowHeight;
        return true;
    }
}
