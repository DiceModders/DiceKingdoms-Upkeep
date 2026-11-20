using System;
using System.Collections.Generic;
using DiceKingdoms.Game;
using DiceModders.ModCore;
using DiceModders.ModCore.Tools;
using Il2CppInterop.Runtime;
using Unity.Mathematics;

namespace DiceModders.Upkeep;

using Core = DiceModders.ModCore.ModCore;

/// <summary>
/// The upkeep itself. The game simulates in lock-step: every computer runs the same steps with the same numbers, so everything here happens at
/// the same step on every computer and only reads values every computer has (the game state, the lobby rules, the players' published choices).
///  * Disasters phase, inside the game's own food upkeep (Starvation): guard towers eat food, lighthouses use wood or stone; before the houses
///    eat when the player chose "guard towers first", after them otherwise. Unpaid buildings get the game's hungry state (and its icon); the
///    game itself repairs it at the end of the next harvest.
///  * Combat of the same turn: unpaid guard towers and lighthouses do not shoot (their range is set to 0 in the combat's tower list).
///  * Harvest of the next turn: unpaid lighthouses give no fish bonus.
///  * Harvest: a player's Cathedral offering is paid (gold per Cathedral while it lasts) and doubles those Cathedrals' culture.
/// </summary>
internal static class Upkeep
{
    private static LobbyRule<bool> _on;
    private static LobbyRule<int> _towerFood, _lighthouseCost, _offerGold;

    private sealed class IslandState
    {
        public int Turn = -1;                         // the disasters phase this island was charged in
        public readonly HashSet<int> Towers = new();  // unpaid guard towers (building id value)
        public readonly HashSet<int> Lights = new();  // unpaid lighthouses
        public int OfferTurn = -1;                    // the harvest the offering was paid for
        public readonly HashSet<int> Offered = new(); // Cathedrals whose culture is doubled
    }
    private static readonly Dictionary<IntPtr, IslandState> States = new();   // by IslandGrid

    // the Starvation run the prefix saw at its first step (the postfix charges the same island)
    private static Island _pendingIsland;
    private static string _pendingFlags;
    private static int _pendingTurn;

    // the harvest being computed: from HarvestPhase.Reset (before the game computes any yield) to the disasters phase
    private static bool _harvest;
    private static int _harvestTurn;

    internal static bool On => Plugin.On && _on != null && _on.Value && Core.Match.Running;
    internal static int TowerFood => _towerFood?.Value ?? 1;
    internal static int LighthouseCost => _lighthouseCost?.Value ?? 1;
    internal static int OfferGold => _offerGold?.Value ?? 4;

    internal static void Install(Patcher patcher)
    {
        _on = Core.Lobby.Rules.Toggle("upkeep.on", "Upkeep", false,
            "Guard towers eat food and lighthouses use wood or stone every turn; unpaid ones do not shoot, and unpaid lighthouses give no fish bonus. Each player chooses what to pay in the Upkeep window; a choice counts from the next turn.");
        _towerFood = Core.Lobby.Rules.Number("upkeep.towerFood", "Food per guard tower", 1, 1, 5, 1, "Food each guard tower eats per turn.", () => _on.Value);
        _lighthouseCost = Core.Lobby.Rules.Number("upkeep.lighthouseCost", "Wood or stone per lighthouse", 1, 1, 5, 1, "Wood or stone (whichever there is more of) each lighthouse uses per turn.", () => _on.Value);
        _offerGold = Core.Lobby.Rules.Number("upkeep.offerGold", "Cathedral offering (gold)", 4, 1, 20, 1, "Gold per Cathedral for an offering, which doubles its culture at the next harvest.", () => _on.Value);
        var heading = Core.Lobby.Groups.Get("upkeep", "Upkeep");
        _on.InGroup(heading); _towerFood.InGroup(heading); _lighthouseCost.InGroup(heading); _offerGold.InGroup(heading);

        patcher.Patch(typeof(Starvation._Execute_d__1), "MoveNext", typeof(Upkeep), nameof(StarvationPrefix), nameof(StarvationPostfix));
        patcher.Patch(typeof(CombatGrid), "Init", typeof(Upkeep), postfix: nameof(CombatPostfix));
        patcher.Patch(typeof(BuildingTypeApplyModifierInRange), "HasModifier", typeof(Upkeep), postfix: nameof(ModifierPostfix));
        patcher.Patch(typeof(HarvestPhase), "Reset", typeof(Upkeep), prefix: nameof(HarvestResetPrefix));
        patcher.Patch(typeof(BuildingTypeProducerAmplifier), "Yield", typeof(Upkeep), postfix: nameof(YieldPostfix));
        patcher.Patch(typeof(BuildingTypeProducerAmplifier), "AverageFactor", typeof(Upkeep), postfix: nameof(AverageFactorPostfix));
        HookDoAction();

        Core.Match.PhaseEntered += phase => { if (phase == MatchPhase.Disasters) _harvest = false; };
        Core.Match.Started += Clear;
        Core.Match.Ended += Clear;
    }

