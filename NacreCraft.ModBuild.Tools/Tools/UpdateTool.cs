using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NacreCraft.ModBuild.Core;
//Trace clashes with System.Diagnostics.Trace, so name the project's own explicitly
using Trace = NacreCraft.ModBuild.Core.Trace;

namespace NacreCraft.ModBuild.Tools;

//UpdateTool checks nuget for a newer version, downloads the package and drops a script that updates ncm
//The script lives in the update cache under the user directory and installs from the downloaded package
//Overwriting a running assembly fails on Windows, so the script waits a second before starting
//ncm itself does not wait for the script, it exits so the files are free to install
internal static class UpdateTool
{
    private const string PackageId = "NacreCraft.ModBuild.Tools";

    //VersionIndex is the version list from the flat container endpoint
    private static readonly string VersionIndex =
        $"https://api.nuget.org/v3-flatcontainer/{PackageId.ToLowerInvariant()}/index.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static void Register()
        => ToolRegistry.Register("update", "Check nuget.org and update ncm to the latest version", Run);

    //Run checks the version, downloads the package and starts the script when newer, or reports it is up to date
    private static int Run(string[] args)
    {
        var current = CurrentVersion();
        var latest = LatestVersion();

        if (latest is null)
        {
            Console.WriteLine("Could not read the version list from nuget.org");
            return 1;
        }

        if (Compare(latest, current) <= 0)
        {
            Console.WriteLine($"ncm {current} is already the latest");
            return 0;
        }

        //The update replaces files in the install directory, which breaks running instances, so ask first
        if (!ConfirmUnlocked())
        {
            Console.WriteLine("Update cancelled");
            return 1;
        }

        //Download the package first so the script can install from it instead of fetching again
        if (Download(latest) is null)
            return 1;

        var script = ReleaseScript(latest);
        Console.WriteLine($"Updating ncm {current} -> {latest}");
        Console.WriteLine($"Started {script}, this process exits now");
        Launch(script);
        return 0;
    }

    //ConfirmUnlocked checks whether another process still holds ncm before updating
    //If so it asks whether to force it, with an empty or unavailable answer meaning no
    private static bool ConfirmUnlocked()
    {
        var executable = NcmExecutable();
        var lockers = Lockers();

        if (lockers.Count == 0 && !IsLocked(executable))
            return true;

        if (executable is not null)
            Console.WriteLine($"{executable} is in use" + (lockers.Count == 0
                ? string.Empty
                : $", held by process(es) {string.Join(", ", lockers)}"));

        return Prompt.Confirm(
            "Some processes are still locking the ncm tool, continuing the update may cause errors. Continue?",
            defaultYes: false,
            warn: true);
    }

    //NcmExecutable is the ncm executable
    //The dotnet tool shim lives in .dotnet/tools under the user profile, named after the command
    //It falls back to the current process image when the shim is missing
    private static string? NcmExecutable()
    {
        var shim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools", OperatingSystem.IsWindows() ? "ncm.exe" : "ncm");
        return File.Exists(shim) ? shim : Environment.ProcessPath;
    }

    //Lockers are the other live ncm processes, excluding the current one
    private static List<int> Lockers()
    {
        var self = Environment.ProcessId;
        var lockers = new List<int>();
        foreach (var process in Process.GetProcessesByName("ncm"))
        {
            if (process.Id != self)
                lockers.Add(process.Id);
            process.Dispose();
        }
        return lockers;
    }

    //IsLocked reports a holder when even an exclusive open of the executable fails
    //Linux has no mandatory locking, so this only covers the cases Windows process names miss
    private static bool IsLocked(string? executable)
    {
        if (executable is null || !File.Exists(executable))
            return false;

        try
        {
            using var stream = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            Trace.Log($"{executable} is locked");
            return true;
        }
    }

