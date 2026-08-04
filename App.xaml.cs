using System.IO;
using System.Windows;

namespace SpotifyTaskbarWidget;

public partial class App : Application
{
    private static Mutex? _mutex;

    /// <summary>True apenas quando o utilizador saiu de propósito (ou update);
    /// false quando a janela morre com o Explorer e deve ser recriada.</summary>
    public static bool IntentionalExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "SpotifyTaskbarWidget_SingleInstance", out bool isNew);
        if (!isNew)
        {
            IntentionalExit = true;
            Shutdown();
            return;
        }

        // A janela pode ser destruída por um reinício do Explorer (é owned pela
        // taskbar) e recriada — a app só termina quando o utilizador manda
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            // Escrita partilhada com o Diag: dedup de repetições (um timer em
            // erro dispara várias vezes por segundo) e teto de tamanho
            Diag.Log(args.Exception.ToString());
            args.Handled = true;
        };

        base.OnStartup(e);

        // Uma janela de widget por barra selecionada nas definições
        SpotifyTaskbarWidget.MainWindow.SyncToMonitors();

        // Se NENHUMA janela se conseguiu criar (crash no arranque numa certa
        // máquina), sair de forma limpa em vez de ficar um processo zombie —
        // sem UI nem ícone de bandeja — a segurar o mutex de instância única e
        // a impedir novas tentativas de abrir (crítica "flashes then does not
        // load"). O log fica com a exceção para diagnóstico.
        if (!SpotifyTaskbarWidget.MainWindow.HasWindows)
        {
            Diag.Log("No widget window could be created at startup — exiting so the single-instance mutex is released.");
            IntentionalExit = true;
            Shutdown();
        }
    }
}
