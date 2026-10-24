namespace NacreCraft.ModBuild.Core;

//Debug output switch; it only speaks when NCM_DEBUG is set since the console should stay quiet otherwise, and it exists to inspect invisible steps like project discovery and usage scanning
public static class Trace
{
    public static bool Enabled { get; } = IsOn();

    public static void Log(string message)
    {
        if (Enabled)
            Console.WriteLine($"[debug] {message}");
    }

    //On when the variable holds a value other than 0 or false
    private static bool IsOn()
    {
        var value = Environment.GetEnvironmentVariable("NCM_DEBUG");
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }
}
