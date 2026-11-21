using System;
using DiceKingdoms.Interface;
using DiceModders.ModCore;
using DiceModders.ModCore.Tools;
using DiceModders.UiKit;
using DiceModders.UiKit.Custom;
using DiceModders.UiKit.Native;

namespace DiceModders.Upkeep;

using Core = DiceModders.ModCore.ModCore;

/// <summary>
/// The Upkeep window, opened from the top bar while a match with the rule on is running. Three switches (pay the guard towers, pay the
/// lighthouses, guard towers eat first) and the Cathedral offering. A switch counts from the next turn. The offering is a build action like a
/// market purchase: the gold goes at the click, during the build phase. It redraws on the game's phase changes and on a language change.
/// </summary>
internal static class Panel
{
    private static UiScope _ui;
    private static TopBarButtonHandle _button;
    private static UiWindow _window;
    private static UiButton _offer;
    private static UiText _offerText, _status;

    // the game's build interface while the build phase is shown (it holds your island's BuildData); told by the game, never searched for
    private static BuildInterface _build;

    internal static void Install(UiScope ui, Patcher patcher)
    {
        _ui = ui;
        _ui?.WhenReady(s => { _button = s.Native.TopBar.AddIconButton("food", Tx.T("panel.tooltip"), Toggle, 21); Refresh(); });
        Core.Texts.LanguageChanged += () => { _button?.SetTooltip(Tx.T("panel.tooltip")); if (_window != null) { _window.Close(); Toggle(); } };
        Core.Match.Started += Refresh;
        Core.Match.Ended += Refresh;
        Core.Match.PhaseEntered += _ => { Refresh(); Redraw(); };
        patcher.Patch(typeof(BuildInterface), "OnEnable", typeof(Panel), postfix: nameof(BuildShown));
        patcher.Patch(typeof(BuildInterface), "OnDisable", typeof(Panel), postfix: nameof(BuildHidden));
    }

    public static void BuildShown(BuildInterface __instance) { _build = __instance; Redraw(); }
    public static void BuildHidden(BuildInterface __instance)
    {
        if (_build != null && __instance != null && _build.Pointer == __instance.Pointer) _build = null;
        Redraw();
    }

    internal static void Refresh()
    {
        bool show = Upkeep.On;
        _button?.SetVisible(show);
        if (!show) _window?.Close();
    }

    private static void Toggle()
    {
        if (_window != null) { _window.Close(); return; }
        if (!Upkeep.On) return;
        _window = _ui?.Custom.Window(w =>
        {
            w.Title(Tx.T("panel.title")).Size(520f, 470f).Position(60f, 80f).Draggable().Closable();
            w.OnClose(() => { _window = null; _offer = null; _offerText = _status = null; });
            w.Body.Paragraph(Tx.T("panel.help"));   // Paragraph wraps; Text stays on one line and cuts the end
            w.Body.Toggle(Tx.T("panel.payTowers", Upkeep.TowerFood), Choices.PayTowers.Value, v => Choose(() => Choices.PayTowers.Value = v));
            w.Body.Toggle(Tx.T("panel.payLighthouses", Upkeep.LighthouseCost), Choices.PayLighthouses.Value, v => Choose(() => Choices.PayLighthouses.Value = v));
            w.Body.Toggle(Tx.T("panel.towersFirst"), Choices.TowersFirst.Value, v => Choose(() => Choices.TowersFirst.Value = v));
            w.Body.Separator();
            _offerText = w.Body.Paragraph("");
            _offer = w.Body.Button(Tx.T("panel.offer"), Offer);
            w.Body.Separator();
            _status = w.Body.Paragraph("");
        });
        Redraw();
    }

    private static void Choose(Action set)
    {
        set();
        Choices.Publish();
        Redraw();
    }

    /// <summary>Plans one offering action per Cathedral without one (the game drops the ones you cannot pay for).</summary>
    private static void Offer()
    {
        try
        {
            var data = _build?.Data;
            if (data == null || Core.Match.Phase != MatchPhase.Build) return;
            int made = 0;
            foreach (int cathedral in Upkeep.Cathedrals(data.Island))
                if (Core.BuildActions.Plan(data, Upkeep.OfferAction, cathedral)) made++;
            _build?.RefreshButtons();   // what you can still afford
            Plugin.L.LogInfo($"offering: {made} Cathedral(s)");
        }
        catch (Exception e) { Plugin.L.LogWarning("offering: " + e.Message); }
        Redraw();
    }

    private static void Redraw()
    {
        if (_window == null) return;
        var island = Core.Players.Local?.Island;
        int cathedrals = Upkeep.Cathedrals(island).Count;
        int offered = Upkeep.OfferedForNextHarvest(island);
        bool canOffer = _build != null && Core.Match.Phase == MatchPhase.Build && cathedrals > offered;

        if (_offerText != null)
            _offerText.Value = cathedrals == 0 ? Tx.T("panel.noCathedral")
                : offered > 0 ? Tx.T("panel.offered", offered, cathedrals)
                : Tx.T("panel.offerHelp", Upkeep.OfferGold, cathedrals);
        _offer?.Interactable(canOffer);

        if (_status != null)
        {
            var (towers, lights, now) = Upkeep.Unpaid(island);
            _status.Value = !now ? Tx.T("panel.fromNextTurn")
                : towers + lights == 0 ? Tx.T("panel.allPaid")
                : Tx.T("panel.unpaid", towers, lights);
        }
    }
}
