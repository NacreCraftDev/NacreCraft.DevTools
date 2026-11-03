namespace NacreCraft.ModBuild.Tools;

//ProgressLine draws one line per stage: the stage counter, a dot spinner, the bar and the current target
//It writes that line itself instead of using a live console, because the tools print their own lines while they work
//and a live renderer and a plain WriteLine fight over the same row
//A stage registered without a step count has no percentage to show, so its bar carries a run of hashes sliding one cell
//per frame and wrapping around the end, which is what tells the reader the work is running rather than stuck at zero
//Plain mode prints one line per step instead, which is what a log or a CI run wants
internal static class ProgressLine
{
    private const int BarWidth = 20;

    //IndeterminateBlock how much of the bar the moving run covers, half of it reads as a pulse instead of a scan line
    private const int IndeterminateBlock = BarWidth / 2;

    //FrameMilliseconds how long one frame stays; the spinner and the moving run step on the same clock so they move together
    private const int FrameMilliseconds = 100;

    private const string Reset = "\u001b[0m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Gray = "\u001b[90m";

    //DECSET 2026: the terminal holds the change until the closing sequence, so one frame reaches the screen whole
    //Terminals that do not know the pair ignore it, and the row is still written in a single call
    private const string SyncStart = "\u001b[?2026h";
    private const string SyncEnd = "\u001b[?2026l";

    //Frames a braille spinner, the densest dot pattern a terminal font carries
    private static readonly string[] Frames = ["⣾", "⣽", "⣻", "⢿", "⡿", "⣟", "⣯", "⣷"];

    private static readonly object Gate = new();
    private static Timer? _timer;
    private static string _stage = "build";
    private static string _target = string.Empty;
    private static int _index;
    private static int _total = 1;
    private static int _value;
    private static int _max = 1;
    private static int _drawn;
    private static bool _active;
    private static bool _plain;
    private static bool _color;
    private static bool _closed;
    //whether the stage has no step count, which replaces the fill and the percentage with a moving run and `??%`
    private static bool _indeterminate;
    //the frame currently on screen, so a repaint that would change nothing is not written at all
    private static string _lastFrame = string.Empty;
    //when the running stage was entered, so its spinner and moving run start from the first frame
    private static long _stageStart;
    //whether the stage row was already ended with a newline, which is what keeps later output off the bar
    private static bool _finished;

    //Plain whether the run prints one line per step, which a child process reads to decide where its output goes
    public static bool Plain => _plain;

    //Run wraps the work in the live line, and steps aside when the output is redirected so logs stay plain
    //Plain mode always prints, one line per step, even when redirected
    public static T Run<T>(bool plain, Func<T> action)
    {
        var interactive = !Console.IsOutputRedirected;
        if (!interactive && !plain)
            return action();

        _plain = plain;
        _color = !plain && interactive && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        _active = true;

        if (!plain)
        {
            //two polls per frame, so a frame is picked up right after its clock ticks instead of up to a frame late
            _timer = new Timer(_ =>
            {
                try
                {
                    Draw();
                }
                catch (IOException)
                {
                    //the terminal went away mid run, which the tool itself will report
                }
            }, null, 0, FrameMilliseconds / 2);
        }

        try
        {
            return action();
        }
        finally
        {
            _timer?.Dispose();
            _timer = null;
            Complete();
            _active = false;

            _stage = "build";
            _target = string.Empty;
            _index = 0;
            _total = 1;
            _value = 0;
            _max = 1;
            _drawn = 0;
            _plain = false;
            _color = false;
            _closed = false;
            _indeterminate = false;
            _lastFrame = string.Empty;
            _stageStart = 0;
            _finished = false;
        }
    }

    //Break clears the running line so a caller can print a line of its own, the timer redraws afterwards
    public static void Break()
    {
        if (!_active || _plain)
            return;

        lock (Gate)
        {
            if (_drawn == 0)
                return;

            WriteLocked("\r" + new string(' ', _drawn) + "\r");
            //the row is gone, so the next draw has to write even when it repeats the last frame
            _lastFrame = string.Empty;
            _drawn = 0;
        }
    }

    //WriteLine prints a message without letting the running line swallow it
    //The line is cleared first, so a note written mid stage lands on its own row instead of under the bar
    public static void WriteLine(string message)
    {
        Break();
        Console.WriteLine(message);
    }

    //Complete closes the running stage at full, which a tool calls before it prints the result lines
    public static void Complete()
    {
        if (!_active || _index == 0 || _finished)
            return;

        lock (Gate)
        {
            _closed = true;
            //a stage without a step count still ends at a full bar, otherwise its last row would read as unfinished
            _indeterminate = false;
            _value = _max;
        }

        if (_plain)
        {
            Console.WriteLine(Line(100));
            _drawn = 0;
            _finished = true;
            return;
        }

        lock (Gate)
        {
            WriteLocked(Frame(Line(100)) + Environment.NewLine);
            //the stage ended and the row was closed, so a later line must not erase what is written on the new one
            _drawn = 0;
            _lastFrame = string.Empty;
            _finished = true;
        }
    }

    //SetTotal fixes how many stages this run has, the stages are entered one by one after it
    public static void SetTotal(int total)
        => _total = Math.Max(1, total);

    //Scanner turns the parse callback into progress steps, the file being relative to the project root
    public static Action<int, int, string> Scanner(string root)
        => (done, total, file) =>
        {
            Resize(total);
            Step(file.Length == 0 ? string.Empty : Path.GetRelativePath(root, file).Replace('\\', '/'));
        };

    //Run without a result, for a tool that reports through the line only
    public static void Run(bool plain, Action action)
        => Run(plain, () =>
        {
            action();
            return true;
        });

    //Enter starts the next stage; target is what it is working on right now
    //A total of zero or less means the stage has no step count, so it runs the moving run and reports no percentage
    public static void Enter(string stage, int total, string target = "")
    {
        if (!_active)
            return;

        lock (Gate)
        {
            if (_index > 0)
                Complete();

            _index++;
            _stage = stage;
            _target = target;
            _value = 0;
            _indeterminate = total <= 0;
            _max = Math.Max(1, total);
            _drawn = 0;
            _lastFrame = string.Empty;
            _stageStart = Environment.TickCount64;
            _closed = false;
            _finished = false;
        }

        if (_plain)
            Console.WriteLine(Line(0));
        else
            Draw();
    }

    //Resize widens the stage once its real size is known, a check learns its file count only after loading
    //A counted size also ends an indeterminate stage, since the percentage becomes meaningful from then on
    public static void Resize(int total)
    {
        if (!_active || total <= 0)
            return;

        lock (Gate)
        {
            _indeterminate = false;
            _max = Math.Max(_value + 1, total);
        }
    }

    public static void Step(string target)
    {
        if (!_active)
            return;

        int percent;
        lock (Gate)
        {
            _target = target;
            _value++;
            //a full step already printed the stage as complete, so closing it again would repeat the line
            _closed = _value >= _max;
            //a stage without a step count ends on its first step, and its closing row shows the full bar
            if (_closed)
                _indeterminate = false;
            percent = Percent();
        }

        if (_plain)
        {
            Console.WriteLine(Line(percent));
            //a step that fills the stage already printed the whole row, so complete must not repeat it
            if (_closed)
                _finished = true;
        }
        else if (_closed)
        {
            //a step that fills the stage ends the row right away, so whatever the caller prints next lands below it
            lock (Gate)
            {
                WriteLocked(Frame(Line(percent)) + Environment.NewLine);
                _drawn = 0;
                _lastFrame = string.Empty;
            }

            _finished = true;
        }
        else
            Draw();
    }

    //Detail moves the target without advancing, for work inside a step such as one assembly of a scan
    //Plain mode ignores it, since a line per assembly would drown the log and the next step names its own file
    public static void Detail(string target)
    {
        if (!_active || _plain)
            return;

        lock (Gate)
            _target = target;
    }

    //Transfer reports a download in progress
    //A running stage only moves its target, while a run without one prints a line per file, so a long download
    //never falls back to a progress bar of its own
    public static void Transfer(string label, int done, int total, string name)
    {
        if (_active && !_plain)
        {
            Detail($"{label} {done}/{total} {name}");
            return;
        }

        WriteLine($"Downloading {label} {done}/{total}: {name}");
    }

    private static void Draw()
    {
        //a closed stage keeps its finished row, otherwise the timer repaints over the lines the caller prints after it
        if (!_active || _index == 0 || _closed)
            return;

        lock (Gate)
            DrawLocked();
    }

    private static void DrawLocked()
    {
        //the timer fires twice per frame, so half its calls land on the frame already on screen
        WriteFrame(Frame(Line(Percent())));
    }

    //Frame renders one repaint of the row and remembers its width
    //The padding covers whatever the previous frame was longer by, since the new line is written over it
    private static string Frame(string line)
    {
        var width = Visible(line);
        var frame = _drawn > width ? "\r" + line + new string(' ', _drawn - width) : "\r" + line;
        _drawn = width;
        return frame;
    }

    //WriteFrame skips a repaint that would leave the row exactly as it is
    private static void WriteFrame(string frame)
    {
        if (string.Equals(_lastFrame, frame, StringComparison.Ordinal))
            return;

        WriteLocked(frame);
    }

    //WriteLocked hands the whole frame to the terminal in one call
    //A console writer that flushed per call would deliver the erase, the row and the padding as separate frames, which is
    //what the flicker was; the sync pair additionally makes a terminal that knows it render the frame atomically
    private static void WriteLocked(string content)
    {
        _lastFrame = content;
        Console.Out.Write(_color ? SyncStart + content + SyncEnd : content);
        Console.Out.Flush();
    }

    private static string Line(int percent)
    {
        var index = Math.Max(1, _index);
        var total = Math.Max(1, _total);
        var percentText = _indeterminate ? "??%" : $"{percent,3}%";

        if (_plain)
            return $"{index}/{total}:{_stage} {percentText} {_target}";

        var frame = Frames[FrameIndex() % Frames.Length];
        var head = $"{index}/{total}:{_stage} {frame}[{Bar(percent, colored: false)}]{percentText} ";
        var room = Math.Max(12, Width() - head.Length - 1);
        var name = _target.Length > room ? "..." + _target[^(room - 3)..] : _target;
        if (!_color)
            return head + name;

        return $"{Cyan}{index}/{total}:{_stage} {frame}{Reset}[{Bar(percent, colored: true)}]"
            + $"{Yellow}{percentText}{Reset} {Gray}{name}{Reset}";
    }

    //Bar renders the twenty cells: a fill for a counted stage, a run sliding across for one without a count
    private static string Bar(int percent, bool colored)
    {
        if (!_indeterminate)
        {
            var filled = Math.Clamp(percent * BarWidth / 100, 0, BarWidth);
            var empty = BarWidth - filled;
            return colored
                ? $"{Green}{new string('#', filled)}{Gray}{new string(' ', empty)}{Reset}"
                : new string('#', filled) + new string(' ', empty);
        }

        //the run slides one cell per frame and wraps around the end, so it moves evenly instead of jumping back
        var start = RunStart();
        return colored ? ColoredRun(start) : new string(RunCells(start));
    }

    //RunCells the bar with the moving run on it, the part past the end continues at the start
    private static char[] RunCells(int start)
    {
        var cells = new char[BarWidth];
        for (var cell = 0; cell < BarWidth; cell++)
            cells[cell] = IsRun(cell, start) ? '#' : ' ';
        return cells;
    }

    //ColoredRun paints the same cells, cutting them into segments so each run of the same kind is one escape pair
    private static string ColoredRun(int start)
    {
        var text = string.Empty;
        var cell = 0;
        while (cell < BarWidth)
        {
            var filled = IsRun(cell, start);
            var end = cell;
            while (end < BarWidth && IsRun(end, start) == filled)
                end++;

            text += (filled ? Green : Gray) + new string(filled ? '#' : ' ', end - cell);
            cell = end;
        }

        return text + Reset;
    }

    //IsRun whether a cell lies inside the moving run, which wraps around the end of the bar
    private static bool IsRun(int cell, int start)
        => (cell - start + BarWidth) % BarWidth < IndeterminateBlock;

    //FrameIndex the frame the clock is on, shared by the spinner and the moving run
    //It counts from the moment the stage started, so every stage opens on the same frame instead of mid animation
    private static long FrameIndex() => Math.Max(0, Environment.TickCount64 - _stageStart) / FrameMilliseconds;

    //RunStart where the moving run begins this frame, one cell per frame around the whole bar
    private static int RunStart() => (int)(FrameIndex() % BarWidth);

    private static int Percent()
        => Math.Clamp(_value * 100 / Math.Max(1, _max), 0, 100);

    //Visible the printed width without the colour escapes
    private static int Visible(string text)
    {
        var width = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\u001b')
            {
                width++;
                continue;
            }

            while (index < text.Length && text[index] != 'm')
                index++;
        }

        return width;
    }

    private static int Width()
    {
        try
        {
            return Math.Max(40, Console.WindowWidth);
        }
        catch (Exception)
        {
            return 120;
        }
    }
}