    private static void Clear() { States.Clear(); _harvest = false; _pendingIsland = null; }

    private static IslandState StateOf(IslandGrid grid)
    {
        if (!States.TryGetValue(grid.Pointer, out var s)) States[grid.Pointer] = s = new IslandState();
        return s;
    }

    private static bool TryState(IslandGrid grid, out IslandState state)
    {
        state = null;
        return grid != null && States.TryGetValue(grid.Pointer, out state);
    }

    /// <summary>The island's buildings in the game's own order (sorted by id; the id comes from the building's position).</summary>
    private static List<(Building.Id id, Building b)> Buildings(IslandGrid grid)
    {
        var list = new List<(Building.Id, Building)>();
        var e = grid.Buildings.GetEnumerator();
        while (e.MoveNext())
        {
            var b = e.Current;
            list.Add((Building.Id.OfPosition(b.transform.position), b));
        }
        return list;
    }

    // ------------------------------------------------------------------ the disasters phase: guard towers and lighthouses

    public static void StarvationPrefix(Starvation._Execute_d__1 __instance)
    {
        _pendingIsland = null;
        try
        {
            if (!On || __instance.__1__state != 0) return;
            var game = __instance.gameState;
            var target = __instance.occurrence?.Target;
            if (game == null || target == null) return;
            var island = game.Island(target);
            if (island == null) return;
            int turn = game.TurnCounter;
            string flags = Choices.Of(FindPlayer(target), turn);
            if (flags[2] == '1') Charge(island, flags, __instance.__4__this, turn);   // guard towers eat before the houses
            else { _pendingIsland = island; _pendingFlags = flags; _pendingTurn = turn; }
        }
        catch (Exception e) { Plugin.Once("starve:pre", "upkeep (before food upkeep): " + e); }
    }

    public static void StarvationPostfix(Starvation._Execute_d__1 __instance)
    {
        var island = _pendingIsland;
        _pendingIsland = null;
        if (island == null) return;
        try { Charge(island, _pendingFlags, __instance.__4__this, _pendingTurn); }
        catch (Exception e) { Plugin.Once("starve:post", "upkeep (after food upkeep): " + e); }
    }

    private static MatchPlayer FindPlayer(Player p)
    {
        foreach (var m in Core.Players.All) if (m.Game != null && m.Game.Pointer == p.Pointer) return m;
        return null;
    }

    /// <summary>Charges one island's guard towers and lighthouses for this turn (once), and marks the unpaid ones hungry.</summary>
    private static void Charge(Island island, string flags, Disaster hungry, int turn)
    {
        var grid = island.grid;
        var stats = island.statistics;
        if (grid == null || stats == null) return;
        var state = StateOf(grid);
        if (state.Turn == turn) return;
        state.Turn = turn;
        state.Towers.Clear();
        state.Lights.Clear();

        var stored = stats.Storage.stored;
        int food = stored.food, wood = stored.wood, stone = stored.stone;
        int usedFood = 0, usedWood = 0, usedStone = 0;
        bool payTowers = flags[0] == '1', payLights = flags[1] == '1';
        var unpaid = new List<Building.Id>();

        foreach (var (id, b) in Buildings(grid))
        {
            var name = b.type.name;
            if (name == BuildingTypeName.GUARD_TOWER || name == BuildingTypeName.GUARD_TOWER_UP)
            {
                int cost = TowerFood;
                if (payTowers && food - usedFood >= cost) usedFood += cost;
                else { state.Towers.Add(id.value); unpaid.Add(id); }
            }
            else if (name == BuildingTypeName.LIGHTHOUSE)
            {
                int cost = LighthouseCost;
                int w = wood - usedWood, s = stone - usedStone;
                if (payLights && Math.Max(w, s) >= cost) { if (w >= s) usedWood += cost; else usedStone += cost; }
                else { state.Lights.Add(id.value); unpaid.Add(id); }
            }
        }

        if (usedFood + usedWood + usedStone > 0)
            stats.RemoveResource(new ResourceAmount { food = usedFood, wood = usedWood, stone = usedStone });
        if (hungry != null) foreach (var id in unpaid) grid.DisasterAffectBuilding(hungry, id);
        if (unpaid.Count > 0 || usedFood + usedWood + usedStone > 0)
            Plugin.L.LogInfo($"turn {turn}: {island.Owner?.UserName} paid {usedFood} food, {usedWood} wood, {usedStone} stone; unpaid: {state.Towers.Count} guard tower(s), {state.Lights.Count} lighthouse(s)");
    }

