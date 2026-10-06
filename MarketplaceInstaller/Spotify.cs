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
                string out_ = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
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
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
