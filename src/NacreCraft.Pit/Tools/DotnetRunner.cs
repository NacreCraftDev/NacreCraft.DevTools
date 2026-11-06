using System.Diagnostics;

namespace NacreCraft.Pit.Tools;

//DotnetRunner runs a dotnet cli command with its output kept out of the progress line
//Plain mode lets the output through as it arrives, which is what a log wants; otherwise it is printed only on failure
internal static class DotnetRunner
{
    private const int TailLines = 40;

    //EngineVariables the environment switches msbuild resolves its own location from; the tool sets them for its
    //in-process engine and they must not leak into a child build
    private static readonly string[] EngineVariables =
    [
        "MSBuildSDKsPath",
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBuildEnableWorkloadResolver",
    ];

    public static bool Run(string directory, IReadOnlyList<string> arguments, bool live, Action? beforeOutput,
        out int exitCode)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = !live,
            RedirectStandardError = !live,
        };
        //pit output is English only, keep the child process from following the system language
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        //The in-process engine points these at the tool's bundled copy, which would make the child build load the
        //wrong targets and fail to start its build server, so the child is left to find its own sdk
        foreach (var name in EngineVariables)
            startInfo.Environment.Remove(name);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.WriteLine("error: could not start dotnet");
            exitCode = -1;
            return false;
        }

        if (live)
        {
            process.WaitForExit();
            exitCode = process.ExitCode;
            return exitCode == 0;
        }

        var lines = new List<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (lines)
                    lines.Add(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (lines)
                    lines.Add(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        exitCode = process.ExitCode;
        if (exitCode == 0)
            return true;

        //the output explains the failure, so the tail is printed once the progress line is out of the way
        List<string> tail;
        lock (lines)
            tail = lines.TakeLast(TailLines).ToList();

        beforeOutput?.Invoke();
        foreach (var line in tail)
            Console.WriteLine(line);
        return false;
    }
}
