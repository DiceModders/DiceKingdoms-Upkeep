using DiceModders.ModCore;
using DiceModders.UiKit;
using DiceModders.UiKit.Custom;
using DiceModders.UiKit.Native;

namespace DiceModders.Upkeep;

using Core = DiceModders.ModCore.ModCore;

/// <summary>
/// The Upkeep window, opened from the top bar while a match with the rule on is running. Three switches (pay the guard towers, pay the
/// lighthouses, guard towers eat first) and the Cathedral offering. A switch counts from the next turn; the offering can be made during the
/// build phase and is paid at the next harvest. It redraws on the game's phase changes and on a language change.
/// </summary>
internal static class Panel
{
    private static UiScope _ui;
    private static TopBarButtonHandle _button;
    private static UiWindow _window;
    private static UiButton _offer;
    private static UiText _offerText, _status;

    internal static void Install(UiScope ui)
    {
        _ui = ui;
        _ui?.WhenReady(s => { _button = s.Native.TopBar.AddIconButton("food", Tx.T("panel.tooltip"), Toggle, 21); Refresh(); });
        Core.Texts.LanguageChanged += () => { _button?.SetTooltip(Tx.T("panel.tooltip")); if (_window != null) { _window.Close(); Toggle(); } };
        Core.Match.Started += Refresh;
        Core.Match.Ended += Refresh;
        Core.Match.PhaseEntered += _ => { Refresh(); Redraw(); };
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

    private static void Choose(System.Action set)
    {
        set();
        Choices.Publish();
        Redraw();
    }

    private static void Offer()
    {
        if (Core.Match.Phase != MatchPhase.Build || Choices.OfferedFor(Core.Match.Turn + 1)) return;
        Choices.Offer();
        Redraw();
    }

    private static void Redraw()
    {
        if (_window == null) return;
        var island = Core.Players.Local?.Island;
        int cathedrals = Upkeep.Cathedrals(island);
        bool offered = Choices.OfferedFor(Core.Match.Turn + 1);
        bool canOffer = Core.Match.Phase == MatchPhase.Build && !offered && cathedrals > 0;

        if (_offerText != null)
            _offerText.Value = cathedrals == 0 ? Tx.T("panel.noCathedral")
                : offered ? Tx.T("panel.offered", Upkeep.OfferGold * cathedrals, cathedrals)
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
