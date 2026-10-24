using NacreCraft.Logging;

namespace test;

//ModEntry mod entry point
//The loader finds this class through the entry field in ncmod.json and calls Init once at load time
//The class name is fixed and does not follow the project name - a project name may contain dots,
//which would not form a legal type name
//By the time Init runs the injection is done and the event routes are live, so all that is left
//is subscribing and registering
public sealed partial class ModEntry
{
    //Init called once when the mod loads
    //Each side keeps its subscriptions in its own partial method; the half that does not exist on
    //this side is never generated and the call is compiled away
    public Task Init()
    {
        Log.Info("test loaded");
        InitServer();
        InitClient();
        return Task.CompletedTask;
    }

    //InitServer server-side subscriptions; implemented only when environment is both or server
    static partial void InitServer();

    //InitClient client-side subscriptions; implemented only when environment is both or client
    static partial void InitClient();
}
