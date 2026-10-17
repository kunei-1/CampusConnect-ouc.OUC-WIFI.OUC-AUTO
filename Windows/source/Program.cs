namespace CampusConnect;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var startInTray = args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
        StartupTiming.Enabled = args.Contains("--timing", StringComparer.OrdinalIgnoreCase);
        StartupTiming.Mark(startInTray ? "process started (logon launch)" : "process started (manual launch)");
        using var instance = new Mutex(true, "Local\\CampusConnect.Windows", out var created);
        if (!created) { if (!startInTray) MessageBox.Show("校园网助手已在运行，请从系统托盘打开。", "校园网助手"); return; }
        // The .NET bundle extracts native libraries into its private cache.
        // Tell WebView2 where its loader is instead of relying on Assembly.Location.
        var directories = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Prepend(AppContext.BaseDirectory);
        var loaderDirectory = directories.FirstOrDefault(dir => File.Exists(Path.Combine(dir, "WebView2Loader.dll")));
        if (loaderDirectory is not null)
            Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(loaderDirectory);
        ApplicationConfiguration.Initialize();
        StartupTiming.Mark("WinForms initialized");
        // The login page needs the browser runtime, which is the slowest part of a logon
        // start. Begin loading it now instead of after the window and profile are ready.
        var warmBrowser = Task.Run(MainForm.PrewarmBrowserAsync);
        var verificationIndex = Array.IndexOf(args, "--verify-ui");
        var verificationPath = verificationIndex >= 0 && verificationIndex + 1 < args.Length ? args[verificationIndex + 1] : null;
        Application.Run(new MainForm(verificationPath, startInTray, warmBrowser));
    }
}
