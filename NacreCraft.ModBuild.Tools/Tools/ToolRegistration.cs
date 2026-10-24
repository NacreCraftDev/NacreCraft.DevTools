namespace NacreCraft.ModBuild.Tools;

//Registers every built-in tool, which both the command line and the language server need
//The language server names the tools when it decides whether a task replaces one, so it registers them up front
public static class ToolRegistration
{
    public static void RegisterAll()
    {
        IconTool.Register();
        InitTool.Register();
        TemplateTool.Register();
        AddTool.Register();
        RestoreTool.Register();
        BuildTool.Register();
        CleanTool.Register();
        AsmTool.Register();
        HookTool.Register();
        CallGraphTool.Register();
        RunServerTool.Register();
        RunClientTool.Register();
        UpgradeTool.Register();
        UpdateTool.Register();
        ToolCommand.Register();
    }
}
