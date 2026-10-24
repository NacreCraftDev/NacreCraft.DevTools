using System.Globalization;
using System.Text;
using NacreCraft.ModBuild.Core;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NacreCraft.ModBuild.Tools;

//IconTool generates an icon for the current mod project
//A white background with the mod name in black, the text scaled to fill the padding and wrapped when it does not fit
internal static class IconTool
{
    private const int Size = 128;

    private const int Padding = 10;

    //LineSample two letters with both an ascender and a descender, the best sample for a line's real height
    private const string LineSample = "Ag";

    private const string FontResource = "iconttf.ttf";

    //RefinePasses font size and wrapping affect each other, a few passes let both settle
    private const int RefinePasses = 3;

    //Family parses the embedded font once
    private static readonly Lazy<FontFamily> Family = new(LoadFamily);

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("icon", "Generate a white background mod icon with the mod name", Run,
        [
            new("-f, --force", "Overwrite an existing icon without asking"),
        ]);

    //Run executes, silently exiting when not inside a mod project
    private static int Run(string[] args)
    {
        var force = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-f":
                case "--force":
                    force = true;
                    break;
                default:
                    Console.WriteLine($"error: unknown icon option {arg}");
                    Console.WriteLine("Usage: ncm icon [--force]");
                    return 1;
            }
        }

        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
            return 0;

        //With force the overwrite question is skipped
        if (!force && File.Exists(project.IconPath) && !ConfirmOverwrite(project.IconPath))
            return 0;

        Generate(project);
        return 0;
    }

    //Generate draws the icon for a project and adds an icon entry when the manifest lacks one
    //Init calls this after creating a project, which is why generation is separate from the command line entry
    internal static void Generate(ModProject project)
    {
        WriteIcon(project.IconPath, project.DisplayName);
        Console.WriteLine($"Icon written to {project.IconPath}");

        //Add an icon entry when the manifest lacks one, the generated path already follows the default convention
        if (project.EnsureIcon(ModProject.DefaultIconName))
            Console.WriteLine($"Added icon entry to {Path.GetFileName(project.ManifestPath)}");
    }

    //ConfirmOverwrite asks when the icon already exists
    //Enter and y both mean skip, only an explicit no overwrites
    private static bool ConfirmOverwrite(string path)
    {
        Console.Write($"Icon already exists ({Path.GetFileName(path)}). Skip? (Y/n) ");
        var answer = Console.ReadLine();
        return answer is not null && answer.StartsWith("n", StringComparison.OrdinalIgnoreCase);
    }

    //WriteIcon draws the image and saves it
    //Starting from the full side length the first pass almost always overflows, scaling brings the font back inside the padding
    private static void WriteIcon(string path, string text)
    {
        var family = Family.Value;
        var available = Size - Padding * 2f;

        var fontSize = (float)Size;
        var lines = Wrap(family, fontSize, text, available);

        //A new font size changes the wrapping, so a few passes let both settle
        for (var pass = 0; pass < RefinePasses; pass++)
        {
            var (width, height) = MeasureBlock(family, fontSize, lines);
            if (width <= 0f || height <= 0f)
                break;

            fontSize *= Math.Min(available / width, available / height);
            lines = Wrap(family, fontSize, text, available);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var image = new Image<Rgba32>(Size, Size, new Rgba32(255, 255, 255));
        DrawLines(image, family, fontSize, lines);
        image.SaveAsPng(path);
    }

    //DrawLines draws line by line, each horizontally centered on its own ink and the block vertically centered
    //Using ink instead of layout boxes makes the text look centered, layout boxes carry uneven padding on top and bottom
    private static void DrawLines(Image<Rgba32> image, FontFamily family, float fontSize, List<string> lines)
    {
        var font = family.CreateFont(fontSize, FontStyle.Regular);
        var lineHeight = MeasureLineHeight(font);
        var top = (Size - lineHeight * lines.Count) / 2f;

        image.Mutate(context =>
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var ink = TextMeasurer.MeasureBounds(lines[i], new TextOptions(font));
                var origin = new PointF(
                    (Size - ink.Width) / 2f - ink.Left,
                    top + i * lineHeight - ink.Top);
                context.DrawText(new RichTextOptions(font) { Origin = origin }, lines[i], Color.Black);
            }
        });
    }

    //MeasureBlock measures the ink extent of multiple lines, used as the basis for scaling the font size
    //Line spacing only applies between lines and is not added for a single line
    private static (float Width, float Height) MeasureBlock(FontFamily family, float fontSize, List<string> lines)
    {
        var font = family.CreateFont(fontSize, FontStyle.Regular);
        var lineHeight = MeasureLineHeight(font);
        var width = 0f;
        var height = 0f;

        foreach (var line in lines)
        {
            var ink = TextMeasurer.MeasureBounds(line, new TextOptions(font));
            width = Math.Max(width, ink.Width);
            height = Math.Max(height, ink.Height);
        }

        return (width, height + lineHeight * (lines.Count - 1));
    }

    //Wrap breaks lines to fit the width, preferring a space
    //A single word wider than the limit gets its own line, the later scaling brings it back inside the padding
    private static List<string> Wrap(FontFamily family, float fontSize, string text, float maxWidth)
    {
        var font = family.CreateFont(fontSize, FontStyle.Regular);
        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && MeasureWidth(font, candidate) > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');
            current.Append(word);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return lines.Count > 0 ? lines : new List<string> { text };
    }

    //MeasureLineHeight measures a full line's height, spacing comes from the font itself
    private static float MeasureLineHeight(Font font)
        => TextMeasurer.MeasureSize(LineSample, new TextOptions(font)).Height;

    //MeasureWidth the layout width of a run of text
    private static float MeasureWidth(Font font, string value)
        => TextMeasurer.MeasureSize(value, new TextOptions(font)).Width;

    //LoadFamily parses the font from the embedded resource
    //Parsing needs a seekable stream, which the resource stream is not, so it is read into memory first
    private static FontFamily LoadFamily()
    {
        using var resource = typeof(IconTool).Assembly.GetManifestResourceStream(FontResource)
            ?? throw new InvalidOperationException($"Embedded font {FontResource} not found");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        buffer.Position = 0;
        return new FontCollection().Add(buffer, CultureInfo.InvariantCulture);
    }
}
