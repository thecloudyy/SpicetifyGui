using System.Diagnostics;

namespace MarketplaceInstaller;

public static class Spotify
{
    public static bool IsRunning()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("tasklist", "/FI \"IMAGENAME eq Spotify.exe\" /NH")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                // Bounded read: a synchronous ReadToEnd would hang the caller
                // forever if tasklist ever wedges (this runs in poll loops).
                Task<string> read = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                read.Wait(1000);
                string out_ = read.IsCompletedSuccessfully ? read.Result : "";
                return out_.Contains("Spotify.exe", StringComparison.OrdinalIgnoreCase);
            }
            if (OperatingSystem.IsMacOS())
            {
                return Process.GetProcessesByName("Spotify").Length > 0;
            }
            return Process.GetProcessesByName("spotify").Length > 0
                || Process.GetProcessesByName("Spotify").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool Close(IProgress<string>? log, CancellationToken ct)
    {
        log?.Report("Closing Spotify...");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RunHidden("taskkill", "/IM Spotify.exe /F");
                RunHidden("taskkill", "/IM SpotifyWebHelper.exe /F");
            }
            else if (OperatingSystem.IsMacOS())
            {
                RunHidden("osascript", "-e 'quit app \"Spotify\"'");
            }
            else
            {
                RunHidden("pkill", "-f spotify");
            }
        }
        catch (Exception ex)
        {
            log?.Report("Could not close Spotify cleanly: " + ex.Message);
        }

        for (int i = 0; i < 20; i++)
        {
            if (ct.IsCancellationRequested) return false;
            if (!IsRunning())
            {
                log?.Report("Spotify closed.");
                return true;
            }
            Thread.Sleep(250);
        }

        log?.Report("Spotify still appears to be running.");
        return false;
    }

    private static void RunHidden(string file, string args)
    {
        try
        {
            // No pipe redirection: the output is never read, and an unread
            // pipe would stall the child if it ever filled up.
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
