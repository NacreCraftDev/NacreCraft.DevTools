using System.Xml;
using System.Xml.Linq;

namespace NacreCraft.ModBuild.Core;

//Writing project config files back; two-space indentation matches hand-written .ncproj files and a trailing newline is appended
internal static class XmlFile
{
    public static void Save(string path, XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
            NewLineChars = Environment.NewLine,
        };

        using (var writer = XmlWriter.Create(path, settings))
            document.Save(writer);

        File.AppendAllText(path, Environment.NewLine);
    }
}
