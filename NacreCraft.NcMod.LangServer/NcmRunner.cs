using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NacreCraft.NcMod.LangServer;

//NcmRunner runs the command line tool for the editor
//The buttons send a request instead of starting anything themselves, which keeps the process, its output and the
//interrupt that stops it in one place: Ctrl+C is a console event, and raising one needs code that can attach to a console
internal sealed class NcmRunner(
    Action<int, string> log,
    Action<string, string> progress,
    Action<string, bool, int, string> finished,
    Action<IReadOnlyList<string>> state)
{
    //Actions the editor may start, everything else is refused
    private static readonly string[] Actions = ["build", "restore", "runclient", "runserver", "clean"];

    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);

    private sealed class Running
    {
        public required Process Process { get; init; }

        public required StringBuilder Output { get; init; }
    }

    static NcmRunner()
    {
        //the interrupt below attaches this process to the target console, and the event then reaches every process on
        //it, this one included; cancelling the default is what keeps the language server alive
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
    }

    //Start launches one action in the project root, returning false when it is already running or unknown
    public bool Start(string root, string action)
    {
        if (!Actions.Contains(action, StringComparer.Ordinal))
            return false;

        return StartCore(root, action, action, action is "runclient" or "runserver");
    }

    //StartTask runs a project task; the running key is prefixed so a task named after a built-in tool cannot collide
    //with the launch of that tool, and the page sees the task name in the finished notification
    public bool StartTask(string root, string name)
        => StartCore(root, "task:" + name, name, launch: false);

    //StartCore starts ncm with one command and tracks it under the given key
    private bool StartCore(string root, string key, string command, bool launch)
    {
        lock (_running)
        {
            if (_running.ContainsKey(key))
                return false;
        }

        var start = new ProcessStartInfo("ncm")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            //a launch has to own a console for Ctrl+C to reach it; the window is hidden so nothing flashes on screen
            CreateNoWindow = !launch,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add(command);

        var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var running = new Running { Process = process, Output = output };

        process.OutputDataReceived += (_, e) => Line(key, output, e.Data);
        process.ErrorDataReceived += (_, e) => Line(key, output, e.Data);

        try
        {
            if (!process.Start())
                return false;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log(1, $"cannot start ncm: {e.Message}");
            return false;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_running)
        {
            _running[key] = running;
            state(_running.Keys.ToList());
        }

        //the exit is polled rather than taken from the Exited event: the restarted server inherits the output pipes, and the
        //event handler below would then wait on pipes that outlive the tracked process, leaving the button on stop
        _ = Task.Run(() => Watch(key, running));
        return true;
    }

    //Watch reports the result as soon as the tracked process is gone, independent of its output pipes
    private void Watch(string action, Running running)
    {
        while (!running.Process.HasExited)
            Thread.Sleep(200);

        Complete(action, running.Process, running.Output);
    }

    //Stop asks a launch to shut down the way a terminal does
    public bool Stop(string action)
    {
        Running? running;
        lock (_running)
        {
            _running.TryGetValue(action, out running);
        }

        if (running is null)
        {
            log(2, $"no {action} process is running");
            return false;
        }

        if (running.Process.HasExited)
        {
            log(2, $"the {action} process already exited");
            return false;
        }

        log(3, $"sending Ctrl+C to the {action} process {running.Process.Id}");
        if (!Interrupt(running.Process))
            log(2, "the interrupt was not taken, the process keeps running");

        return true;
    }

    //StopAll ends everything still running, which the shutdown path uses so no process outlives the editor session
    public void StopAll()
    {
        List<Running> all;
        lock (_running)
        {
            all = _running.Values.ToList();
        }

        foreach (var running in all)
            End(running.Process);
    }

    //End kills the whole tree, since the kernel restarts the process once and the real server or client is its child
    private void End(Process process)
    {
        try
        {
            if (process.HasExited)
                return;

            log(2, "ending the process tree");
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log(2, $"cannot end the process: {e.Message}");
        }
    }

    //Line forwards one line of tool output to the editor log and to the progress notification
    private void Line(string action, StringBuilder output, string? line)
    {
        if (line is null)
            return;

        lock (output)
            output.AppendLine(line);

        log(3, line);
        progress(action, line);
    }

    //Complete reports the result once the process is gone, draining what the readers already received
    private void Complete(string action, Process process, StringBuilder output)
    {
        //bounded, since a pipe held open somewhere else must not keep the command looking like it still runs
        process.WaitForExit(500);

        if (!process.HasExited)
            return;

        var code = process.ExitCode;
        string tail;
        lock (output)
            tail = Tail(output.ToString());

        lock (_running)
        {
            if (!_running.Remove(action))
                return;

            state(_running.Keys.ToList());
        }

        process.Dispose();
        finished(action, code == 0, code, tail);
    }

    //Tail keeps the end of the output, which is what a failure dialog shows and what the clean size is read from
    private static string Tail(string text)
    {
        const int limit = 8000;
        return text.Length <= limit ? text : text[^limit..];
    }

    //Interrupt delivers Ctrl+C to the process, falling back to Ctrl+Break
    //Windows has no signal to send, so this attaches to the target console and raises the event there; other systems
    //take the usual SIGINT, which the kernel handles the same way
    //A console process can have Ctrl+C turned off, and then only Ctrl+Break reaches it; .NET reports both through the
    //same CancelKeyPress handler, so the fallback stops the server the same way
    private bool Interrupt(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return Signal(process.Id);

        var sent = Raise(process, CtrlCEvent);

        //the shutdown takes a moment to show, so the fallback only fires when the process is clearly still up
        if (!process.WaitForExit(700))
        {
            log(3, "Ctrl+C was not answered, sending Ctrl+Break");
            sent |= Raise(process, CtrlBreakEvent);
        }

        return sent;
    }

    //Raise attaches to the console of the process and generates one control event there
    private bool Raise(Process process, uint ctrlEvent)
    {
        FreeConsole();
        var attached = AttachConsole((uint)process.Id);
        var attachError = Marshal.GetLastWin32Error();

        try
        {
            //AttachConsole resets the control handlers of the calling process, and the ignore flag only covers Ctrl+C:
            //a Ctrl+Break would reach the language server with no handler and terminate it, taking the pending request down
            //A handler that answers every event replaces both the reset table and the ignore flag
            SetConsoleCtrlHandler(KeepAlive, true);

            var generated = GenerateConsoleCtrlEvent(ctrlEvent, 0);
            var generateError = Marshal.GetLastWin32Error();

            //the event is delivered on another thread, so the handler stays installed until it has been handled;
            //leaving the console first would reset the table while the event is still in flight
            Thread.Sleep(150);

            //the console list says whether the restarted server really sits on the same console as the tracked process
            var pids = new uint[64];
            var count = GetConsoleProcessList(pids, (uint)pids.Length);
            log(3, $"interrupt event={ctrlEvent} attached={attached} ({attachError}), generated={generated} "
                + $"({generateError}), console=[{string.Join(",", pids.Take((int)count))}] target={process.Id}");
            return generated;
        }
        finally
        {
            FreeConsole();
        }
    }

    //Signal sends SIGINT through kill, since .NET exposes no signal API beyond terminating a process
    private static bool Signal(int pid)
    {
        try
        {
            var start = new ProcessStartInfo("kill")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("-INT");
            start.ArgumentList.Add(pid.ToString());

            using var kill = Process.Start(start);
            if (kill is null)
                return false;

            kill.WaitForExit(2000);
            return kill.HasExited && kill.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private const uint CtrlCEvent = 0;

    private const uint CtrlBreakEvent = 1;

    //KeepAlive answers every control event so the language server never takes the interrupt it raises for the target
    //kept in a field since the native side holds only a pointer and the delegate must not be collected
    private static readonly ConsoleCtrlHandler KeepAlive = _ => true;

    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
}
