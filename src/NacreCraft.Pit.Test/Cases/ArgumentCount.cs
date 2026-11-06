namespace NacreCraft.Pit.Test.Cases;

public static class ArgumentCount
{
    public static void Run()
    {
        //too few arguments for the only constructor
        //expect: `Widget` is called as `Widget(string, string)`
        var few = new Widget("only one");

        //the named argument does not exist, so the real parameter names are listed
        //expect: `Widget` takes `first, second`
        var named = new Widget(wrong: "a");

        //a method call with no matching overload
        //expect: `Run` is called as `Run(int)`
        Tool.Run();

        //more arguments than the only overload accepts
        //expect: `Run` is called as `Run(int)`
        Tool.Run(1, 2);

        //more arguments than any constructor accepts
        //expect: `Widget` is built as `Widget(string, string)`
        var many = new Widget("a", "b", "c");
    }
}
