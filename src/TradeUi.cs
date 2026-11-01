using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace SpireTrade;

/// <summary>
/// The trade panel, built from plain Godot controls so the mod needs no .pck file.
/// </summary>
public static class TradeUi
{
    private static readonly int[] BidSteps = { 1, 10, 50 };

    private static bool _tickConnected;
    private static CanvasLayer? _layer;
    private static PanelContainer _panel = null!;
    private static OptionButton _targets = null!;
    private static Label _gold = null!;
    private static VBoxContainer _potions = null!;
    private static VBoxContainer _merchant = null!;
    private static SpinBox _giveAmount = null!;
    private static SpinBox _startPrice = null!;
    private static VBoxContainer _deck = null!;
    private static VBoxContainer _auctions = null!;
    private static Label _log = null!;
    private static readonly Dictionary<uint, Label> _timers = new();
    private static string _targetsSignature = "";
    private static string _listSignature = "";

    public static void Install(NRun run)
    {
        if (RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            return;
        }
        Build(run);
        if (!_tickConnected && Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += Tick;
            _tickConnected = true;
        }
    }

    private static void Build(NRun run)
    {
        _layer = new CanvasLayer { Layer = 100 };
        run.AddChild(_layer);

        var toggle = new Button { Text = "Trade", Position = new Vector2(20, 90) };
        toggle.Pressed += () => _panel.Visible = !_panel.Visible;
        _layer.AddChild(toggle);

        _panel = new PanelContainer { Visible = false, Position = new Vector2(20, 130), CustomMinimumSize = new Vector2(520, 620) };
        _layer.AddChild(_panel);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _panel.AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(root);

        _gold = new Label();
        root.AddChild(_gold);
        _targets = new OptionButton();
        root.AddChild(Row(new Label { Text = "Give to:" }, _targets));

        root.AddChild(new Label { Text = "Potions (click to give)" });
        _potions = new VBoxContainer();
        root.AddChild(_potions);

        _merchant = new VBoxContainer();
        root.AddChild(_merchant);
        _giveAmount = new SpinBox { MinValue = 1, MaxValue = 9999, Value = 10 };
        var giveGold = new Button { Text = "Give gold" };
        giveGold.Pressed += OnGiveGold;
        _merchant.AddChild(Row(_giveAmount, giveGold));

        _merchant.AddChild(new Label { Text = "Auctions" });
        _auctions = new VBoxContainer();
        _merchant.AddChild(_auctions);

        _startPrice = new SpinBox { MinValue = 0, MaxValue = 9999, Value = 25 };
        _merchant.AddChild(Row(new Label { Text = "Sell a card, starting at:" }, _startPrice));
        _deck = new VBoxContainer();
        _merchant.AddChild(_deck);

        _log = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        root.AddChild(_log);

        _targetsSignature = "";
        _listSignature = "";
    }

    private static HBoxContainer Row(params Control[] children)
    {
        var row = new HBoxContainer();
        foreach (Control child in children)
        {
            row.AddChild(child);
        }
        return row;
    }