    // ------------------------------------------------------------------ combat: unpaid towers do not shoot

    private const int TowersField = 0xC8;      // CombatGrid.towers (List<Tower>)
    private const int ListItems = 0x10;        // List<T>._items
    private const int ListSize = 0x18;         // List<T>._size
    private const int ArrayData = 0x20;        // first element of an IL2CPP array
    private const int TowerPosition = 0x14;    // Tower.position (int2); Tower.specifications.range is at 0x0
    private const int TypeTowerHas = 0xC8;     // BuildingType.tower (Opt<BuildingType.Tower>): hasValue, the value from 0xCC
    private const int TypeTowerOffset = 0xD4;  // ...value.combatGridOffset (int2)
    private static int _towerSize;

    public static void CombatPostfix(CombatGrid __instance)
    {
        try
        {
            if (!On || __instance == null) return;
            var grid = __instance.islandGrid;
            if (!TryState(grid, out var state) || state.Turn != Core.Match.Turn || state.Towers.Count + state.Lights.Count == 0) return;

            // where each unpaid building's tower stands on the combat grid (as the game places it: transform.TransformCombat(combatGridOffset))
            var silent = new HashSet<long>();
            foreach (var (id, b) in Buildings(grid))
            {
                if (!state.Towers.Contains(id.value) && !state.Lights.Contains(id.value)) continue;
                IntPtr type = b.type.Pointer;
                if ((Mem.ReadInt(type, TypeTowerHas) & 0xFF) == 0) continue;
                var at = b.transform.TransformCombat(new int2(Mem.ReadInt(type, TypeTowerOffset), Mem.ReadInt(type, TypeTowerOffset + 4)));
                silent.Add(Key(at.x, at.y));
            }
            if (silent.Count == 0) return;

            if (_towerSize == 0)
            {
                uint align = 0;
                _towerSize = IL2CPP.il2cpp_class_value_size(Il2CppClassPointerStore<Tower>.NativeClassPtr, ref align);
            }
            IntPtr list = Mem.ReadPtr(__instance.Pointer, TowersField);
            IntPtr items = Mem.ReadPtr(list, ListItems);
            int count = Mem.ReadInt(list, ListSize), done = 0;
            for (int i = 0; i < count; i++)
            {
                IntPtr t = items + ArrayData + i * _towerSize;
                if (!silent.Contains(Key(Mem.ReadInt(t, TowerPosition), Mem.ReadInt(t, TowerPosition + 4)))) continue;
                Mem.WriteInt(t, 0, 0);   // specifications.range = 0: no target is ever in range
                done++;
            }
            Plugin.L.LogInfo($"turn {state.Turn}: {done} of {silent.Count} unpaid tower(s) silent in this combat");
        }
        catch (Exception e) { Plugin.Once("combat", "upkeep (combat towers): " + e); }
    }

    private static long Key(int x, int y) => ((long)x << 32) | (uint)y;

    // ------------------------------------------------------------------ harvest: unpaid lighthouses give no fish bonus

