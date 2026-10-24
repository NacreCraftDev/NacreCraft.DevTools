import { NcmActions, PackageInfo, PackageSearchResult } from "./actions";

//NuGetCache keeps feed answers for the session
//The extension preloads the default listing once, so opening the tab or switching back to it does not ask again;
//the refresh button clears the answer and fetches it once more
export class NuGetCache {
    private static readonly entries = new Map<string, PackageInfo[]>();

    private static readonly inflight = new Map<string, Promise<PackageSearchResult>>();

    //Preload fetches one query so the page opens on data that is already there
    public static async preload(source: string): Promise<void> {
        await NuGetCache.search("", source, false);
    }

    //Search answers from the cache unless a refresh is asked for
    public static async search(
        query: string,
        source: string,
        refresh: boolean
    ): Promise<PackageSearchResult> {
        const key = `${source}\n${query}`;

        if (!refresh) {
            const cached = NuGetCache.entries.get(key);
            if (cached) {
                return { ok: true, packages: cached };
            }

            //a preload still running is joined rather than asked twice
            const running = NuGetCache.inflight.get(key);
            if (running) {
                return running;
            }
        }

        const call = NcmActions.nugetSearch(query, source).then((result) => {
            //an answer a newer request replaced must not overwrite the cache
            if (NuGetCache.inflight.get(key) === call) {
                NuGetCache.inflight.delete(key);
                if (result.ok) {
                    NuGetCache.entries.set(key, result.packages ?? []);
                }
            }
            return result;
        });

        NuGetCache.inflight.set(key, call);
        return call;
    }

    //Clear forgets every answer, which a refresh of the whole feed uses
    public static clear(): void {
        NuGetCache.entries.clear();
    }
}
