using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace TenchyShell.NotificationTestSender;

/// <summary>Emisor MSIX separado, exclusivo de las pruebas de integración del bridge.</summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var document = new XmlDocument();
        document.LoadXml("""
            <toast><visual><binding template="ToastGeneric">
              <text>Aplicación de prueba · TenchyShell</text>
              <text>Esta Toast procede de una identidad MSIX independiente. TenchyShell debe recibirla mediante el bridge, mostrar una tarjeta y guardarla en el historial.</text>
            </binding></visual></toast>
            """);
        ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(document));
        return 0;
    }
}