    public static void ModifierPostfix(Island __0, Building __2, ref bool __result)
    {
        try
        {
            if (!__result || !On || __0 == null || !TryState(__0.grid, out var state) || state.Lights.Count == 0) return;
            // the harvest of turn T + 1 uses the lighthouses paid in the disasters phase of turn T; build and combat show this turn's
            int paidIn = _harvest ? _harvestTurn - 1 : Core.Match.Turn;
            if (state.Turn != paidIn) return;
            foreach (var (id, b) in Buildings(__0.grid))
            {
                if (state.Lights.Contains(id.value)) continue;
                var range = b.type.TryCast<BuildingTypeApplyModifierInRange>();
                if (range != null && range.Affects(b, __2)) return;   // a paid lighthouse (or another source) covers it
            }
            __result = false;
        }
        catch (Exception e) { Plugin.Once("modifier", "upkeep (fish bonus): " + e); }
    }

    // ------------------------------------------------------------------ the Cathedral offering: one of the game's own build actions

    /// <summary>
    /// The offering travels like a market purchase: the window plans the game's own build action REPAIR_BUILDING (type 0) on the Cathedral's
    /// id with this bit set. The game sends it to everybody with its own messages, runs it at once on your screen (preview: the gold drops at the
    /// click) and on every other computer when the build phase ends (BuildPhase.ApplyChanges), which the game waits for. Every computer has
    /// this mod (the rule is used only then), so every computer reads it as an offering. Building ids are (y &lt;&lt; 16 | x), far below this bit.
    /// </summary>
    internal const int OfferFlag = 0x40000000;
    private const int RepairAction = 0;

    // BuildData.DoAction(ref Action) takes the action struct BY REFERENCE, and the action holds object references (Building, arrays,
    // Technology, Player). A Harmony patch on it made the game crash at the first building placed (2026-11-21): Il2CppInterop cannot
    // pass such a struct by reference. So this is a plain native hook that reads the action in place:
    //   bool DoAction(BuildData* this, BuildData.Action* action, MethodInfo* method)
    //   Action: type (int) at 0x0, buildingId (int) at 0xC
    private const int ActionType = 0x0, ActionBuildingId = 0xC;

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate byte DoActionFn(IntPtr self, IntPtr action, IntPtr method);
    private static DoActionFn _doActionHook, _doActionOriginal;   // kept alive for as long as the game runs
    private static object _doActionDetour;

    private static void HookDoAction()
    {
        try
        {
            var method = typeof(BuildData).GetMethod("DoAction");
            var field = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            IntPtr methodInfo = (IntPtr)field.GetValue(null);
            IntPtr code = System.Runtime.InteropServices.Marshal.ReadIntPtr(methodInfo);   // Il2CppMethodInfo.methodPointer is its first field
            _doActionHook = DoActionNative;
            _doActionDetour = BepInEx.Unity.IL2CPP.Hook.INativeDetour.CreateAndApply(code, _doActionHook, out _doActionOriginal);
            Plugin.L.LogInfo("[patch] OK   BuildData.DoAction (native, for the Cathedral offering)");
        }
        catch (Exception e) { Plugin.L.LogError("[patch] FAILED BuildData.DoAction: " + e.Message + " (the Cathedral offering will not work)"); }
    }

    /// <summary>
    /// An offering action is handled here (the game's own code never sees it); every other action goes to the game unchanged. Same checks
    /// and the same result on every computer: the Cathedral exists, it has no offering for the next harvest yet, and the island has the gold.
    /// </summary>
    private static byte DoActionNative(IntPtr self, IntPtr action, IntPtr method)
    {
        try
        {
            if (action != IntPtr.Zero && Mem.ReadInt(action, ActionType) == RepairAction)
            {
                int id = Mem.ReadInt(action, ActionBuildingId);
                if ((id & OfferFlag) != 0) return Offer(new BuildData(self), id & ~OfferFlag) ? (byte)1 : (byte)0;
            }
        }
        catch (Exception e) { Plugin.Once("offer", "upkeep (offering): " + e); return 0; }
        return _doActionOriginal(self, action, method);
    }

