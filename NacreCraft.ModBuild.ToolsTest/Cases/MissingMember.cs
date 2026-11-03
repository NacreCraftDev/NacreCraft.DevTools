namespace NacreCraft.ModBuild.ToolsTest.Cases;

public static class MissingMember
{
    public static void Run()
    {
        var widget = new Widget("a", "b");

        //the compiler says the member is missing, so the message is rewritten to agree with the advice
        //expect: 'Widget.Count' is a field, not a method
        //expect: `Widget.Count` is a field, call it without parentheses
        var count = widget.Count();

        //expect: did you mean `Count`?
        var typo = widget.Cuont;

        //expect: `Widget` declares
        var absent = widget.NoSuchMember;

        //expect: `Widget` declares
        var missingStatic = Widget.NoSuchStatic;

        //expect: An object reference is required for the non-static member
        var instanceOnly = Widget.Count;
    }
}