    private static void Tick()
    {
        try
        {
            if (!RunManager.Instance.IsInProgress || _layer == null || !GodotObject.IsInstanceValid(_layer))
            {
                return;
            }
            if (LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState()) is not { } me)
            {
                return;
            }
            RequestExpiredSettlements(me);
            _layer.Visible = !RunManager.Instance.IsSingleplayerOrFakeMultiplayer;
            if (_panel.Visible)
            {
                Refresh(me);
            }
        }
        catch (Exception e)
        {
            Main.Logger.Error($"Trade UI tick failed: {e}");
        }
    }

    private static void RequestExpiredSettlements(Player me)
    {
        if (RunManager.Instance.NetService.Type != NetGameType.Host)
        {
            return;
        }
        foreach (Auction auction in Trade.Auctions.ToList())
        {
            if (!auction.SettleRequested && Time.GetTicksMsec() >= auction.ExpiresAtMsec)
            {
                auction.SettleRequested = true;
                Send(new NetSettleAuctionAction { auctionId = auction.Id }.ToGameAction(me));
            }
        }
    }

    private static void Refresh(Player me)
    {
        _gold.Text = $"Your gold: {me.Gold}";
        _log.Text = string.Join("\n", Trade.Log);
        _merchant.Visible = Trade.IsAtMerchant(me) && !CombatManager.Instance.IsInProgress;

        List<Player> others = me.RunState.Players.Where(p => p != me).ToList();
        string targets = string.Join(",", others.Select(p => p.NetId));
        if (targets != _targetsSignature)
        {
            _targetsSignature = targets;
            _targets.Clear();
            foreach (Player other in others)
            {
                _targets.AddItem(Trade.NameOf(other));
            }
        }

        // Lists are rebuilt only when their contents change, so a button is never replaced mid-click.
        string potions = string.Join(",", me.PotionSlots.Select(p => p?.Id.ToString() ?? "-"));
        string deck = string.Join(",", me.Deck.Cards.Select(c => c.Title));
        string auctions = string.Join(",", Trade.Auctions.Select(a => $"{a.Id}:{a.Price}:{a.HighBidder?.NetId}"));
        string signature = $"{potions}|{deck}|{auctions}|{me.Gold}|{_merchant.Visible}";
        if (signature != _listSignature)
        {
            _listSignature = signature;
            RebuildPotions(me);
            RebuildAuctions(me);
            RebuildDeck(me);
        }

        foreach (Auction auction in Trade.Auctions)
        {
            if (_timers.TryGetValue(auction.Id, out Label? timer))
            {
                ulong now = Time.GetTicksMsec();
                ulong left = auction.ExpiresAtMsec > now ? (auction.ExpiresAtMsec - now) / 1000 : 0;
                timer.Text = $"{left}s";
            }
        }
    }

    private static void RebuildPotions(Player me)
    {
        Clear(_potions);
        for (int i = 0; i < me.PotionSlots.Count; i++)
        {
            if (me.PotionSlots[i] is not { } potion)
            {
                continue;
            }
            int slot = i;
            var button = new Button { Text = potion.Title.GetFormattedText(), Disabled = potion.IsQueued };
            button.Pressed += () => OnGivePotion(me, slot);
            _potions.AddChild(button);
        }
    }

    private static void RebuildAuctions(Player me)
    {
        Clear(_auctions);
        _timers.Clear();
        if (Trade.Auctions.Count == 0)
        {
            _auctions.AddChild(new Label { Text = "No cards for sale" });
        }
        foreach (Auction auction in Trade.Auctions)
        {
            string leader = auction.HighBidder == null ? "no bids" : Trade.NameOf(auction.HighBidder);
            var timer = new Label();
            _timers[auction.Id] = timer;
            HBoxContainer row = Row(new Label { Text = $"{auction.Title} - {auction.Price}g ({leader})" }, timer);
            if (auction.Seller != me && auction.HighBidder != me)
            {
                if (auction.HighBidder == null)
                {
                    row.AddChild(BidButton(me, auction, auction.Price, "Bid"));
                }
                else
                {
                    foreach (int step in BidSteps)
                    {
                        row.AddChild(BidButton(me, auction, auction.Price + step, $"+{step}"));
                    }
                }
            }
            _auctions.AddChild(row);
        }
    }

    private static Button BidButton(Player me, Auction auction, int amount, string text)
    {
        var button = new Button { Text = text, Disabled = me.Gold < amount };
        uint id = auction.Id;
        button.Pressed += () => SendPending(new NetBidAction { auctionId = id, amount = amount }.ToGameAction(me));
        return button;
    }

    private static void RebuildDeck(Player me)
    {
        Clear(_deck);
        foreach (CardModel card in me.Deck.Cards.Where(c => c.IsRemovable))
        {
            var button = new Button { Text = card.Title };
            button.Pressed += () => OnListCard(me, card);
            _deck.AddChild(button);
        }
    }

    private static void Clear(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static Player? SelectedTarget(Player me)
    {
        List<Player> others = me.RunState.Players.Where(p => p != me).ToList();
        int index = _targets.Selected;
        return index >= 0 && index < others.Count ? others[index] : null;
    }

    private static void OnGiveGold()
    {
        if (LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState()) is not { } me || SelectedTarget(me) is not { } target)
        {
            return;
        }
        int amount = (int)_giveAmount.Value;
        if (amount <= me.Gold)
        {
            SendPending(new NetGiveGoldAction { target = target.NetId, amount = amount }.ToGameAction(me));
        }
    }

    private static void OnGivePotion(Player me, int slot)
    {
        if (SelectedTarget(me) is { HasOpenPotionSlots: true } target)
        {
            bool inCombat = CombatManager.Instance.IsInProgress;
            Send(new NetGivePotionAction { target = target.NetId, slot = (uint)slot, inCombat = inCombat }.ToGameAction(me));
        }
    }

    private static void OnListCard(Player me, CardModel card)
    {
        if (card.Pile != null)
        {
            SendPending(new NetListCardAction
            {
                card = NetDeckCard.FromModel(card),
                cardId = card.Id,
                startPrice = (int)_startPrice.Value
            }.ToGameAction(me));
        }
    }

    private static void SendPending(MegaCrit.Sts2.Core.GameActions.GameAction action)
    {
        Trade.MarkLocalTradePending();
        Send(action);
    }

    private static void Send(MegaCrit.Sts2.Core.GameActions.GameAction action)
    {
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
    }
}
