using System.Diagnostics;

namespace MarketplaceInstaller;

/// <summary>
/// Locates the Spicetify CLI and installs it when missing, per
/// spicetify.app/docs/getting-started: the official install script puts
/// spicetify.exe in %LOCALAPPDATA%\spicetify and registers it in the user PATH;
/// winget (Spicetify.Spicetify) is the documented alternative and is used as
/// a fallback. Everything is per-user — no admin rights required.
/// </summary>
public static class SpicetifyCli
{
    /// <summary>%LOCALAPPDATA%\spicetify — where the current official install
    /// script puts the CLI (it adds this folder to the user PATH).</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "spicetify");

    /// <summary>%APPDATA%\spicetify — where older installers put the CLI; the
    /// folder now doubles as spicetify's user-data dir, so keep it as a candidate.</summary>
    public static string LegacyInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "spicetify");

    public static string ExePath => Path.Combine(InstallDir, "spicetify.exe");

    public static string LegacyExePath => Path.Combine(LegacyInstallDir, "spicetify.exe");

    private const string InstallScriptUrl =
        "https://raw.githubusercontent.com/spicetify/cli/main/install.ps1";

    private static string? _resolved;

    /// <summary>Full path to a usable spicetify executable, or null when not installed.</summary>
    public static string? FindExecutable()
    {
        if (_resolved is string cached && File.Exists(cached)) return cached;
        try
        {
            // 1. Official install-script location — survives a PATH inherited at
            //    app start that predates the install.
            if (File.Exists(ExePath)) return _resolved = ExePath;
            if (File.Exists(LegacyExePath)) return _resolved = LegacyExePath;

            // 2. Current PATH (covers winget / scoop / choco installs).
            string? onPath = Which("spicetify");
            if (onPath != null) return _resolved = onPath;

            // 3. User PATH read fresh from the registry — covers a CLI installed
            //    while this app was already running (the environment block of a
            //    running process never sees that update).
            foreach (string dir in UserPathDirs())
            {
                string candidate = Path.Combine(dir, "spicetify.exe");
                if (File.Exists(candidate)) return _resolved = candidate;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Drop the cached path so the next lookup rescans (after an install).</summary>
    public static void InvalidateCache() => _resolved = null;

    public static bool IsInstalled() => TryGetVersion(out _);

    /// <summary>Runs `spicetify -v` and returns its trimmed output.</summary>
    public static string Version()
    {
        var (code, stdout) = RunVersion();
        string v = StripAnsi(stdout).Trim();
        return code == 0 && v != "" ? v : "unknown";
    }

    public static bool TryGetVersion(out string version)
    {
        var (code, stdout) = RunVersion();
        version = StripAnsi(stdout).Trim();
        return code == 0 && version != "";
    }

    private static (int code, string stdout) RunVersion()
    {
        string exe = FindExecutable() ?? "spicetify";
        try
        {
            var psi = new ProcessStartInfo(exe, "-v")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (1, "");
            // Async drain first: a synchronous ReadToEnd would block forever if
            // the CLI wedges, making the timeout below unreachable.
            Task<string> read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return (1, "spicetify -v timed out.");
            }
            read.Wait(2000);
            return (p.ExitCode, read.IsCompletedSuccessfully ? read.Result : "");
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static string StripAnsi(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*m", "");

    /// <summary>
    /// Installs the Spicetify CLI when it is missing. Tries the official
    /// install script first (documented method, per-user, no UAC), then winget.
    /// Returns true when a working CLI is present afterwards.
    /// </summary>
    public static async Task<bool> InstallAsync(IProgress<string>? log, CancellationToken ct)
    {
        Say(log, "Spicetify CLI not found — installing it now.");
        InvalidateCache();

        if (OperatingSystem.IsWindows())
        {
            Say(log, "Running the official Spicetify install script...");
            if (await RunPowerShellAsync(log, ct) && VerifyInstalled(log))
                return true;

            Say(log, "Official script did not yield a working CLI — trying winget...");
            if (await RunWingetAsync(log, ct) && VerifyInstalled(log))
                return true;
        }
        else
        {
            Say(log, "Automatic CLI install is only supported on Windows.");
            Say(log, "Install manually: curl -fsSL " + InstallScriptUrl.Replace(".ps1", ".sh") + " | sh");
        }

        Say(log, "Spicetify CLI install failed — see https://spicetify.app/docs/getting-started.");
        return false;
    }

    private static bool VerifyInstalled(IProgress<string>? log)
    {
        InvalidateCache();
        bool ok = TryGetVersion(out string v);
        if (ok) Say(log, $"Spicetify CLI installed ({v}).");
        return ok;
    }

    /// <summary>`iwr -useb &lt;script&gt; | iex` in a hidden PowerShell — the exact command the docs give.</summary>
    private static async Task<bool> RunPowerShellAsync(IProgress<string>? log, CancellationToken ct)
    {
        string script = $"iwr -useb '{InstallScriptUrl}' | iex";
        var (code, output) = await Task.Run(() => RunHiddenAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            TimeSpan.FromMinutes(5), log, ct), ct);
        return code == 0;
    }

    /// <summary>`winget install Spicetify.Spicetify`, the package-manager alternative from the docs.</summary>
    private static async Task<bool> RunWingetAsync(IProgress<string>? log, CancellationToken ct)
    {
        var (availCode, _) = await Task.Run(() => RunHiddenAsync(
            "winget", "--version", TimeSpan.FromSeconds(30), log, ct), ct);
        if (availCode != 0)
        {
            Say(log, "winget is not available.");
            return false;
        }
        var (code, _) = await Task.Run(() => RunHiddenAsync("winget",
            "install --id Spicetify.Spicetify -e --silent " +
            "--accept-source-agreements --accept-package-agreements --disable-interactivity",
            TimeSpan.FromMinutes(10), log, ct), ct);
        return code == 0;
    }

    private static (int code, string output) RunHiddenAsync(string file, string args,
        TimeSpan timeout, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (1, "");
            using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
            // Drain both pipes before waiting: reading stdout to completion first
            // deadlocks the moment chatty stderr (winget progress) fills its pipe.
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(); } catch { }
                Say(log, "Installer timed out.");
                return (1, outTask.IsCompletedSuccessfully ? outTask.Result : "");
            }
            try { Task.WaitAll(new Task[] { outTask, errTask }, 5000); } catch { }
            ct.ThrowIfCancellationRequested();
            string stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";
            foreach (string line in (stdout + "\n" + stderr).Split('\n'))
            {
                string t = StripAnsi(line).Trim();
                if (t != "" && !t.StartsWith("   ") && t != "") Say(log, t);
            }
            return (p.ExitCode, stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (1, ex.Message);
        }
    }

    private static string? Which(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> UserPathDirs()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string? raw = Microsoft.Win32.Registry.GetValue(
            "HKEY_CURRENT_USER\\Environment", "Path", null) as string;
        if (string.IsNullOrEmpty(raw)) yield break;
        foreach (string dir in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim().Trim('"');
            if (trimmed != "") yield return Environment.ExpandEnvironmentVariables(trimmed);
        }
    }

    private static void Say(IProgress<string>? log, string msg)
    {
        try { log?.Report(msg); } catch { }
    }
}
