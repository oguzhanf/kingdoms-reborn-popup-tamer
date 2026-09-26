namespace KRPopupTamer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Mutex single;
        bool first;
        try
        {
            single = new Mutex(true, @"Local\KRPopupTamer", out first);
        }
        catch (UnauthorizedAccessException) // an elevated instance owns it
        {
            first = false;
            single = null!;
        }
        using (single)
        {
            if (!first)
            {
                MessageBox.Show("KR Popup Tamer is already running (see the tray icon).", "KR Popup Tamer");
                return;
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startMinimized: args.Contains("--minimized")));
        }
    }
}
