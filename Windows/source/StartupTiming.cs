using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CampusConnect;

// Startup timing helps explain why "connect as Windows starts" can take a minute.
// Only stage names, durations and the campus SSID are written: never credentials,
// never page content. OFF disables it entirely; the file is only created on demand.
internal static class StartupTiming
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static DateTimeOffset bootAt;
    private static bool started;

    public static bool Enabled { get; set; }
    public static string FilePath => Path.Combine(LocalStore.DirectoryPath, "startup-timing.txt");

    public static void Mark(string stage)
    {
        if (!Enabled) return;
        try {
            lock (Gate) {
                Directory.CreateDirectory(LocalStore.DirectoryPath);
                if (!started) {
                    started = true;
                    var created = ProcessStartTime() ?? DateTimeOffset.Now;
                    bootAt = created.AddMilliseconds(-(double)GetTickCount64());
                    File.AppendAllText(FilePath, new StringBuilder()
                        .AppendLine()
                        .AppendLine($"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} ===")
                        .AppendLine($"process started {created:HH:mm:ss.fff}, {(created - bootAt).TotalSeconds:N1}s after Windows boot")
                        .ToString(), Encoding.UTF8);
                }
                var sinceBoot = (DateTimeOffset.Now - bootAt).TotalSeconds;
                File.AppendAllText(FilePath, $"{Clock.ElapsedMilliseconds,7} ms  T+{sinceBoot,7:N1}s  {stage}{Environment.NewLine}", Encoding.UTF8);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }


    private static DateTimeOffset? ProcessStartTime()
    {
        try {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime);
        } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
