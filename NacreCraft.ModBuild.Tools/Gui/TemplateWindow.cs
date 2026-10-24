using Photino.NET;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Gui;

//The template panel window
//The page carries all its own data, the host only opens and closes the window, and the catalog table is built in the page by js
internal static class TemplateWindow
{
    private const int WindowWidth = 1100;
    private const int WindowHeight = 720;

    public static void Show(TemplateCatalog? catalog)
    {
        //Write the whole page to a temp file and let the window load it by path, since handing the page string to LoadRawString crashes in native code once it grows (0xC0000005)
        var page = Path.Combine(Path.GetTempPath(), "ncm-panel.html");
        File.WriteAllText(page, UiPage.Build(catalog));

        var window = new PhotinoWindow()
            //Silence Photino's own api logging, which otherwise prints a line for every property set
            .SetLogVerbosity(0)
            .SetTitle("NacreCraft Template Browser")
            .SetUseOsDefaultSize(false)
            .SetSize(WindowWidth, WindowHeight)
            .SetResizable(true)
            .Load(page);

        window.WaitForClose();
    }
}
