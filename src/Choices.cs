using System;
using BepInEx.Configuration;
using DiceModders.ModCore;

namespace DiceModders.Upkeep;

using Core = DiceModders.ModCore.ModCore;

/// <summary>
/// A player's upkeep choices, shared with every computer through ModCore's player values. A change made during turn N counts from turn N + 1
/// (the user's choice), so it has reached every computer long before the disasters phase that uses it.
///   "upkeep.c" = "from|flags|before": flags = pay guard towers, pay lighthouses, guard towers eat first ('1'/'0'); "before" counts until turn "from".
///   "upkeep.o" = the turn whose harvest gets this player's Cathedral offering.
/// Computer players and players without a value: pay everything, houses eat first, no offering.
/// </summary>
internal static class Choices
{
    private const string ChoiceKey = "upkeep.c";
    private const string OfferKey = "upkeep.o";
    internal const string Default = "110";

    internal static ConfigEntry<bool> PayTowers, PayLighthouses, TowersFirst;

    internal static void Bind(ConfigFile config)
    {
        PayTowers = config.Bind("Choices", "PayGuardTowers", true, "Feed the guard towers (unfed towers do not shoot in this turn's combat).");
        PayLighthouses = config.Bind("Choices", "PayLighthouses", true, "Pay the lighthouses (unpaid ones do not shoot and give no fish bonus).");
        TowersFirst = config.Bind("Choices", "GuardTowersEatFirst", false, "When food is short, the guard towers eat before the houses.");
        Core.Match.Started += () => Publish(true);
    }

    internal static string OwnFlags => (PayTowers.Value ? "1" : "0") + (PayLighthouses.Value ? "1" : "0") + (TowersFirst.Value ? "1" : "0");

    /// <summary>Publishes this player's choices: at the start of a match they count at once, later from the next turn.</summary>
    internal static void Publish(bool matchStart = false)
    {
        int turn = Core.Match.Turn;
        string now = matchStart ? OwnFlags : Effective(Core.Lobby.PlayerValues.Get(LocalId, ChoiceKey), turn);
        int from = matchStart ? 0 : turn + 1;
        Core.Lobby.PlayerValues.Set(ChoiceKey, from + "|" + OwnFlags + "|" + now);
    }

    /// <summary>Asks for this player's Cathedral offering at the next harvest.</summary>
    internal static void Offer() => Core.Lobby.PlayerValues.Set(OfferKey, (Core.Match.Turn + 1).ToString());

    internal static bool OfferedFor(int turn) => Core.Lobby.PlayerValues.Get(LocalId, OfferKey) == turn.ToString();

    /// <summary>The flags of <paramref name="player"/> that count in <paramref name="turn"/>.</summary>
    internal static string Of(MatchPlayer player, int turn)
    {
        if (player == null || player.IsAI) return Default;
        return Effective(Core.Lobby.PlayerValues.Get(player.Id, ChoiceKey), turn);
    }

    internal static bool Offers(MatchPlayer player, int turn) =>
        player != null && !player.IsAI && Core.Lobby.PlayerValues.Get(player.Id, OfferKey) == turn.ToString();

    private static string Effective(string value, int turn)
    {
        if (string.IsNullOrEmpty(value)) return Default;
        var p = value.Split('|');
        if (p.Length != 3 || !int.TryParse(p[0], out int from) || p[1].Length != 3 || p[2].Length != 3) return Default;
        return turn >= from ? p[1] : p[2];
    }

    private static ulong LocalId => Core.Players.Local?.Id ?? 0;
}
