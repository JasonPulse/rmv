using Rmv.Web.Data;

namespace Rmv.Web.Herald;

/// <summary>
/// Which address a game's herald is at.
///
/// The adapter's own, unless the game overrides it. The override exists for a
/// server changing domain, not as a field to fill in: an adapter is code written
/// against one specific herald, so it knows where that herald lives.
///
/// One method because this was two. CharacterService had it privately and
/// ServerStatusMonitor had its own copy, while the monitor's own comment claimed
/// "the address is the one the character fetches use". That was true by
/// coincidence. The consequence of the two drifting is the worst kind: the status
/// light on the home page reports a different server from the one the site
/// actually fetches characters from, and both look like they are working.
/// </summary>
public static class HeraldAddress
{
    public static string For(GamePresence game, IHeraldAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(adapter);

        return string.IsNullOrWhiteSpace(game.HeraldBaseUrl)
            ? adapter.DefaultBaseUrl
            : game.HeraldBaseUrl;
    }
}
