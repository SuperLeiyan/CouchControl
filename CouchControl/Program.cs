using System.Windows.Forms;
using CouchControl;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

TrayApp? app = null;
int cleanedUp = 0;

// On a crash, still release held keys/buttons, stop rumble and disconnect the virtual pad.
// (A kill from Task Manager can't be caught; the next start recovers: HidHide entries are
// re-adopted from the state file and rumble is reset when a controller is picked up.)
void CleanUpAfterCrash(string source, Exception? ex)
{
    AppLogger.Log($"CRASH ({source}): {ex}");
    if (Interlocked.Exchange(ref cleanedUp, 1) == 1) return;
    try { app?.Dispose(); } catch (Exception e) { AppLogger.Log($"Cleanup after crash failed: {e.Message}"); }
}

Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += (_, e) =>
{
    CleanUpAfterCrash("UI thread", e.Exception);
    Application.Exit();
};
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    CleanUpAfterCrash("background thread", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    AppLogger.Log($"Unobserved task error: {e.Exception.GetBaseException().Message}");
    e.SetObserved();
};

// Run the tray app — this blocks until the user quits
app = new TrayApp();
try
{
    app.Run();
}
finally
{
    if (Interlocked.Exchange(ref cleanedUp, 1) == 0)
        app.Dispose();
}
