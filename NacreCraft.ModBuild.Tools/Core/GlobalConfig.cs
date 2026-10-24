namespace NacreCraft.ModBuild.Core;

//Machine wide defaults, read from Config.ncproj beside the caches
//It is not a project of its own: what it declares fills in whatever a project leaves out, and a value the project declares
//always wins, so sharing a kernel source or a mirror through it never overrides a project that has its own mind
//Its tasks are reachable from anywhere and show up in help, which makes them global commands rather than project ones
internal static class GlobalConfig
{
    //File name, kept .ncproj so it is parsed by exactly the same rules as a project config
    private const string FileName = "Config.ncproj";

    //Location beside the server, client and update caches
    public static string Location => Path.Combine(CacheLayout.Root, FileName);

    //Written on first use; every example is commented out, since an untouched file must not change any behaviour
    private const string Template = """
        <ncproj version="1">
          <!--
            Machine wide defaults for every project on this machine.

            Whatever is written here fills in what a project leaves out: the pull rules field by field, any other element
            whole. A value the project itself declares always wins, so this file never overrides a project config.

            Tasks declared here run from any directory as ncm <name> and are listed as global tasks in help. Their
            $(ProjectDir) is the directory the command was issued from, so a shared task can act on the current project.

            A few examples, all commented out:
          <Server Cache="cache" />
          <Client Version="26.2" />
          <Sources Url="https://example.com/kernel/" Index="index.txt" Format="sha256-lines" />
          <Template Url="https://example.com/NacreCraftTemplate.yaml" />

          <Tasks>
            <Task Name="example" Description="an example global task">
              <Exec>ncm help</Exec>
            </Task>
          </Tasks>
          -->
        </ncproj>
        """;

    private static NcProject? Loaded { get; set; }

    private static bool Resolved { get; set; }

    //The global config, created with its template on first use and null when it cannot be read
    public static NcProject? Load()
    {
        if (Resolved)
            return Loaded;

        Resolved = true;
        TryCreate();
        if (!File.Exists(Location))
            return null;

        Loaded = NcProject.LoadFile(Location, out var error);
        if (Loaded is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"warning: {error}");
            Console.ResetColor();
            return null;
        }

        Trace.Log($"global config loaded from {Location}");
        return Loaded;
    }

    //Create the file when it is missing, so the settings have a documented home from the first run on
    private static void TryCreate()
    {
        if (File.Exists(Location))
            return;

        try
        {
            Directory.CreateDirectory(CacheLayout.Root);
            File.WriteAllText(Location, Template);
            Trace.Log($"global config created at {Location}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.Log($"cannot create {Location}: {e.Message}");
        }
    }
}
