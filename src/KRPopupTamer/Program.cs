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
            if (!first && args.Contains(Updater.AfterUpdateArgument)) // the old version is still exiting
            {
                try { first = single.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (AbandonedMutexException) { first = true; }
            }
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
            Updater.DeleteLeftovers();
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startMinimized: args.Contains("--minimized")));
        }
    }
}
