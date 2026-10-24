namespace NacreCraft.ModBuild.Core;

//FormField, one row of a form
public sealed class FormField(string key, string label, bool required = false)
{
    public string Key { get; } = key;

    public string Label { get; } = label;

    //Required fields block submission while empty
    public bool Required { get; } = required;

    public string Value { get; set; } = string.Empty;

    public bool IsFilled => !string.IsNullOrWhiteSpace(Value);

    //Runs on every value change to pick the row color; null falls back to the default palette
    public Func<string, FieldState>? Validate { get; set; }
}

//How valid a row currently looks
public enum FieldState
{
    Normal,

    Warn,

    Error,
}

//Prompt interaction primitives; with redirected input or output they degrade to line-by-line questions so scripts and CI still work
public static class Prompt
{
    //How long the missing-field highlight stays red
    private static readonly TimeSpan InvalidHighlight = TimeSpan.FromSeconds(3);

    //Extra rounds the line mode re-asks for missing required fields
    private const int MaxLineAttempts = 3;

    //Ask a yes/no question; defaultYes decides the empty answer and warn colors the whole prompt yellow
    public static bool Confirm(string question, bool defaultYes, bool warn = false)
    {
        if (warn)
            Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{question} ({(defaultYes ? "Y/n" : "y/N")}) ");
        Console.ResetColor();

        var answer = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(answer))
            return defaultYes;
        return answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    //Collect a group of fields and report whether every required one is filled; at a terminal it is an arrow-key form that blocks Enter and jumps to the nearest missing field, and under redirection it asks one line per field
    public static bool Form(IReadOnlyList<FormField> fields)
    {
        if (fields.Count == 0)
            return true;

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            //The fallback must be visible or users just assume the terminal is broken
            Console.WriteLine("Interactive form is not available here (console is redirected), using line-by-line prompts.");
            return FillByLines(fields);
        }

        return FillInteractive(fields);
    }

    //Line-by-line prompts where an empty line skips a field; missing required fields trigger another round and a broken input ends it to avoid spinning in a pipe
    private static bool FillByLines(IReadOnlyList<FormField> fields)
    {
        for (var attempt = 0; attempt < MaxLineAttempts; attempt++)
        {
            //Ask everything on the first round, then only the still-empty required fields
            IReadOnlyList<FormField> pending = attempt == 0
                ? fields
                : fields.Where(field => field.Required && !field.IsFilled).ToList();

            if (pending.Count == 0)
                return true;

            foreach (var field in pending)
            {
                Console.Write($"{field.Label}: ");
                var value = Console.ReadLine();
                if (value is null)
                    return false;
                if (!string.IsNullOrWhiteSpace(value))
                    field.Value = value.Trim();
            }

            if (fields.All(field => !field.Required || field.IsFilled))
                return true;

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("A required field is still empty, fill it in and I will ask again.");
            Console.ResetColor();
        }

        return false;
    }

    //Interactive list form; reserve the block's lines up front and redraw in place each frame so repainting does not scroll the screen
    private static bool FillInteractive(IReadOnlyList<FormField> fields)
    {
        for (var i = 0; i < fields.Count; i++)
            Console.WriteLine();

        //The cursor rests after the block, so derive the top from it and clamp to stay in bounds when scrolled to the bottom
        var top = Math.Max(0, Console.CursorTop - fields.Count);
        var selected = 0;
        var highlightUntil = DateTime.MinValue;
        var showCursor = OperatingSystem.IsWindows() && Console.CursorVisible;
        SetCursorVisible(false);

        try
        {
            while (true)
            {
                var highlighting = DateTime.UtcNow < highlightUntil;
                DrawFrame(top, fields, selected, highlighting);

                //The highlight expires on its own but keys still register, immediately restoring the normal look
                if (highlighting)
                {
                    while (!Console.KeyAvailable && DateTime.UtcNow < highlightUntil)
                        Thread.Sleep(30);
                    if (!Console.KeyAvailable)
                        continue;
                }

                var key = Console.ReadKey(true);
                highlightUntil = DateTime.MinValue;

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + fields.Count) % fields.Count;
                        break;
                    case ConsoleKey.DownArrow:
                        selected = (selected + 1) % fields.Count;
                        break;
                    case ConsoleKey.Enter:
                        var missing = FirstMissing(fields, selected);
                        if (missing < 0)
                            return true;
                        selected = missing;
                        highlightUntil = DateTime.UtcNow + InvalidHighlight;
                        break;
                    case ConsoleKey.Backspace:
                        if (fields[selected].Value.Length > 0)
                            fields[selected].Value = fields[selected].Value[..^1];
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                            fields[selected].Value += key.KeyChar;
                        break;
                }
            }
        }
        finally
        {
            SetCursorVisible(showCursor);
            //Move the cursor below the block on commit or exception so later output does not overlap the form
            Console.SetCursorPosition(0, Math.Min(top + fields.Count - 1, Console.BufferHeight - 1));
            Console.WriteLine();
        }
    }

    //Find the nearest empty required field scanning downward and wrapping, starting at the next row so the current one is still checked; -1 when none
    private static int FirstMissing(IReadOnlyList<FormField> fields, int selected)
    {
        for (var offset = 1; offset <= fields.Count; offset++)
        {
            var index = (selected + offset) % fields.Count;
            if (fields[index].Required && !fields[index].IsFilled)
                return index;
        }
        return -1;
    }

    //Cursor visibility is Windows-only, reading it elsewhere throws
    private static void SetCursorVisible(bool visible)
    {
        if (OperatingSystem.IsWindows())
            Console.CursorVisible = visible;
    }

    //Redraw the whole form in place, padding lines to erase the previous frame; required labels carry an asterisk and highlightInvalid reddens the empty ones
    private static void DrawFrame(int top, IReadOnlyList<FormField> fields, int selected, bool highlightInvalid)
    {
        var width = Math.Max(Console.WindowWidth - 1, 20);

        for (var i = 0; i < fields.Count; i++)
        {
            Console.SetCursorPosition(0, top + i);
            var field = fields[i];
            var label = field.Required ? $"{field.Label} *" : field.Label;
            var line = $"{(i == selected ? ">" : " ")} {label}: {field.Value}";

            //Validation state wins over selection so problems stand out; selection only marks the cursor row
            var state = field.Validate?.Invoke(field.Value) ?? FieldState.Normal;
            Console.ForegroundColor = state switch
            {
                FieldState.Error => ConsoleColor.Red,
                FieldState.Warn => ConsoleColor.Yellow,
                _ when highlightInvalid && field.Required && !field.IsFilled => ConsoleColor.Red,
                _ => i == selected ? ConsoleColor.Cyan : ConsoleColor.Gray,
            };

            Console.Write(line.Length > width ? line[..width] : line.PadRight(width));
            Console.ResetColor();
        }

        Console.SetCursorPosition(0, top + selected);
    }
}
