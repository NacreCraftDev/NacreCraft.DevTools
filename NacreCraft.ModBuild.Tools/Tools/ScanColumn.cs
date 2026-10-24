using Spectre.Console;
using Spectre.Console.Rendering;

namespace NacreCraft.ModBuild.Tools;

//ScanColumn draws the call graph scan as percentage, a plain bracket bar and the file being read
//It is a column of its own instead of the download bar in the stores, which has a different shape and a different job
internal sealed class ScanColumn : ProgressColumn
{
    private const int BarWidth = 20;

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        var percent = task.MaxValue <= 0
            ? 0
            : Math.Clamp((int)(task.Value * 100 / task.MaxValue), 0, 100);

        var filled = Math.Clamp(percent * BarWidth / 100, 0, BarWidth);
        var bar = new string('#', filled) + new string(' ', BarWidth - filled);
        var head = $"{percent,3}%[{bar}] ";

        var name = task.Description;
        var room = Math.Max(12, options.ConsoleSize.Width - head.Length - 1);
        if (name.Length > room)
            name = "..." + name[^(room - 3)..];

        return new Markup(Markup.Escape(head + name));
    }
}