    //Download fetches the new package into the update cache, reusing it if present, and returns its path or null
    private static string? Download(string version)
    {
        Directory.CreateDirectory(CacheLayout.Update);
        var target = Path.Combine(CacheLayout.Update, $"{PackageId}.{version}.nupkg");
        if (File.Exists(target))
        {
            Trace.Log($"update package is already downloaded {target}");
            return target;
        }

        //The flat container path lowercases the package segment and uses the version as published on nuget
        var name = PackageId.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{name}/{version.ToLowerInvariant()}/{name}.{version.ToLowerInvariant()}.nupkg";

        try
        {
            var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(target, bytes);
            Console.WriteLine($"Downloaded {Path.GetFileName(target)} ({bytes.Length / 1024} KB)");
            return target;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"error: cannot download {url}: {exception.Message}");
            return null;
        }
    }

    //CurrentVersion is the assembly version with the SourceLink commit suffix after + stripped
    private static string CurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

    //LatestVersion picks the newest stable version on nuget, or null when unavailable
    //Only versions without a prerelease tag count, matching dotnet tool install without --prerelease
    private static string? LatestVersion()
    {
        try
        {
            var json = Http.GetStringAsync(VersionIndex).GetAwaiter().GetResult();
            var versions = JsonDocument.Parse(json).RootElement.GetProperty("versions");

            string? latest = null;
            foreach (var item in versions.EnumerateArray())
            {
                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Contains('-'))
                    continue;
                if (latest is null || Compare(value, latest) > 0)
                    latest = value;
            }
            return latest;
        }
        catch (Exception exception)
        {
            Trace.Log($"failed to read the version list: {exception.Message}");
            return null;
        }
    }

    //ReleaseScript writes an update script into the update cache, with different commands per platform
    //It installs with dotnet tool update from the downloaded package, pinning both version and source
    //The first line delays so dotnet can touch the install directory once ncm has exited
    private static string ReleaseScript(string version)
    {
        var windows = OperatingSystem.IsWindows();
        Directory.CreateDirectory(CacheLayout.Update);
        var path = Path.Combine(CacheLayout.Update, windows ? "ncm-update.cmd" : "ncm-update.sh");

        var lines = new[]
        {
            //Use ping instead of timeout on Windows, since timeout exits immediately when input is redirected
            //The delay waits for ncm to exit and release the files in the install directory
            windows ? "ping -n 2 127.0.0.1 >nul" : "sleep 1",
            $"dotnet tool update -g {PackageId} --version {version} --add-source \"{CacheLayout.Update}\"",
        };

        File.WriteAllLines(path, lines);
        if (!windows)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Trace.Log($"update script written to {path}");
        return path;
    }

    //Launch starts the script without waiting so ncm can exit right away
    //The working directory is the script's folder and only the file name is given, avoiding quoting issues when the user directory has spaces
    private static void Launch(string script)
    {
        var windows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = windows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false,
        };
        //ncm prints in English, so keep the child process from following the system language
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        if (windows)
            startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(Path.GetFileName(script));

        Process.Start(startInfo);
    }

    //Compare orders two versions and returns -1, 0 or 1
    //It compares the numeric segments with missing ones as 0, and a prerelease tag sorts lower on equal numbers
    private static int Compare(string left, string right)
    {
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);

        var core = CompareParts(leftCore, rightCore);
        if (core != 0)
            return core;

        if (leftPre.Length == 0 && rightPre.Length == 0)
            return 0;
        if (leftPre.Length == 0)
            return 1;
        if (rightPre.Length == 0)
            return -1;
        return string.CompareOrdinal(leftPre, rightPre);
    }

    //Split cuts a version into its numeric core and prerelease tag, dropping anything after +
    private static (string Core, string Pre) Split(string version)
    {
        var text = version;
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        var dash = text.IndexOf('-');
        return dash >= 0 ? (text[..dash], text[(dash + 1)..]) : (text, string.Empty);
    }

    private static int CompareParts(string left, string right)
    {
        var a = ParseParts(left);
        var b = ParseParts(right);
        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y)
                return x < y ? -1 : 1;
        }
        return 0;
    }

    //ParseParts reads the numeric segments and stops at the first non-numeric one
    private static List<int> ParseParts(string text)
    {
        var parts = new List<int>();
        foreach (var segment in text.Split('.'))
        {
            if (!int.TryParse(segment, out var value))
                break;
            parts.Add(value);
        }
        return parts;
    }
}
