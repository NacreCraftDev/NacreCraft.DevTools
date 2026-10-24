using NacreCraft.Logging;
using NacreCraft.ModApi.Extension;
using NacreCraft.ModApi.Wrapper;

namespace test;

//ModEntry server side
public sealed partial class ModEntry
{
    //InitServer server-side subscriptions and command registration
    //Subscribe returns an unsubscribe handle; ignore it when subscribing once at startup
    static partial void InitServer()
    {
        ServerEvents.Started.Subscribe(_ => Log.Info("test server started"));

        //Player joined; the join packet sequence has finished so player state is safe to read
        ServerEvents.PlayerJoin.Subscribe(args =>
            Log.Info($"test player joined {args.ProfileName}"));

        //The kernel took a snapshot right before writing to disk; keep this callback cheap and safe
        ServerEvents.ChunkSaved.Subscribe(args =>
            Log.Debug($"test chunk saved {args.X},{args.Z}"));

        //Command registration happens after every built-in command is in place
        ServerEvents.CommandRegister.Subscribe(args =>
            args.Register("test", "Example command from test", builder =>
                builder.Executes(context =>
                {
                    context.GetSource().SendSuccess("hello from test");
                    return 1;
                })));
    }
}
