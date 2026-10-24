using NacreCraft.Logging;
using NacreCraft.ModApi.Extension;
using NacreCraft.ModApi.Wrapper;

namespace NcModTemplate;

//ModEntry client side
public sealed partial class ModEntry
{
    //InitClient client-side subscriptions
    static partial void InitClient()
    {
        ClientEvents.Tick.Subscribe(args =>
        {
            //Log once every 100 ticks to avoid flooding; real code would keep and dispose the handle
            if (args.TickCount % 100 == 0)
                Log.Debug($"NcModTemplate client tick {args.TickCount}");
        });
    }
}
