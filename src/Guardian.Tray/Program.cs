namespace Guardian.Tray;

internal static class Program
{
    /// <summary>Per-session mutex: one tray per interactive session (the service and the Run key may both start it).</summary>
    private const string MutexName = @"Local\GuardianTray";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew) return;   // another instance already owns this session's tray

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        try
        {
            using var context = new TrayAppContext();
            Application.Run(context);
        }
        catch (Exception ex)
        {
            Log.Error("Tray crashed", ex);
            throw;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}
