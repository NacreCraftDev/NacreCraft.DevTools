namespace NacreCraft.ModBuild.Tools.Experimental;

//Registers the commands that only exist on this branch
//Each command lives under Commands/ and calls ToolRegistry.Register from its own Register method,
//so adding an experiment is one file plus one line here and deleting it is the reverse
internal static class ExperimentalRegistration
{
    public static void RegisterAll()
    {
        Commands.SelfCheckCommand.Register();
        Commands.JavaCallGraphCommand.Register();
        Commands.JavaFlowCommand.Register();
    }
}
