using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Saving on the phone. Only one device can be signed in per account, so a "changed somewhere else" answer always
/// comes from this phone itself: the app editor and the bubble both had the macro open, or the phone lost the
/// server's answer to an earlier save when the connection dropped. The copy on screen is the newest, so keep it.
/// </summary>
internal static class Saving
{
    /// <summary>Call from the autosaver's StateChanged: answers a conflict with "keep mine".</summary>
    public static void KeepMineOnConflict(DraftAutosaver saver, SyncState state)
    {
        if (state == SyncState.Conflict) Night.OnMain(saver.KeepMine);
    }

    /// <summary>Saves; after a conflict, keeps this phone's copy and tries once more.</summary>
    public static async Task<MacroDto> SaveAsync(DraftAutosaver saver, MacroDoc doc)
    {
        try
        {
            return await saver.SaveAsync(doc);
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException && saver.State == SyncState.Conflict)
        {
            saver.KeepMine();
            return await saver.SaveAsync(doc);
        }
    }
}
