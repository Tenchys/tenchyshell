# TenchyShell.Workspaces

Gestión de workspaces virtuales y foco.

`WorkspaceManager` mantiene nueve workspaces, asocia ventanas top-level por `HWND`, oculta y muestra ventanas al cambiar de workspace y mueve la ventana activa entre workspaces. La interacción Win32 se inyecta mediante `IWorkspaceWindowService`, por lo que la lógica puede probarse sin una sesión gráfica.
