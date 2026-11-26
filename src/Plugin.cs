using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DiceModders.ModCore.Tools;

namespace DiceModders.Upkeep;

using Core = DiceModders.ModCore.ModCore;   // inside DiceModders.* the bare name would be the namespace
using Ui = DiceModders.UiKit.UiKit;

/// <summary>
/// DiceKingdoms Upkeep: a lobby rule (off by default). While it is on, guard towers eat food and lighthouses use wood or stone every turn;
/// unpaid ones do not shoot, and unpaid lighthouses give no fish bonus. Every player chooses in the Upkeep window (top bar) what to pay and
/// whether the guard towers eat before the houses, and can make an offering at the Cathedrals that doubles their culture at the next harvest.
/// Everything reacts to the game's own steps; nothing runs every frame.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency("DiceModders.ModCore")]
[BepInDependency("DiceModders.UiKit")]
[BepInProcess("Dice-Kingdoms.exe")]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "org.dicemodders.dicekingdoms.upkeep";
    public const string Name = "DiceKingdoms Upkeep";
    public const string Version = "1.0.3";

    internal static ManualLogSource L;
    /// <summary>The mod's own switch ([General] Enabled); while off the game behaves as without the mod.</summary>
    internal static bool On;

    private static readonly HashSet<string> Logged = new();

    public override void Load()
    {
        Tx.Load();                                   // texts.json in the game's language (ModCore)
        L = Log;
        Choices.Bind(Config);

        var patcher = new Patcher(Guid, Log);
        Upkeep.Install(patcher);
        Panel.Install(Ui.CreateScope(Guid), patcher);
        patcher.Summary();

        Core.Mods.Switch(Config, affectsSimulation: true, description: "Turns the upkeep rule of this mod on or off.")
            .WhenOn(() => { On = true; Panel.Refresh(); }, () => { On = false; Panel.Refresh(); });
    }

    /// <summary>Logs a problem once per key (a hook that fails would otherwise log every call).</summary>
    internal static void Once(string key, string message)
    {
        if (Logged.Add(key)) L?.LogError(message);
    }
}
