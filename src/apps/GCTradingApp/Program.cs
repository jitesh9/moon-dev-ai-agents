/*
 * GC Trading Application - Gold Futures Divergence Strategy
 * Implements Aggressive and Conservative GC strategies via IBKR TWS API
 * Author: Moon Dev AI
 * Date: November 2025
 */

namespace GCTradingApp;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Set up global exception handlers
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += Application_ThreadException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        Logger.Info("=== GC Trading Application Starting ===");
        Logger.Info($"Application directory: {AppDomain.CurrentDomain.BaseDirectory}");
        Logger.Info($"Log file: {Logger.GetLogFilePath()}");

        try
        {
            ApplicationConfiguration.Initialize();
            Logger.Info("ApplicationConfiguration initialized");

            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error in Main()", ex);
            MessageBox.Show($"Fatal error: {ex.Message}\n\nCheck log file:\n{Logger.GetLogFilePath()}",
                "Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Logger.Info("=== GC Trading Application Exiting ===");
        }
    }

    private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI thread exception", e.Exception);
        MessageBox.Show($"Unhandled error: {e.Exception.Message}\n\nCheck log file:\n{Logger.GetLogFilePath()}",
            "Application Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        if (ex != null)
        {
            Logger.Error("Unhandled domain exception", ex);
        }
        else
        {
            Logger.Error($"Unhandled domain exception (non-Exception): {e.ExceptionObject}");
        }
    }
}
