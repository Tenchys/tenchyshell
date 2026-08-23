using System.Runtime.InteropServices;
using TenchyShell.Core.Commands;
using TenchyShell.Core.Configuration;
using TenchyShell.Core.Diagnostics;
using TenchyShell.Core.InputLanguages;
using TenchyShell.Core.Logging;
using TenchyShell.Core.Network;
using TenchyShell.Core.Notifications;
using TenchyShell.Core.SystemTray;
using TenchyShell.Core.Wallpaper;

namespace TenchyShell.Win32;

/// <summary>Host de bandeja propio de TenchyShell; no depende de Explorer.</summary>
public sealed class SystemTrayService : ISystemTrayService, IDisposable
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int RowHeight = 30;
    private const int FirstRowTop = 42;
    private const uint SelectedRowBackgroundColor = 0x001D1D1D;
    private const nuint AutoHideTimerId = 1;
    private const uint AutoHideTimeoutMilliseconds = 1500;

    private readonly NativeMethods.WindowProc windowProcedure;
    private readonly string windowClassName = $"TenchyShell.SystemTray.{Guid.NewGuid():N}";
    private readonly IntPtr moduleHandle;
    private readonly ShellConfiguration shellConfiguration;
    private readonly SystemTrayConfiguration configuration;
    private readonly ShellActions actions;
    private readonly ISystemTrayScriptRunner scriptRunner;
    private readonly INetworkService networkService;
    private readonly IWallpaperService wallpaperService;
    private readonly IInputLanguageService inputLanguageService;
    private readonly ILogger logger;
    private readonly MessageLoopHost messageLoop;
    private readonly DesktopAreaPolicy desktopAreaPolicy;
    private readonly LiveBenchmarkRecorder? benchmarkRecorder;
    private readonly SystemTrayAutoDismissState autoDismissState = new();
    private readonly NotificationCenter? notificationCenter;
    private readonly List<RuntimeItem> runtimeItems = new();
    private readonly SemaphoreSlim networkRefreshGate = new(1, 1);
    private SystemTrayState state = new(Array.Empty<SystemTrayItem>());
    private NetworkSnapshot networkSnapshot = new(false, false, Array.Empty<NetworkInterfaceSnapshot>(), null);
    private InputLanguageSnapshot inputLanguageSnapshot = new(Array.Empty<InputLanguage>(), IntPtr.Zero, null);
    private IntPtr windowHandle;
    private IntPtr lastForegroundWindow;
    private ushort windowClassAtom;
    private bool isVisible;
    private bool wallpaperMenuOpen;
    private bool inputLanguageMenuOpen;
    private bool notificationMenuOpen;
    private bool isDisposed;

    public SystemTrayService(
        ShellConfiguration shellConfiguration,
        ShellActions actions,
        ISystemTrayScriptRunner scriptRunner,
        INetworkService networkService,
        IWallpaperService wallpaperService,
        IInputLanguageService inputLanguageService,
        ILogger logger,
        MessageLoopHost messageLoop,
        DesktopAreaPolicy? desktopAreaPolicy = null,
        LiveBenchmarkRecorder? benchmarkRecorder = null,
        NotificationCenter? notificationCenter = null)
    {
        this.shellConfiguration = shellConfiguration;
        configuration = shellConfiguration.SystemTray;
        this.actions = actions;
        this.scriptRunner = scriptRunner;
        this.networkService = networkService;
        this.wallpaperService = wallpaperService;
        this.inputLanguageService = inputLanguageService;
        this.logger = logger;
        this.messageLoop = messageLoop;
        this.desktopAreaPolicy = desktopAreaPolicy ?? new DesktopAreaPolicy();
        this.benchmarkRecorder = benchmarkRecorder;
        this.notificationCenter = notificationCenter;
        if (notificationCenter is not null) notificationCenter.Changed += OnNotificationCenterChanged;
        windowProcedure = WindowProcedure;
        moduleHandle = NativeMethods.GetModuleHandle(null);
    }

    public bool TryOpen(out string? error)
    {
        return TryOpen(openedFromPointer: false, out error);
    }

    /// <summary>Abre la bandeja desde una superficie apuntada por el mouse.</summary>
    public bool TryOpenFromPointer(out string? error)
    {
        return TryOpen(openedFromPointer: true, out error);
    }

    /// <summary>Notifica las transiciones visibles del menú raíz de la bandeja.</summary>
    public event Action<bool>? VisibilityChanged;

    private bool TryOpen(bool openedFromPointer, out string? error)
    {
        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (isVisible) Hide();
            else Show(openedFromPointer: openedFromPointer);
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = $"No se pudo mostrar la bandeja propia: {exception.Message}";
            logger.Error(error, exception);
            return false;
        }
    }

    public bool TryOpenInputLanguageSelector(out string? error)
    {
        if (!shellConfiguration.InputLanguage.Enabled)
        {
            error = "El selector de idioma está deshabilitado en la configuración.";
            return false;
        }

        try
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (!isVisible) Show(inputLanguageMenu: true);
            else
            {
                DisableAutoHide();
                inputLanguageMenuOpen = true;
                wallpaperMenuOpen = false;
                RefreshInputLanguages();
                RebuildState();
                Invalidate();
            }
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = $"No se pudo mostrar el selector de idioma: {exception.Message}";
            logger.Error(error, exception);
            return false;
        }
    }

    public void Dispose()
    {
        if (isDisposed) return;
        if (notificationCenter is not null) notificationCenter.Changed -= OnNotificationCenterChanged;
        StopRefreshers();
        Hide();
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(windowHandle);
            windowHandle = IntPtr.Zero;
        }
        if (windowClassAtom != 0)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
        }
        isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void Show(bool inputLanguageMenu = false, bool openedFromPointer = false)
    {
        lastForegroundWindow = NativeMethods.GetForegroundWindow();
        EnsureWindow();
        LoadItems();
        inputLanguageMenuOpen = inputLanguageMenu;
        RebuildState();
        var workArea = GetPrimaryWorkArea();
        var dockY = workArea.Top + Math.Max(0, (workArea.Bottom - workArea.Top - shellConfiguration.StatusPanel.Height) / 2);
        var x = workArea.Left + shellConfiguration.StatusPanel.Width + 8;
        var popupHeight = GetPopupHeight();
        var y = Math.Max(workArea.Top, Math.Min(dockY, workArea.Bottom - popupHeight));

        NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_TOPMOST, x, y,
            configuration.Width, popupHeight, SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(windowHandle);
        NativeMethods.SetFocus(windowHandle);
        isVisible = true;
        VisibilityChanged?.Invoke(true);
        autoDismissState.Open(openedFromPointer, IsPointerOverTray());
        UpdateAutoHideTimer();
        TrackPointerLeave();
        StartRefreshers();
        _ = RefreshNetworkAsync();
        _ = RefreshWallpaperCatalogAsync();
        Invalidate();
    }

    private void Hide()
    {
        var wasVisible = isVisible;
        StopAutoHideTimer();
        autoDismissState.Close();
        StopRefreshers();
        if (windowHandle != IntPtr.Zero) NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_HIDE);
        isVisible = false;
        if (wasVisible) VisibilityChanged?.Invoke(false);
    }

    private void LoadItems()
    {
        StopRefreshers();
        runtimeItems.Clear();
        wallpaperMenuOpen = false;
        inputLanguageMenuOpen = false;
        notificationMenuOpen = false;

        var configuredItems = configuration.Items.Count > 0
            ? configuration.Items
            : CreateBuiltInItems();

        foreach (var item in configuredItems)
        {
            runtimeItems.Add(new RuntimeItem(item, CreateSnapshot(item)));
        }

        if (notificationCenter is not null && !runtimeItems.Any(item => item.Snapshot.Id.Equals("notifications", StringComparison.OrdinalIgnoreCase)))
        {
            runtimeItems.Add(new RuntimeItem(
                new SystemTrayItemConfiguration { Id = "notifications", Title = "Notificaciones", Tooltip = "Avisos de la sesión" },
                CreateNotificationSnapshot()));
        }

        if (shellConfiguration.Wallpaper.Enabled && !runtimeItems.Any(item => item.Snapshot.Id.Equals("wallpaper", StringComparison.OrdinalIgnoreCase)))
        {
            runtimeItems.Add(new RuntimeItem(
                new SystemTrayItemConfiguration { Id = "wallpaper", Title = "Fondos", Text = "Cargando imágenes...", Tooltip = "Seleccionar fondo" },
                new SystemTrayItemSnapshot("wallpaper", "Fondos", "Cargando imágenes...", "Seleccionar fondo", string.Empty, "unknown", "open", false, null)));
        }

        if (shellConfiguration.InputLanguage.Enabled && !runtimeItems.Any(item => item.Snapshot.Id.Equals("input-language", StringComparison.OrdinalIgnoreCase)))
        {
            runtimeItems.Add(new RuntimeItem(
                new SystemTrayItemConfiguration { Id = "input-language", Title = shellConfiguration.InputLanguage.Title, Tooltip = "Cambiar idioma o distribución de teclado" },
                new SystemTrayItemSnapshot("input-language", shellConfiguration.InputLanguage.Title, "Consultando...", "Cambiar idioma o distribución de teclado", string.Empty, "unknown", "open", false, null)));
        }

        RefreshInputLanguages();

        var networkItem = runtimeItems.FirstOrDefault(item => item.Snapshot.Id.Equals("network", StringComparison.OrdinalIgnoreCase));
        if (networkItem is not null)
        {
            networkItem.Snapshot = networkItem.Snapshot with
            {
                Text = networkSnapshot.Interfaces.Count == 0 && string.IsNullOrWhiteSpace(networkSnapshot.Error)
                    ? "Consultando estado..."
                    : GetNetworkText(networkSnapshot),
                Tooltip = "Estado de red; usa las acciones de cada interfaz"
            };
            AddNetworkRows(networkSnapshot);
        }

        RebuildState();
    }

    private async Task RefreshNetworkAsync()
    {
        if (!await networkRefreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var snapshot = await Task.Run(networkService.GetSnapshot).ConfigureAwait(false);
            if (isDisposed) return;
            messageLoop.Post(() => ApplyNetworkSnapshot(snapshot));
        }
        catch (Exception exception)
        {
            if (!isDisposed) messageLoop.Post(() => logger.Error("No se pudo actualizar el estado de red.", exception));
        }
        finally
        {
            networkRefreshGate.Release();
        }
    }

    private void ApplyNetworkSnapshot(NetworkSnapshot snapshot)
    {
        if (isDisposed) return;
        networkSnapshot = snapshot;
        runtimeItems.RemoveAll(item =>
            item.Snapshot.Id.StartsWith("interface:", StringComparison.OrdinalIgnoreCase) ||
            item.Snapshot.Id.StartsWith("wifi:", StringComparison.OrdinalIgnoreCase));
        var networkItem = runtimeItems.FirstOrDefault(item => item.Snapshot.Id.Equals("network", StringComparison.OrdinalIgnoreCase));
        if (networkItem is null) return;
        networkItem.Snapshot = networkItem.Snapshot with
        {
            Text = GetNetworkText(snapshot),
            State = string.IsNullOrWhiteSpace(snapshot.Error) ? "ok" : "error",
            IsStale = !string.IsNullOrWhiteSpace(snapshot.Error),
            Error = snapshot.Error
        };
        AddNetworkRows(snapshot);
        RebuildState();
        if (windowHandle != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_TOPMOST, 0, 0,
                configuration.Width, GetPopupHeight(), NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
        }
        Invalidate();
    }

    private void AddNetworkRows(NetworkSnapshot snapshot)
    {
        foreach (var ethernet in snapshot.Interfaces.Where(item => item.Kind == NetworkInterfaceKind.Ethernet))
        {
            var actionLabel = ethernet.IsOperational ? "[Desconectar]" : "[Conectar]";
            runtimeItems.Add(new RuntimeItem(
                new SystemTrayItemConfiguration
                {
                    Id = $"interface:{ethernet.Name}",
                    Title = $"Cable · {ethernet.Name}",
                    Text = $"{(ethernet.IsOperational ? "activa" : "inactiva")} · {actionLabel}",
                    Tooltip = $"Activar o desactivar {ethernet.Name}"
                },
                new SystemTrayItemSnapshot(
                    $"interface:{ethernet.Name}",
                    $"Cable · {ethernet.Name}",
                    $"{(ethernet.IsOperational ? "activa" : "inactiva")} · {actionLabel}",
                    $"Activar o desactivar {ethernet.Name}",
                    string.Empty,
                    ethernet.IsOperational ? "ok" : "unknown",
                    ethernet.IsOperational ? "disable" : "enable",
                    false,
                    null)));
        }

        foreach (var wifi in snapshot.WifiNetworks)
        {
            var action = wifi.IsConnected ? "[Desconectar]" : "[Conectar]";
            runtimeItems.Add(new RuntimeItem(
                new SystemTrayItemConfiguration
                {
                    Id = $"wifi:{wifi.Ssid}",
                    Title = wifi.Ssid,
                    Text = $"Señal {wifi.SignalQuality}% · {(wifi.IsSecured ? "protegida" : "abierta")} · {action}",
                    Tooltip = $"{(wifi.IsConnected ? "Desconectar de" : "Conectar a")} {wifi.Ssid}"
                },
                new SystemTrayItemSnapshot(
                    $"wifi:{wifi.Ssid}",
                    wifi.Ssid,
                    $"Señal {wifi.SignalQuality}% · {(wifi.IsSecured ? "protegida" : "abierta")} · {action}",
                    $"{(wifi.IsConnected ? "Desconectar de" : "Conectar a")} {wifi.Ssid}",
                    string.Empty,
                    wifi.IsConnected ? "ok" : "unknown",
                    wifi.Ssid,
                    false,
                    null)));
        }
    }

    private async Task RefreshWallpaperCatalogAsync()
    {
        if (!shellConfiguration.Wallpaper.Enabled) return;
        var catalog = await Task.Run(wallpaperService.GetCatalog).ConfigureAwait(false);
        if (isDisposed) return;
        messageLoop.Post(() => ApplyWallpaperCatalog(catalog));
    }

    private void ApplyWallpaperCatalog(WallpaperCatalog catalog)
    {
        if (isDisposed || !isVisible) return;
        runtimeItems.RemoveAll(item => item.Snapshot.Id.StartsWith("wallpaper:", StringComparison.OrdinalIgnoreCase));
        var wallpaper = runtimeItems.FirstOrDefault(item => item.Snapshot.Id.Equals("wallpaper", StringComparison.OrdinalIgnoreCase));
        if (wallpaper is null) return;
        wallpaper.Snapshot = wallpaper.Snapshot with
        {
            Text = catalog.Error ?? $"{catalog.Items.Count} imágenes · selecciona una"
        };
        foreach (var item in catalog.Items)
        {
            runtimeItems.Add(new RuntimeItem(
                new SystemTrayItemConfiguration { Id = $"wallpaper:{item.Path}", Title = item.Name, Text = "[Aplicar]", Tooltip = item.Path },
                new SystemTrayItemSnapshot($"wallpaper:{item.Path}", item.Name, "[Aplicar]", item.Path, string.Empty, "ok", item.Path, false, null)));
        }
        RebuildState();
        NativeMethods.SetWindowPos(windowHandle, NativeMethods.HWND_TOPMOST, 0, 0,
            configuration.Width, GetPopupHeight(), NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
        Invalidate();
    }

    private void StartRefreshers()
    {
        foreach (var runtimeItem in runtimeItems.Where(item => !string.IsNullOrWhiteSpace(item.Configuration.Command)))
        {
            runtimeItem.Timer = new Timer(
                _ => _ = RefreshItemAsync(runtimeItem),
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(runtimeItem.Configuration.IntervalMilliseconds));
        }
    }

    private void StopRefreshers()
    {
        foreach (var runtimeItem in runtimeItems)
        {
            runtimeItem.Timer?.Dispose();
            runtimeItem.Timer = null;
            runtimeItem.RefreshCancellation.Cancel();
        }
    }

    private async Task RefreshItemAsync(RuntimeItem runtimeItem)
    {
        if (!await runtimeItem.RefreshGate.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            runtimeItem.RefreshCancellation.Cancel();
            runtimeItem.RefreshCancellation.Dispose();
            runtimeItem.RefreshCancellation = new CancellationTokenSource();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runtimeItem.RefreshCancellation.Token);
            timeout.CancelAfter(runtimeItem.Configuration.TimeoutMilliseconds);

            var result = await scriptRunner.RunAsync(
                runtimeItem.Configuration,
                shellConfiguration.ConfigurationDirectory,
                timeout.Token).ConfigureAwait(false);

            if (isDisposed) return;
            messageLoop.Post(() => ApplyScriptResult(runtimeItem, result));
        }
        catch (Exception exception)
        {
            if (!isDisposed)
            {
                messageLoop.Post(() => ApplyScriptResult(
                    runtimeItem,
                    SystemTrayScriptOutputResult.Failure(exception.Message)));
            }
        }
        finally
        {
            runtimeItem.RefreshGate.Release();
        }
    }

    private void ApplyScriptResult(RuntimeItem runtimeItem, SystemTrayScriptOutputResult result)
    {
        if (isDisposed || !runtimeItems.Contains(runtimeItem)) return;

        if (!result.Succeeded || result.Output is null)
        {
            runtimeItem.Snapshot = runtimeItem.Snapshot with
            {
                State = "error",
                IsStale = true,
                Error = result.Error ?? "El script no devolvió datos."
            };
            logger.Error($"Error en el elemento de bandeja '{runtimeItem.Configuration.Id}': {runtimeItem.Snapshot.Error}");
        }
        else
        {
            var output = result.Output;
            runtimeItem.Snapshot = runtimeItem.Snapshot with
            {
                Text = string.IsNullOrWhiteSpace(output.Text) ? runtimeItem.Snapshot.Text : output.Text.Trim(),
                Tooltip = string.IsNullOrWhiteSpace(output.Tooltip) ? runtimeItem.Snapshot.Tooltip : output.Tooltip.Trim(),
                Icon = ResolveAsset(output.Icon, runtimeItem.Configuration.DefaultIcon),
                State = NormalizeState(output.State),
                Action = output.Action,
                IsStale = false,
                Error = null
            };
        }

        RebuildState();
        Invalidate();
    }

    private void RebuildState()
    {
        if (notificationMenuOpen && notificationCenter is not null)
        {
            var notificationItems = notificationCenter.GetActive()
                .Select(notification => new SystemTrayItem(
                    $"notification:{notification.Id}",
                    notification.AppName,
                    string.IsNullOrWhiteSpace(notification.Body) ? notification.Title : $"{notification.Title} — {notification.Body}"))
                .ToList();
            notificationItems.Add(new SystemTrayItem("notifications-back", "← Notificaciones", "Volver al menú principal"));
            state = new SystemTrayState(notificationItems);
            return;
        }
        if (inputLanguageMenuOpen)
        {
            var languageItems = inputLanguageSnapshot.Languages
                .Select(language => new SystemTrayItem(
                    $"input-language:{language.Handle.ToInt64():X}",
                    language.DisplayName,
                    language.Handle == inputLanguageSnapshot.ActiveHandle ? "Activo" : "Seleccionar")
                {
                    IconText = language.Handle == inputLanguageSnapshot.ActiveHandle ? "*" : "•"
                })
                .ToList();
            languageItems.Add(new SystemTrayItem("input-language-back", "← Idioma", "Volver al menú principal"));
            state = new SystemTrayState(
                languageItems,
                $"input-language:{inputLanguageSnapshot.ActiveHandle.ToInt64():X}");
            return;
        }

        var visibleItems = wallpaperMenuOpen
            ? runtimeItems.Where(item => item.Snapshot.Id.StartsWith("wallpaper:", StringComparison.OrdinalIgnoreCase))
            : runtimeItems.Where(item => !item.Snapshot.Id.StartsWith("wallpaper:", StringComparison.OrdinalIgnoreCase));
        var items = visibleItems.Select(runtimeItem => new SystemTrayItem(
            runtimeItem.Snapshot.Id,
            runtimeItem.Snapshot.Title,
            BuildDescription(runtimeItem.Snapshot))
        {
            IconText = GetIconText(runtimeItem.Snapshot)
        }).ToList();
        if (wallpaperMenuOpen)
        {
            items.Insert(0, new SystemTrayItem("wallpaper-back", "← Fondos", "Volver al menú principal"));
        }
        state = new SystemTrayState(items);
    }

    private void RefreshInputLanguages()
    {
        if (!shellConfiguration.InputLanguage.Enabled) return;

        inputLanguageSnapshot = inputLanguageService.GetSnapshot(lastForegroundWindow);
        var item = runtimeItems.FirstOrDefault(runtimeItem =>
            runtimeItem.Snapshot.Id.Equals("input-language", StringComparison.OrdinalIgnoreCase));
        if (item is null) return;

        if (!string.IsNullOrWhiteSpace(inputLanguageSnapshot.Error))
        {
            item.Snapshot = item.Snapshot with
            {
                Text = "No disponible",
                State = "error",
                IsStale = true,
                Error = inputLanguageSnapshot.Error
            };
            logger.Error($"No se pudo consultar el idioma de teclado: {inputLanguageSnapshot.Error}");
            return;
        }

        var active = inputLanguageSnapshot.ActiveLanguage;
        item.Snapshot = item.Snapshot with
        {
            Text = active is null
                ? "No disponible"
                : InputLanguageLabel.Format(active.ShortName, active.DisplayName, shellConfiguration.InputLanguage.LabelFormat),
            Tooltip = active?.Tooltip ?? "Windows no informó el método de entrada activo.",
            State = active is null ? "unknown" : "ok",
            IsStale = false,
            Error = active is null ? "No hay método de entrada activo." : null
        };
    }

    private static string BuildDescription(SystemTrayItemSnapshot snapshot)
    {
        var description = string.IsNullOrWhiteSpace(snapshot.Text) ? snapshot.Tooltip : snapshot.Text;
        if (snapshot.IsStale) description += " · desactualizado";
        return description;
    }

    private string ResolveAsset(string? asset, string fallback)
    {
        var selected = string.IsNullOrWhiteSpace(asset) ? fallback : asset;
        if (string.IsNullOrWhiteSpace(selected)) return string.Empty;
        var path = Path.IsPathRooted(selected)
            ? selected
            : Path.Combine(shellConfiguration.ConfigurationDirectory, selected);
        return File.Exists(path) ? path : string.Empty;
    }

    private static string NormalizeState(string? state) => state?.ToLowerInvariant() switch
    {
        "warning" => "warning",
        "error" => "error",
        "unknown" => "unknown",
        _ => "ok"
    };

    private static string GetIconText(SystemTrayItemSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Icon)) return "■";
        return snapshot.State switch
        {
            "error" => "!",
            "warning" => "?",
            _ => "•"
        };
    }

    private List<SystemTrayItemConfiguration> CreateBuiltInItems()
    {
        var items = new List<SystemTrayItemConfiguration>
        {
            new() { Id = "tenchyshell", Title = "TenchyShell", Text = "Bandeja propia activa", Tooltip = "TenchyShell", Icon = "MS" },
            new() { Id = "terminal", Title = "Terminal", Text = "Abrir terminal configurado", Command = string.Empty },
            new() { Id = "files", Title = "Yazi", Text = "Abrir administrador de archivos", Command = string.Empty },
            new() { Id = "browser", Title = "Navegador", Text = "Abrir navegador configurado", Command = string.Empty },
            new() { Id = "network", Title = "Red", Text = "Consultando estado..." },
            new() { Id = "wallpaper", Title = "Fondos", Text = "Cargando imágenes..." },
            new() { Id = "battery", Title = "Batería", Text = GetBatteryText() }
        };
        if (shellConfiguration.InputLanguage.Enabled)
        {
            items.Insert(5, new SystemTrayItemConfiguration
            {
                Id = "input-language",
                Title = shellConfiguration.InputLanguage.Title,
                Text = "Consultando idioma...",
                Tooltip = "Cambiar idioma o distribución de teclado"
            });
        }
        return items;
    }

    private SystemTrayItemSnapshot CreateNotificationSnapshot()
    {
        var count = notificationCenter?.GetActive().Count ?? 0;
        return new SystemTrayItemSnapshot(
            "notifications",
            "Notificaciones",
            count == 1 ? "1 activa" : $"{count} activas",
            "Avisos de la sesión",
            string.Empty,
            "ok",
            "open",
            false,
            null);
    }

    private string GetNetworkText(NetworkSnapshot? currentSnapshot = null)
    {
        var snapshot = currentSnapshot ?? networkService.GetSnapshot();
        if (!string.IsNullOrWhiteSpace(snapshot.Error)) return "Estado no disponible";
        if (snapshot.Interfaces.Count == 0) return "Sin interfaces";

        return string.Join(", ", snapshot.Interfaces.Select(networkInterface =>
            networkInterface.Kind == NetworkInterfaceKind.Ethernet && networkInterface.IsOperational
                ? $"Cable: {FormatSpeed(networkInterface.SpeedBitsPerSecond)} · IP {FormatAddresses(networkInterface)}"
                : $"{GetInterfaceLabel(networkInterface.Kind)}: {(networkInterface.IsOperational ? "activa" : "inactiva")}"));
    }

    private static string FormatAddresses(NetworkInterfaceSnapshot networkInterface) =>
        networkInterface.IpAddresses.Count == 0 ? "sin IP" : string.Join(", ", networkInterface.IpAddresses);

    private static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "velocidad desconocida";
        var gigabits = bitsPerSecond / 1_000_000_000d;
        if (gigabits >= 1) return $"{gigabits:0.#} Gb/s";
        return $"{bitsPerSecond / 1_000_000d:0.#} Mb/s";
    }

    private int GetPopupHeight() => Math.Min(620, Math.Max(configuration.Height, 92 + state.Items.Count * RowHeight));

    private static string GetInterfaceLabel(NetworkInterfaceKind kind) => kind switch
    {
        NetworkInterfaceKind.Wifi => "Wi-Fi",
        NetworkInterfaceKind.Ethernet => "Ethernet",
        NetworkInterfaceKind.Vpn => "VPN",
        NetworkInterfaceKind.Loopback => "Loopback",
        _ => "Red"
    };

    private static string GetBatteryText()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status)) return "Estado no disponible";
        var percent = status.BatteryLifePercent == 255 ? "desconocida" : $"{status.BatteryLifePercent}%";
        return status.ACLineStatus == 1 ? $"{percent}, con corriente" : $"{percent}, batería";
    }

    private void EnsureWindow()
    {
        if (windowHandle != IntPtr.Zero) return;
        var windowClass = new NativeMethods.WindowClass
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
            WindowProcedure = windowProcedure,
            Instance = moduleHandle,
            ClassName = windowClassName
        };
        windowClassAtom = NativeMethods.RegisterClassEx(ref windowClass);
        if (windowClassAtom == 0) throw new InvalidOperationException("No se pudo registrar la clase de la bandeja propia.");

        windowHandle = NativeMethods.CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, windowClassName,
            "TenchyShell — Bandeja propia", WS_POPUP | WS_BORDER, 0, 0,
            configuration.Width, configuration.Height, IntPtr.Zero, IntPtr.Zero, moduleHandle, IntPtr.Zero);
        if (windowHandle == IntPtr.Zero)
        {
            NativeMethods.UnregisterClass(windowClassName, moduleHandle);
            windowClassAtom = 0;
            throw new InvalidOperationException("No se pudo crear la ventana de la bandeja propia.");
        }
    }

    private NativeMethods.Rect GetPrimaryWorkArea()
    {
        var monitor = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };

        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return desktopAreaPolicy.UseMonitorArea ? monitorInfo.Monitor : monitorInfo.Work;
        }

        return new NativeMethods.Rect
        {
            Left = 0,
            Top = 0,
            Right = NativeMethods.GetSystemMetrics(0),
            Bottom = NativeMethods.GetSystemMetrics(1)
        };
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WM_KEYDOWN)
        {
            DisableAutoHide();
            switch (wParam.ToInt32())
            {
                case NativeMethods.VK_ESCAPE: Hide(); break;
                case NativeMethods.VK_DOWN:
                case NativeMethods.VK_TAB: state.Move(); Invalidate(); break;
                case NativeMethods.VK_UP: state.Move(backwards: true); Invalidate(); break;
                case NativeMethods.VK_RETURN: ActivateSelected(); break;
            }
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_LBUTTONUP)
        {
            // Un clic continúa siendo una interacción de puntero: conservar el
            // autocierre de una bandeja abierta desde el dock, a diferencia de
            // la navegación por teclado que lo desactiva explícitamente.
            StopAutoHideTimer();
            var y = (short)((long)lParam >> 16);
            if (TryGetItemIndex(y, out var index))
            {
                state.TrySelect(index);
                ActivateSelected();
            }
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_MOUSEMOVE)
        {
            autoDismissState.PointerEntered();
            StopAutoHideTimer();
            TrackPointerLeave();
            var y = (short)((long)lParam >> 16);
            if (TryGetItemIndex(y, out var index) && state.TrySelect(index))
            {
                Invalidate();
            }
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_MOUSELEAVE)
        {
            autoDismissState.PointerLeft();
            UpdateAutoHideTimer();
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_TIMER && (nuint)wParam == AutoHideTimerId)
        {
            StopAutoHideTimer();
            if (autoDismissState.TimerElapsed(IsPointerOverTray()))
            {
                benchmarkRecorder?.Record("system_tray_auto_hidden", new
                {
                    reason = "pointer_timeout",
                    timeoutMs = AutoHideTimeoutMilliseconds
                });
                Hide();
            }
            else
            {
                TrackPointerLeave();
            }
            return IntPtr.Zero;
        }
        if (message == NativeMethods.WM_PAINT) { Paint(hWnd); return IntPtr.Zero; }
        if (message == NativeMethods.WM_ERASEBKGND) return new IntPtr(1);
        if (message == NativeMethods.WM_CLOSE) { Hide(); return IntPtr.Zero; }
        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ActivateSelected()
    {
        var selected = state.SelectedItem;
        if (selected is null) return;

        if (selected.Id.Equals("wallpaper", StringComparison.OrdinalIgnoreCase))
        {
            wallpaperMenuOpen = true;
            RebuildState();
            Invalidate();
            return;
        }

        if (selected.Id.Equals("wallpaper-back", StringComparison.OrdinalIgnoreCase))
        {
            wallpaperMenuOpen = false;
            RebuildState();
            Invalidate();
            return;
        }

        if (selected.Id.Equals("input-language", StringComparison.OrdinalIgnoreCase))
        {
            inputLanguageMenuOpen = true;
            wallpaperMenuOpen = false;
            RefreshInputLanguages();
            RebuildState();
            Invalidate();
            return;
        }

        if (selected.Id.Equals("input-language-back", StringComparison.OrdinalIgnoreCase))
        {
            inputLanguageMenuOpen = false;
            RebuildState();
            Invalidate();
            return;
        }

        if (selected.Id.Equals("notifications", StringComparison.OrdinalIgnoreCase) && notificationCenter is not null)
        {
            notificationMenuOpen = true;
            inputLanguageMenuOpen = false;
            wallpaperMenuOpen = false;
            RebuildState();
            Invalidate();
            return;
        }

        if (selected.Id.Equals("notifications-back", StringComparison.OrdinalIgnoreCase))
        {
            notificationMenuOpen = false;
            RebuildState();
            Invalidate();
            return;
        }

        if (selected.Id.StartsWith("notification:", StringComparison.OrdinalIgnoreCase) && notificationCenter is not null)
        {
            notificationCenter.RequestDismiss(selected.Id["notification:".Length..]);
            return;
        }

        if (selected.Id.Equals("network", StringComparison.OrdinalIgnoreCase))
        {
            // La consulta propia no abre ms-settings ni bloquea el message loop.
            var networkItem = runtimeItems.FirstOrDefault(item => item.Snapshot.Id.Equals("network", StringComparison.OrdinalIgnoreCase));
            if (networkItem is not null) networkItem.Snapshot = networkItem.Snapshot with { Text = "Actualizando..." };
            _ = RefreshNetworkAsync();
            RebuildState();
            Invalidate();
            return;
        }

        Hide();

        switch (selected.Id)
        {
            case "terminal": actions.LaunchTerminal(); break;
            case "files": actions.LaunchFiles(); break;
            case "browser": actions.LaunchBrowser(); break;
            default:
                if (selected.Id.StartsWith("wallpaper:", StringComparison.OrdinalIgnoreCase))
                {
                    var path = selected.Id["wallpaper:".Length..];
                    var wallpaperResult = wallpaperService.Apply(path);
                    if (!wallpaperResult.Succeeded) logger.Error(wallpaperResult.Error ?? "No se pudo aplicar el fondo.");
                    break;
                }
                if (selected.Id.StartsWith("input-language:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = selected.Id["input-language:".Length..];
                    if (!long.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var handleValue))
                    {
                        logger.Error($"El identificador del idioma de teclado no es válido: {value}.");
                        break;
                    }

                    var result = inputLanguageService.Activate((IntPtr)handleValue, lastForegroundWindow);
                    if (!result.Succeeded)
                    {
                        logger.Error(result.Error ?? "No se pudo solicitar el cambio de idioma.");
                        break;
                    }

                    logger.Info($"Se solicitó el método de entrada {value} para la ventana activa anterior.");
                    _ = RefreshInputLanguageAfterActivationAsync();
                    break;
                }
                if (selected.Id.StartsWith("wifi:", StringComparison.OrdinalIgnoreCase))
                {
                    var ssid = selected.Id["wifi:".Length..];
                    var wifi = networkSnapshot.WifiNetworks.FirstOrDefault(item => item.Ssid.Equals(ssid, StringComparison.OrdinalIgnoreCase));
                    var wifiResult = wifi?.IsConnected == true
                        ? networkService.DisconnectWifi()
                        : networkService.ConnectWifi(ssid);
                    if (!wifiResult.Succeeded) logger.Error(wifiResult.Error ?? "No se pudo abrir la configuración Wi-Fi.");
                    break;
                }
                if (selected.Id.StartsWith("interface:", StringComparison.OrdinalIgnoreCase))
                {
                    var interfaceName = selected.Id["interface:".Length..];
                    var interfaceSnapshot = networkSnapshot.Interfaces.FirstOrDefault(item =>
                        item.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));
                    if (interfaceSnapshot is not null)
                    {
                        var result = networkService.SetInterfaceEnabled(interfaceName, !interfaceSnapshot.IsOperational);
                        if (!result.Succeeded) logger.Error(result.Error ?? "No se pudo cambiar el estado de la interfaz.");
                    }
                    break;
                }
                var action = runtimeItems.FirstOrDefault(item => item.Snapshot.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase))?.Snapshot.Action ?? "open";
                if (configuration.Actions.TryGetValue($"{selected.Id}.{action}", out var configuredAction))
                {
                    actions.LaunchApplication(configuredAction.Command, configuredAction.Arguments.ToArray());
                }
                break;
        }
    }

    private void Paint(IntPtr hWnd)
    {
        var deviceContext = NativeMethods.BeginPaint(hWnd, out var paintStruct);
        var background = NativeMethods.CreateSolidBrush(0x00252525);
        var rectangle = new NativeMethods.Rect { Right = configuration.Width, Bottom = GetPopupHeight() };
        NativeMethods.FillRect(deviceContext, ref rectangle, background);
        NativeMethods.DeleteObject(background);
        NativeMethods.SetBkMode(deviceContext, (int)NativeMethods.TRANSPARENT);
        NativeMethods.SetTextColor(deviceContext, 0x00FFFFFF);

        const string header = "Bandeja propia de TenchyShell";
        NativeMethods.TextOut(deviceContext, 20, 20, header, header.Length);
        var y = 60;
        if (state.SelectedItem is not null)
        {
            var selectedRow = new NativeMethods.Rect
            {
                Left = 8,
                Top = FirstRowTop + state.SelectedIndex * RowHeight,
                Right = configuration.Width - 8,
                Bottom = FirstRowTop + (state.SelectedIndex + 1) * RowHeight
            };
            var selectedBackground = NativeMethods.CreateSolidBrush(SelectedRowBackgroundColor);
            NativeMethods.FillRect(deviceContext, ref selectedRow, selectedBackground);
            NativeMethods.DeleteObject(selectedBackground);
        }
        foreach (var (item, index) in state.Items.Select((item, index) => (item, index)))
        {
            NativeMethods.SetTextColor(deviceContext, (uint)(index == state.SelectedIndex ? 0x0000D7FF : 0x00FFFFFF));
            var icon = runtimeItems.FirstOrDefault(runtime => runtime.Snapshot.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))?.Snapshot.Icon;
            var iconHandle = LoadIcon(icon);
            if (iconHandle != IntPtr.Zero)
            {
                NativeMethods.DrawIconEx(deviceContext, 20, y - 2, iconHandle, 20, 20, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
            }

            var prefix = iconHandle != IntPtr.Zero ? string.Empty : $"[{item.IconText}] ";
            var line = $"{(index == state.SelectedIndex ? "> " : "  ")}{prefix}{item.Title} — {item.Description}";
            NativeMethods.TextOut(deviceContext, iconHandle != IntPtr.Zero ? 48 : 20, y, line, line.Length);
            if (iconHandle != IntPtr.Zero) NativeMethods.DestroyIcon(iconHandle);
            y += RowHeight;
        }
        NativeMethods.SetTextColor(deviceContext, 0x00A0A0A0);
        const string footer = "↑/↓ o Tab navegar · Enter seleccionar · Escape cerrar";
        var footerY = Math.Min(GetPopupHeight() - 28, 52 + state.Items.Count * RowHeight + 8);
        NativeMethods.TextOut(deviceContext, 20, footerY, footer, footer.Length);
        NativeMethods.EndPaint(hWnd, ref paintStruct);
    }

    private bool TryGetItemIndex(int clientY, out int index)
    {
        index = -1;
        if (clientY < FirstRowTop) return false;

        var candidate = (clientY - FirstRowTop) / RowHeight;
        if (candidate < 0 || candidate >= state.Items.Count) return false;

        index = candidate;
        return true;
    }

    private void RebuildStateAndInvalidate() { RebuildState(); Invalidate(); }

    private void OnNotificationCenterChanged(object? sender, NotificationCenterChangedEventArgs args)
    {
        if (isDisposed) return;
        messageLoop.Post(() =>
        {
            if (isDisposed) return;
            var item = runtimeItems.FirstOrDefault(runtimeItem => runtimeItem.Snapshot.Id.Equals("notifications", StringComparison.OrdinalIgnoreCase));
            if (item is not null) item.Snapshot = CreateNotificationSnapshot();
            if (isVisible)
            {
                RebuildState();
                Invalidate();
            }
        });
    }

    private void DisableAutoHide()
    {
        autoDismissState.KeyboardInteraction();
        StopAutoHideTimer();
    }

    private void UpdateAutoHideTimer()
    {
        StopAutoHideTimer();
        if (!isVisible || !autoDismissState.IsTimerPending || windowHandle == IntPtr.Zero) return;

        if (NativeMethods.SetTimer(windowHandle, AutoHideTimerId, AutoHideTimeoutMilliseconds, IntPtr.Zero) == 0)
        {
            logger.Error($"No se pudo programar el autocierre de la bandeja: Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private void StopAutoHideTimer()
    {
        if (windowHandle != IntPtr.Zero) NativeMethods.KillTimer(windowHandle, AutoHideTimerId);
    }

    private void TrackPointerLeave()
    {
        if (!isVisible || !autoDismissState.IsEnabled || windowHandle == IntPtr.Zero || !IsPointerOverTray()) return;

        var eventData = new NativeMethods.TrackMouseEventData
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.TrackMouseEventData>(),
            Flags = NativeMethods.TME_LEAVE,
            WindowHandle = windowHandle
        };
        NativeMethods.TrackMouseEvent(ref eventData);
    }

    private bool IsPointerOverTray()
    {
        return windowHandle != IntPtr.Zero &&
               NativeMethods.GetCursorPos(out var point) &&
               NativeMethods.GetWindowRect(windowHandle, out var rectangle) &&
               point.X >= rectangle.Left && point.X < rectangle.Right &&
               point.Y >= rectangle.Top && point.Y < rectangle.Bottom;
    }

    private void Invalidate()
    {
        if (windowHandle != IntPtr.Zero) NativeMethods.InvalidateRect(windowHandle, IntPtr.Zero, true);
    }

    private async Task RefreshInputLanguageAfterActivationAsync()
    {
        await Task.Delay(150).ConfigureAwait(false);
        if (isDisposed) return;
        messageLoop.Post(() =>
        {
            if (isDisposed) return;
            RefreshInputLanguages();
            RebuildState();
            Invalidate();
        });
    }

    private static IntPtr LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
        {
            return IntPtr.Zero;
        }

        return NativeMethods.LoadImage(
            IntPtr.Zero,
            path,
            NativeMethods.IMAGE_ICON,
            20,
            20,
            NativeMethods.LR_LOADFROMFILE);
    }

    private sealed class RuntimeItem
    {
        public RuntimeItem(SystemTrayItemConfiguration configuration, SystemTrayItemSnapshot snapshot)
        {
            Configuration = configuration;
            Snapshot = snapshot;
        }

        public SystemTrayItemConfiguration Configuration { get; }
        public SystemTrayItemSnapshot Snapshot { get; set; }
        public Timer? Timer { get; set; }
        public CancellationTokenSource RefreshCancellation { get; set; } = new();
        public SemaphoreSlim RefreshGate { get; } = new(1, 1);
    }

    private static SystemTrayItemSnapshot CreateSnapshot(SystemTrayItemConfiguration item) =>
        new(item.Id, item.Title, item.Text, string.IsNullOrWhiteSpace(item.Tooltip) ? item.Title : item.Tooltip,
            item.Icon, "ok", "open", false, null);
}
