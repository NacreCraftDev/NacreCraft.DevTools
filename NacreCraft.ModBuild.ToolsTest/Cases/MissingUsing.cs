namespace NacreCraft.ModBuild.ToolsTest.Cases;

public static class MissingUsing
{
    public static void Run()
    {
        //expect: `StringBuilder` is declared in `System.Text`, add `using System.Text;`
        var builder = new StringBuilder();

        //System.Linq is part of the implicit usings ncm checks with, so the case uses a namespace outside that set
        //expect: `Regex` is declared in `System.Text.RegularExpressions`, add `using System.Text.RegularExpressions;`
        var matches = Regex.IsMatch("text", "t");
    }
}
