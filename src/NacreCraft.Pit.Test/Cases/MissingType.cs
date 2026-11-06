namespace NacreCraft.Pit.Test.Cases;

public static class MissingType
{
    public static void Run()
    {
        //a near miss keeps the first letter, so a diff is offered
        //expect: did you mean `Widget`?
        Widgte typo = null;

        //nothing visible shares the first letter and is close, so the plain leads are printed instead of a guess
        //expect: Check the spelling of the type or namespace
        //expect: Add a using directive for the namespace that declares it
        //expect-not: did you mean `Type`?
        //expect-not: Build/kernel
        Nope absent = null;
    }
}
