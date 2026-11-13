using System.Windows.Forms;
using CouchControl;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

// Run the tray app — this blocks until the user quits
using var app = new TrayApp();
app.Run();
