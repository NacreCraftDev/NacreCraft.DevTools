using NacreCraft.Logging;
using NacreCraft.ModApi.Extension;
using NacreCraft.ModApi.Wrapper;

namespace NcModTemplate;

//ModEntry server side
public sealed partial class ModEntry
{
    //InitServer server-side subscriptions and command registration
    //Subscribe returns an unsubscribe handle; ignore it when subscribing once at startup
    static partial void InitServer()
    {
        ServerEvents.Started.Subscribe(_ => Log.Info("NcModTemplate server started"));

        //Player joined; the join packet sequence has finished so player state is safe to read
        ServerEvents.PlayerJoin.Subscribe(args =>
            Log.Info($"NcModTemplate player joined {args.ProfileName}"));

        //The kernel took a snapshot right before writing to disk; keep this callback cheap and safe
        ServerEvents.ChunkSaved.Subscribe(args =>
            Log.Debug($"NcModTemplate chunk saved {args.X},{args.Z}"));

        //Command registration happens after every built-in command is in place
        ServerEvents.CommandRegister.Subscribe(args =>
            args.Register("__MOD_ID__", "Example command from NcModTemplate", builder =>
                builder.Executes(context =>
                {
                    context.GetSource().SendSuccess("hello from NcModTemplate");
                    return 1;
                })));
    }
}