    private static bool Offer(BuildData data, int cathedral)
    {
        if (!On) return false;
        var island = data.Island;
        var stats = island?.statistics;
        var grid = island?.grid;
        var game = data.GameState;
        if (stats == null || grid == null || game == null) return false;
        int harvest = game.TurnCounter + 1;   // the culture is doubled at the next harvest
        var state = StateOf(grid);
        if (state.OfferTurn != harvest) { state.OfferTurn = harvest; state.Offered.Clear(); }
        if (state.Offered.Contains(cathedral)) return false;
        bool found = false;
        foreach (var (id, b) in Buildings(grid))
            if (id.value == cathedral && b.type.name == BuildingTypeName.CATHEDRAL) { found = true; break; }
        if (!found || stats.Storage.stored.gold < OfferGold) return false;
        stats.RemoveResource(new ResourceAmount { gold = OfferGold });
        state.Offered.Add(cathedral);
        Plugin.L.LogInfo($"turn {harvest - 1}: {island.Owner?.UserName} offered {OfferGold} gold at a Cathedral (culture doubled at the next harvest)");
        return true;
    }

    /// <summary>The harvest begins (every computer), before the game computes any yield.</summary>
    public static void HarvestResetPrefix(HarvestPhase __instance)
    {
        try
        {
            if (!On) return;
            var game = __instance.GameState;
            if (game == null || game.TurnCounter <= 0) return;
            _harvest = true;
            _harvestTurn = game.TurnCounter;
        }
        catch (Exception e) { Plugin.Once("harvest", "upkeep (harvest start): " + e); }
    }

    /// <summary>The harvest this Cathedral's culture is doubled in, or false. Outside the harvest: the coming one (shown in the tooltips).</summary>
    private static bool Doubled(Island island, Building building)
    {
        if (!TryState(island.grid, out var state) || state.Offered.Count == 0) return false;
        int harvest = _harvest ? _harvestTurn : Core.Match.Turn + 1;
        return state.OfferTurn == harvest && state.Offered.Contains(Building.Id.OfPosition(building.transform.position).value);
    }

    public static void YieldPostfix(Island __0, Building __1, ref ResourceAmount __result)
    {
        try
        {
            // "is null": Building is a struct in the game, and its == operator throws on a null wrapper
            if (!On || __0 is null || __1 is null || !Doubled(__0, __1)) return;
            var r = __result;
            r.culture *= 2;
            __result = r;
        }
        catch (Exception e) { Plugin.Once("yield", "upkeep (Cathedral culture): " + e); }
    }

    /// <summary>
    /// The harvest screen's line for the Cathedrals ("N Churches x F culture") uses AverageFactor, not Yield: with an offering the factor is
    /// worked out again from the (doubled) yields, so the screen shows what you really get.
    /// </summary>
    public static void AverageFactorPostfix(BuildingTypeProducerAmplifier __instance, Island __0, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Building> __1, ref float __result)
    {
        try
        {
            if (!On || __0 is null || __1 is null || __1.Length == 0 || __instance.name != BuildingTypeName.CATHEDRAL) return;
            bool any = false;
            foreach (var b in __1) if (!(b is null) && Doubled(__0, b)) { any = true; break; }
            if (!any) return;
            int count = __instance.AmplifiedCount(__0);
            if (count <= 0) return;
            float sum = 0;
            foreach (var b in __1) if (!(b is null)) sum += __instance.Yield(__0, b).culture;
            __result = sum / __1.Length / count;
        }
        catch (Exception e) { Plugin.Once("average", "upkeep (harvest screen): " + e); }
    }

    // ------------------------------------------------------------------ for the window

    /// <summary>This island's unpaid buildings in its last disasters phase, and whether that was this turn.</summary>
    internal static (int towers, int lights, bool now) Unpaid(Island island) =>
        island?.grid != null && TryState(island.grid, out var s) ? (s.Towers.Count, s.Lights.Count, s.Turn == Core.Match.Turn) : (0, 0, false);

    /// <summary>The Cathedrals on this island (building id values).</summary>
    internal static List<int> Cathedrals(Island island)
    {
        var list = new List<int>();
        if (island?.grid == null) return list;
        foreach (var (id, b) in Buildings(island.grid)) if (b.type.name == BuildingTypeName.CATHEDRAL) list.Add(id.value);
        return list;
    }

    /// <summary>How many of this island's Cathedrals have an offering for the next harvest.</summary>
    internal static int OfferedForNextHarvest(Island island) =>
        island?.grid != null && TryState(island.grid, out var s) && s.OfferTurn == Core.Match.Turn + 1 ? s.Offered.Count : 0;
}
