namespace NacreCraft.Pit.Tools;

//Shared progress component
//A run holds a task list: every tool registers the tasks it is about to do, reports how far each one got, and the
//renderer decides when to repaint. Tools never draw or clear the line themselves, which is what keeps downloads,
//child process output and diagnostics from landing on top of it.
//A run started inside another one, such as a run tool building the mod, only does the work: the outer task list owns
//the row, and the inner one reports through plain lines instead of fighting it.
public static class Progress
{
    //Switch every drawing tool answers, taken out of the command line once in Program
    public const string PlainOption = "--plain";

    //Whether the run prints one line per step instead of redrawing; set once from the command line
    public static bool Plain { get; set; }

    //How many runs are open, one meaning this one owns the row
    private static int _depth;

    //Whether the current run only does the work, while an outer one owns the row
    internal static bool Quiet => _depth > 1;

    //SetTotal fixes how many tasks this run has, the tasks are added one by one after it
    public static void SetTotal(int total)
    {
        if (Quiet)
            return;

        ProgressLine.SetTotal(total);
    }

    //Run a task list and return what the body returns
    public static T Run<T>(Func<ProgressSession, T> action)
    {
        if (_depth > 0)
        {
            _depth++;
            try
            {
                return action(new ProgressSession());
            }
            finally
            {
                _depth--;
            }
        }

        _depth = 1;
        try
        {
            return ProgressLine.Run(Plain, () => action(new ProgressSession()));
        }
        finally
        {
            _depth = 0;
        }
    }

    //Run a task list that reports through the line only
    public static void Run(Action<ProgressSession> action)
        => Run(session =>
        {
            action(session);
            return true;
        });

    //Enter starts a task without holding a handle, for work that cannot reach the session
    //A total of zero or less marks the task as having no step count, shown as a moving run and `??%`
    public static void Enter(string name, int total, string detail = "")
    {
        if (Quiet)
            return;

        ProgressLine.Enter(name, total, detail);
    }

    //Step advances the running task, detail naming what was just finished
    public static void Step(string detail = "")
    {
        if (Quiet)
            return;

        ProgressLine.Step(detail);
    }

    //Detail moves the running task's label without advancing, for work inside one step
    public static void Detail(string detail)
    {
        if (Quiet)
            return;

        ProgressLine.Detail(detail);
    }

    //Resize widens the running task once its real size is known
    public static void Resize(int total)
    {
        if (Quiet)
            return;

        ProgressLine.Resize(total);
    }

    //Complete closes the running task at full
    public static void Complete()
    {
        if (Quiet)
            return;

        ProgressLine.Complete();
    }

    //WriteLine prints a line without letting the running task swallow it
    public static void WriteLine(string message)
        => ProgressLine.WriteLine(message);

    //Break clears the running row so a caller can print its own line, the timer redraws afterwards
    public static void Break()
        => ProgressLine.Break();

    //Transfer reports a download in progress, naming the file in the running task
    //A nested run prints a line per file, since the row belongs to the outer task list
    public static void Transfer(string label, int done, int total, string name)
    {
        if (Quiet)
        {
            WriteLine($"Downloading {label} {done}/{total}: {name}");
            return;
        }

        ProgressLine.Transfer(label, done, total, name);
    }

    //Scanner turns a parse callback into task steps, the file being relative to the project root
    public static Action<int, int, string> Scanner(string root)
        => (done, total, file) =>
        {
            if (Quiet)
                return;

            ProgressLine.Resize(total);
            ProgressLine.Step(file.Length == 0 ? string.Empty : Path.GetRelativePath(root, file).Replace('\\', '/'));
        };
}

//One run's task list
//Adding a task closes the one before it, since only one row is ever drawn
public sealed class ProgressSession
{
    //When an outer run owns the row, the tasks handed out here do nothing
    private readonly bool _quiet;

    internal ProgressSession() => _quiet = Progress.Quiet;

    //Add registers a task; total is how many steps it takes and detail what it is on right now
    //A total of zero or less means the length is unknown, which draws the moving run and `??%` instead of a fill
    public ProgressTask Add(string name, int total, string detail = "")
    {
        if (_quiet)
            return new ProgressTask(quiet: true);

        ProgressLine.Enter(name, total, detail);
        return new ProgressTask(quiet: false);
    }
}

//One task of the list, every call moving the same running line
public sealed class ProgressTask
{
    private readonly bool _quiet;

    internal ProgressTask(bool quiet) => _quiet = quiet;

    //Step advances by one, detail naming what was just finished
    public void Step(string detail = "")
    {
        if (!_quiet)
            ProgressLine.Step(detail);
    }

    //Detail moves the label without advancing
    public void Detail(string detail)
    {
        if (!_quiet)
            ProgressLine.Detail(detail);
    }

    //Resize widens the task once its real size is known
    public void Resize(int total)
    {
        if (!_quiet)
            ProgressLine.Resize(total);
    }

    //Complete closes the task at full, what the caller does before printing result lines
    public void Complete()
    {
        if (!_quiet)
            ProgressLine.Complete();
    }
}
