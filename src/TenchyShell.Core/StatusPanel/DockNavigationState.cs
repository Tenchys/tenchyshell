namespace TenchyShell.Core.StatusPanel;
using TenchyShell.Core.Configuration;

public sealed class DockNavigationState
{
    private readonly IReadOnlyList<DockNavigationCategory> categories;
    public int CategoryIndex { get; private set; }
    public int ItemIndex { get; private set; }
    public bool Expanded { get; private set; } = true;
    public bool MenuActive { get; private set; }
    public IReadOnlyList<DockNavigationCategory> Categories => categories;
    public DockNavigationCategory? SelectedCategory => categories.Count == 0 ? null : categories[CategoryIndex];
    public DockNavigationItem? SelectedItem => SelectedCategory?.Items.Count > 0 ? SelectedCategory.Items[ItemIndex] : null;

    public DockNavigationState(IEnumerable<DockNavigationCategory> categories)
    {
        this.categories = categories.Where(c => c.Visible).ToArray();
        Normalize();
    }
    public static DockNavigationState FromConfiguration(DockConfiguration configuration)
    {
        var categories = configuration.Categories.ToList();
        var session = categories.FirstOrDefault(c => c.Id.Equals("sesion", StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            categories.Add(new DockCategoryConfiguration { Id = "sesion", Title = "Sesión", Order = 30,
                Items = new[] { new DockItemConfiguration { Id = "recovery", Title = "Recuperación", Action = "recovery" } } });
        }
        else if (!session.Items.Any(i => i.Action.Equals("recovery", StringComparison.OrdinalIgnoreCase)))
        {
            categories[categories.IndexOf(session)] = new DockCategoryConfiguration { Id = session.Id, Title = session.Title,
                Order = session.Order, Visible = session.Visible,
                Items = session.Items.Append(new DockItemConfiguration { Id = "recovery", Title = "Recuperación", Action = "recovery" }).ToArray() };
        }
        return new DockNavigationState(categories.OrderBy(c => c.Order).Select(c => new DockNavigationCategory(
            c.Id, c.Title, c.Visible, c.Items.Where(i => i.Visible).Select(i => new DockNavigationItem(i.Id, i.Title, i.Action, i.Visible)).ToArray())));
    }
    public void MoveCategory(int delta) { if (categories.Count == 0) return; CategoryIndex = Mod(CategoryIndex + delta, categories.Count); ItemIndex = 0; }
    public void MoveItem(int delta) { var count = SelectedCategory?.Items.Count ?? 0; if (count > 0) ItemIndex = Mod(ItemIndex + delta, count); }
    public bool SelectAt(int y, int firstRow = 76, int rowHeight = 19)
    {
        if (y < firstRow || rowHeight <= 0) return false;
        var row = (y - firstRow) / rowHeight;
        for (var categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
        {
            if (row == 0)
            {
                CategoryIndex = categoryIndex;
                ItemIndex = 0;
                return true;
            }
            row--;
            if (!Expanded)
                continue;
            var itemCount = Math.Max(1, categories[categoryIndex].Items.Count);
            if (row < itemCount)
            {
                CategoryIndex = categoryIndex;
                ItemIndex = categories[categoryIndex].Items.Count == 0 ? 0 : row;
                return categories[categoryIndex].Items.Count > 0;
            }
            row -= itemCount;
        }
        return false;
    }
    public void ToggleExpanded() => Expanded = !Expanded;
    public void SetMenuActive(bool active) => MenuActive = active;
    public void Reset() { CategoryIndex = 0; ItemIndex = 0; Expanded = true; MenuActive = false; Normalize(); }
    private void Normalize() { if (categories.Count == 0) { CategoryIndex = ItemIndex = 0; return; } CategoryIndex = Math.Clamp(CategoryIndex, 0, categories.Count - 1); ItemIndex = Math.Clamp(ItemIndex, 0, Math.Max(0, categories[CategoryIndex].Items.Count - 1)); }
    private static int Mod(int value, int count) => (value % count + count) % count;
}
public sealed record DockNavigationCategory(string Id, string Title, bool Visible, IReadOnlyList<DockNavigationItem> Items);
public sealed record DockNavigationItem(string Id, string Title, string Action, bool Visible);
