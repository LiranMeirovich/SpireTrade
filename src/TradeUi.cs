using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SpireTrade;

/// <summary>
/// The trade windows and the buttons that open them. Built in code from Godot controls and the
/// game's own fonts, cards and deck picker, so the mod needs no .pck file. Godot controls only
/// react to the raw ui_accept action, so confirming with a controller and moving focus between
/// buttons are both wired up here.
/// </summary>
public static class TradeUi
{
    public const string PromptTable = "spire_trade";
    public const string SellPrompt = "Choose a card to put up for auction";

    private enum TradeWindow
    {
        Auctions,
        GiveGold,
        GivePotions
    }

    private const float CardScale = 0.55f;
    private const string FontPath = "res://themes/kreon_bold_shared.tres";
    private const string GoldIconPath = "res://images/atlases/ui_atlas.sprites/top_bar/top_bar_gold.tres";

    // Where the shop buttons sit on screen once the shop's slots have finished sliding into place.
    private const float ShopSlotsRestingY = 80f;
    private static readonly Vector2 ShopButtonsPosition = new(1395, 925);
    private static readonly Vector2 ShopButtonSize = new(190, 44);

    private static readonly Color Gold = new(0.96f, 0.80f, 0.33f);
    private static readonly Color Cream = new(1f, 0.96f, 0.88f);
    private static readonly Color Muted = new(0.70f, 0.78f, 0.76f);
    private static readonly Color PanelColor = new(0.09f, 0.17f, 0.20f, 0.98f);
    private static readonly Color RowColor = new(0.15f, 0.27f, 0.25f);
    private static readonly Color BorderColor = new(0.72f, 0.56f, 0.25f);
    private static readonly Color ButtonColor = new(0.20f, 0.34f, 0.38f);
    private static readonly Color ButtonHover = new(0.28f, 0.45f, 0.50f);
    private static readonly Color Confirm = new(0.25f, 0.50f, 0.24f);
    private static readonly Color ConfirmHover = new(0.33f, 0.62f, 0.30f);

    private static readonly Dictionary<uint, Label> _timers = new();
    private static readonly Dictionary<uint, Control> _cardHolders = new();
    private static readonly Dictionary<uint, int> _bidAmounts = new();
    private static readonly List<List<Button>> _focusRows = new();
    private static readonly Dictionary<string, Button> _buttonsByKey = new();

    private static bool _tickConnected;
    private static bool _pickingCard;
    private static bool _isOpen;
    private static bool _dirty;
    private static ulong _lastActivationFrame;
    private static CanvasLayer? _layer;
    private static Theme _theme = null!;
    private static Control _screen = null!;
    private static PanelContainer _panel = null!;
    private static VBoxContainer _content = null!;
    private static Label _auctionNotice = null!;
    private static NMerchantInventory? _inventory;
    private static Button? _auctionsButton;
    private static Button? _giveGoldButton;
    private static NRewardsScreen? _rewardsScreen;
    private static Button? _potionsButton;
    private static Button? _returnFocus;
    private static TradeWindow _window;
    private static string _signature = "";
    private static ulong _targetId;
    private static int _giveAmount = 10;

    private static bool IsMultiplayer => !RunManager.Instance.IsSingleplayerOrFakeMultiplayer;

    public static void Install(NRun run)
    {
        if (!IsMultiplayer)
        {
            return;
        }
        Build(run);
        NHotkeyManager.Instance?.PushHotkeyPressedBinding(MegaInput.peek, OnTradeHotkey);
        _layer!.TreeExiting += () => NHotkeyManager.Instance?.RemoveHotkeyPressedBinding(MegaInput.peek, OnTradeHotkey);
        if (!_tickConnected && Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += Tick;
            _tickConnected = true;
        }
    }

    /// <summary>
    /// Adds the shop's two buttons on first call. The shop rebuilds its controller navigation after
    /// every purchase, so the links from the card removal slot are restored on each call.
    /// </summary>
    public static void AttachToShop(NMerchantInventory inventory)
    {
        if (!IsMultiplayer || inventory.GetType() != typeof(NMerchantInventory))
        {
            return;
        }
        if (inventory.GetNodeOrNull<Control>("%MerchantCardRemoval") is not { } removal
            || inventory.GetNodeOrNull<Control>("%SlotsContainer") is not { } slots)
        {
            return;
        }
        if (_inventory != inventory || _auctionsButton == null || !GodotObject.IsInstanceValid(_auctionsButton))
        {
            _inventory = inventory;
            _auctionsButton = MakeScreenButton("Trade cards", () => Open(TradeWindow.Auctions));
            _giveGoldButton = MakeScreenButton("Give gold", () => Open(TradeWindow.GiveGold));
            // The slots slide in from off screen, so position relative to where they come to rest.
            var position = new Vector2(ShopButtonsPosition.X - slots.GlobalPosition.X, ShopButtonsPosition.Y - ShopSlotsRestingY);
            PlaceShopButton(slots, _auctionsButton, position);
            PlaceShopButton(slots, _giveGoldButton, position + new Vector2(0, ShopButtonSize.Y + 6));
        }

        Button auctions = _auctionsButton;
        Button giveGold = _giveGoldButton!;
        NodePath removalPath = removal.GetPath();
        removal.FocusNeighborBottom = auctions.GetPath();
        auctions.FocusNeighborTop = removalPath;
        auctions.FocusNeighborBottom = giveGold.GetPath();
        auctions.FocusNeighborLeft = removal.FocusNeighborLeft;
        auctions.FocusNeighborRight = auctions.GetPath();
        giveGold.FocusNeighborTop = auctions.GetPath();
        giveGold.FocusNeighborBottom = giveGold.GetPath();
        giveGold.FocusNeighborLeft = removal.FocusNeighborLeft;
        giveGold.FocusNeighborRight = giveGold.GetPath();
    }

    /// <summary>
    /// Adds the potion button to the rewards shown after a fight. It stays out of the rewards
    /// list's focus order, since that list scrolls to follow focus; controllers use the hotkey.
    /// </summary>
    public static void AttachToRewards(NRewardsScreen screen)
    {
        if (!IsMultiplayer || RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is not CombatRoom)
        {
            return;
        }
        _rewardsScreen = screen;
        _potionsButton = MakeScreenButton("Give a potion", () => Open(TradeWindow.GivePotions));
        _potionsButton.FocusMode = Control.FocusModeEnum.None;
        _potionsButton.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _potionsButton.OffsetLeft = -130;
        _potionsButton.OffsetRight = 130;
        _potionsButton.OffsetTop = -112;
        _potionsButton.OffsetBottom = -60;
        screen.AddChild(_potionsButton);
    }

    private static void PlaceShopButton(Control parent, Button button, Vector2 position)
    {
        parent.AddChild(button);
        button.Position = position;
        button.Size = ShopButtonSize;
        button.CustomMinimumSize = ShopButtonSize;
    }

    private static Button MakeScreenButton(string text, Action action)
    {
        Button button = MakeButton(text, text, action);
        button.Theme = _theme;
        button.AddThemeStyleboxOverride("normal", Box(ButtonColor, Gold, 2, 8, 6));
        return button;
    }

    private static void Build(NRun run)
    {
        _isOpen = false;
        _pickingCard = false;
        _signature = "";
        _timers.Clear();
        _cardHolders.Clear();
        _bidAmounts.Clear();

        _layer = new CanvasLayer { Layer = 100 };
        run.AddChild(_layer);
        _theme = BuildTheme();

        _auctionNotice = new Label { Theme = _theme, Visible = false, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
        _auctionNotice.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _auctionNotice.OffsetLeft = -500;
        _auctionNotice.OffsetRight = 500;
        _auctionNotice.OffsetTop = 84;
        _auctionNotice.AddThemeColorOverride("font_color", Gold);
        _layer.AddChild(_auctionNotice);

        _screen = new ColorRect { Color = new Color(0, 0, 0, 0.65f), Visible = false, Theme = _theme };
        _screen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _layer.AddChild(_screen);

        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _screen.AddChild(center);

        _panel = new PanelContainer();
        _panel.AddThemeStyleboxOverride("panel", Box(PanelColor, BorderColor, 4, 14, 20));
        center.AddChild(_panel);

        // FollowFocus keeps the selected button on screen while navigating with a controller.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _panel.AddChild(scroll);
        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_content);
    }

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = 26 };
        if (ResourceLoader.Load(FontPath) is Font font)
        {
            theme.DefaultFont = font;
        }
        theme.SetColor("font_color", "Label", Cream);
        theme.SetColor("font_outline_color", "Label", Colors.Black);
        theme.SetConstant("outline_size", "Label", 5);
        theme.SetColor("font_color", "Button", Cream);
        theme.SetColor("font_hover_color", "Button", Cream);
        theme.SetColor("font_focus_color", "Button", Cream);
        theme.SetColor("font_pressed_color", "Button", Gold);
        theme.SetColor("font_disabled_color", "Button", new Color(0.55f, 0.58f, 0.58f));
        theme.SetColor("font_outline_color", "Button", Colors.Black);
        theme.SetConstant("outline_size", "Button", 5);
        theme.SetStylebox("normal", "Button", Box(ButtonColor, BorderColor, 2, 8, 12));
        theme.SetStylebox("hover", "Button", Box(ButtonHover, Gold, 3, 8, 12));
        theme.SetStylebox("pressed", "Button", Box(RowColor, Gold, 3, 8, 12));
        theme.SetStylebox("disabled", "Button", Box(new Color(0.13f, 0.18f, 0.20f), new Color(0.3f, 0.3f, 0.28f), 2, 8, 12));
        theme.SetStylebox("focus", "Button", Box(new Color(0, 0, 0, 0), Gold, 4, 8, 12));
        return theme;
    }

    private static StyleBoxFlat Box(Color fill, Color border, int borderWidth, int radius, int padding)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = border };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        box.SetContentMarginAll(padding);
        return box;
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
            RefreshScreenButtons(me);
            if (_isOpen)
            {
                if (IsWindowAvailable(_window))
                {
                    Refresh(me);
                }
                else
                {
                    Close();
                }
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

    private static void RefreshScreenButtons(Player me)
    {
        int count = Trade.Auctions.Count;
        Auction? first = Trade.Auctions.FirstOrDefault();
        _auctionNotice.Visible = first != null && !_isOpen && !_pickingCard;
        if (first != null)
        {
            string others = count > 1 ? $" (+{count - 1} more)" : "";
            string bid = first.HighBidder == null ? "no bids yet" : $"top bid {first.Price}";
            _auctionNotice.Text = $"Auction: {first.Title}, {bid}{others}";
        }

        NControllerManager? controller = NControllerManager.Instance;
        Texture2D? glyph = controller is { ShouldShowInputGlyphs: true } ? controller.GetHotkeyIcon(MegaInput.peek) : null;
        if (IsValid(_auctionsButton))
        {
            _auctionsButton!.Text = count > 0 ? $"Trade cards ({count})" : "Trade cards";
            SetGlyph(_auctionsButton, glyph);
        }
        if (IsValid(_potionsButton))
        {
            SetGlyph(_potionsButton!, glyph);
        }
    }

    private static void SetGlyph(Button button, Texture2D? glyph)
    {
        button.Icon = glyph;
        button.ExpandIcon = true;
    }

    private static bool IsValid(GodotObject? node) => node != null && GodotObject.IsInstanceValid(node);

    private static bool IsWindowAvailable(TradeWindow window)
    {
        if (window == TradeWindow.GivePotions)
        {
            return IsValid(_rewardsScreen);
        }
        return NMerchantRoom.Instance?.Inventory is { IsOpen: true };
    }

    private static void OnTradeHotkey()
    {
        if (_isOpen)
        {
            Close();
        }
        else if (_pickingCard)
        {
            return;
        }
        else if (IsValid(_rewardsScreen) && ActiveScreenContext.Instance.IsCurrent(_rewardsScreen!))
        {
            Open(TradeWindow.GivePotions);
        }
        else if (NMerchantRoom.Instance?.Inventory is { IsOpen: true } inventory && ActiveScreenContext.Instance.IsCurrent(inventory))
        {
            Open(TradeWindow.Auctions);
        }
    }

    private static void Open(TradeWindow window)
    {
        if (_isOpen)
        {
            return;
        }
        _returnFocus = _screen.GetViewport()?.GuiGetFocusOwner() as Button;
        _window = window;
        _isOpen = true;
        _screen.Visible = true;
        _signature = "";
        _panel.CustomMinimumSize = window == TradeWindow.Auctions ? new Vector2(980, 880) : new Vector2(660, 560);
        // Blocks the game's hotkeys while open. Our bindings go on top, so they still fire.
        NHotkeyManager? hotkeys = NHotkeyManager.Instance;
        hotkeys?.AddBlockingScreen(_layer!);
        hotkeys?.PushHotkeyPressedBinding(MegaInput.cancel, Close);
        hotkeys?.PushHotkeyPressedBinding(MegaInput.pauseAndBack, Close);
        hotkeys?.PushHotkeyPressedBinding(MegaInput.peek, Close);
    }

    private static void Close()
    {
        if (!_isOpen)
        {
            return;
        }
        _isOpen = false;
        _screen.Visible = false;
        NHotkeyManager? hotkeys = NHotkeyManager.Instance;
        hotkeys?.RemoveHotkeyPressedBinding(MegaInput.cancel, Close);
        hotkeys?.RemoveHotkeyPressedBinding(MegaInput.pauseAndBack, Close);
        hotkeys?.RemoveHotkeyPressedBinding(MegaInput.peek, Close);
        hotkeys?.RemoveBlockingScreen(_layer!);
        _screen.GetViewport()?.GuiReleaseFocus();
        if (NControllerManager.Instance is { ShouldShowInputGlyphs: true } && IsValid(_returnFocus))
        {
            _returnFocus!.GrabFocus();
        }
    }

    private static void Refresh(Player me)
    {
        string signature = BuildSignature(me);
        if (_dirty || signature != _signature)
        {
            _dirty = false;
            _signature = signature;
            Rebuild(me);
        }
        foreach (Auction auction in Trade.Auctions)
        {
            if (_timers.TryGetValue(auction.Id, out Label? timer))
            {
                ulong now = Time.GetTicksMsec();
                ulong left = auction.ExpiresAtMsec > now ? (auction.ExpiresAtMsec - now) / 1000 : 0;
                timer.Text = $"{left}s left";
            }
        }
        KeepControllerFocus();
    }

    private static string BuildSignature(Player me)
    {
        string potions = string.Join(",", me.PotionSlots.Select(p => p?.Id.ToString() ?? "-"));
        string others = string.Join(",", me.RunState.Players.Select(p => $"{p.NetId}:{p.HasOpenPotionSlots}"));
        string auctions = string.Join(",", Trade.Auctions.Select(a => $"{a.Id}:{a.Price}:{a.HighBidder?.NetId}"));
        return $"{_window}|{potions}|{others}|{auctions}|{me.Gold}|{string.Join(",", Trade.Log)}";
    }

    private static void KeepControllerFocus()
    {
        if (NControllerManager.Instance is not { ShouldShowInputGlyphs: true })
        {
            return;
        }
        Control? owner = _screen.GetViewport()?.GuiGetFocusOwner();
        if (owner == null || !_screen.IsAncestorOf(owner))
        {
            // The first row is the Close button, so start on the window's first real choice.
            List<Button>? start = _focusRows.Skip(1).FirstOrDefault() ?? _focusRows.FirstOrDefault();
            start?.FirstOrDefault()?.GrabFocus();
        }
    }

    private static void Rebuild(Player me)
    {
        string? focusedKey = _screen.GetViewport()?.GuiGetFocusOwner()?.GetMeta("key", "").AsString();

        // Card nodes survive rebuilds so they don't reload and flicker on every bid.
        foreach (Control holder in _cardHolders.Values)
        {
            holder.GetParent()?.RemoveChild(holder);
        }
        foreach (Node child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }
        _focusRows.Clear();
        _buttonsByKey.Clear();
        _timers.Clear();
        RemoveFinishedCards();

        List<Player> others = me.RunState.Players.Where(p => p != me).ToList();
        if (others.All(p => p.NetId != _targetId) && others.Count > 0)
        {
            _targetId = others[0].NetId;
        }

        switch (_window)
        {
            case TradeWindow.Auctions:
                BuildHeader("Trade cards", me);
                BuildAuctions(_content, me);
                break;
            case TradeWindow.GiveGold:
                BuildHeader("Give gold", me);
                BuildTargets(_content, others);
                BuildGiveGold(_content, me);
                break;
            case TradeWindow.GivePotions:
                BuildHeader("Give a potion", me);
                BuildTargets(_content, others);
                BuildPotions(_content, me);
                break;
        }
        BuildLog();
        LinkFocus();

        if (!string.IsNullOrEmpty(focusedKey) && _buttonsByKey.TryGetValue(focusedKey, out Button? previous))
        {
            previous.GrabFocus();
        }
    }

    private static void RemoveFinishedCards()
    {
        foreach (uint id in _cardHolders.Keys.Where(id => Trade.Auctions.All(a => a.Id != id)).ToList())
        {
            _cardHolders[id].QueueFree();
            _cardHolders.Remove(id);
            _bidAmounts.Remove(id);
        }
    }

    private static void BuildHeader(string text, Player me)
    {
        var title = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 46);
        title.AddThemeColorOverride("font_color", Gold);
        var goldLabel = new Label { Text = me.Gold.ToString() };
        goldLabel.AddThemeFontSizeOverride("font_size", 34);
        goldLabel.AddThemeColorOverride("font_color", Gold);

        var row = new HBoxContainer();
        row.AddChild(title);
        if (ResourceLoader.Load(GoldIconPath) is Texture2D icon)
        {
            row.AddChild(new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(44, 44)
            });
        }
        row.AddChild(goldLabel);
        Button close = MakeButton("Close", "close", Close);
        row.AddChild(close);
        _content.AddChild(row);
        _focusRows.Add(new List<Button> { close });
    }

    private static void BuildTargets(VBoxContainer parent, List<Player> others)
    {
        parent.AddChild(Heading("Give to"));
        var row = new HBoxContainer();
        var focusRow = new List<Button>();
        foreach (Player other in others)
        {
            ulong id = other.NetId;
            Button button = MakeButton(Trade.NameOf(other), $"target{id}", () =>
            {
                _targetId = id;
                _dirty = true;
            });
            if (id == _targetId)
            {
                button.AddThemeStyleboxOverride("normal", Box(Confirm, Gold, 3, 8, 12));
            }
            row.AddChild(button);
            focusRow.Add(button);
        }
        parent.AddChild(row);
        _focusRows.Add(focusRow);
    }

    private static void BuildPotions(VBoxContainer parent, Player me)
    {
        parent.AddChild(Heading("Your potions"));
        Player? target = me.RunState.GetPlayer(_targetId);
        bool any = false;
        for (int i = 0; i < me.PotionSlots.Count; i++)
        {
            if (me.PotionSlots[i] is not { } potion)
            {
                continue;
            }
            any = true;
            int slot = i;
            Button button = MakeButton(potion.Title.GetFormattedText(), $"potion{slot}", () => OnGivePotion(me, slot));
            button.Icon = potion.Image;
            button.ExpandIcon = true;
            button.CustomMinimumSize = new Vector2(0, 76);
            button.Alignment = HorizontalAlignment.Left;
            button.Disabled = target is not { HasOpenPotionSlots: true };
            parent.AddChild(button);
            _focusRows.Add(new List<Button> { button });
        }
        if (!any)
        {
            parent.AddChild(new Label { Text = "No potions to give.", Modulate = Muted });
        }
        else if (target is { HasOpenPotionSlots: false })
        {
            parent.AddChild(new Label { Text = "Their potion belt is full.", Modulate = Muted });
        }
    }

    private static void BuildGiveGold(VBoxContainer parent, Player me)
    {
        parent.AddChild(Heading("Amount"));
        int max = me.Gold;
        _giveAmount = Math.Clamp(_giveAmount, Math.Min(1, max), Math.Max(1, max));
        HBoxContainer row = Stepper("give", _giveAmount, Math.Min(1, max), max, value => _giveAmount = value, out List<Button> focusRow);
        Button give = MakeButton("Give", "giveGold", () => OnGiveGold(me), primary: true);
        give.Disabled = max < 1 || me.RunState.GetPlayer(_targetId) == null;
        row.AddChild(give);
        focusRow.Add(give);
        parent.AddChild(row);
        _focusRows.Add(focusRow);
    }

    private static void BuildAuctions(VBoxContainer parent, Player me)
    {
        Button sell = MakeButton("Sell a card", "sell", () => TaskHelper.RunSafely(PickCardToSell(me)), primary: true);
        parent.AddChild(sell);
        _focusRows.Add(new List<Button> { sell });

        if (Trade.Auctions.Count == 0)
        {
            parent.AddChild(new Label { Text = "No cards for sale. Bidding starts at 0 gold.", Modulate = Muted });
        }
        foreach (Auction auction in Trade.Auctions)
        {
            BuildAuctionRow(parent, auction, me);
        }
    }

    private static void BuildAuctionRow(VBoxContainer parent, Auction auction, Player me)
    {
        var frame = new PanelContainer();
        frame.AddThemeStyleboxOverride("panel", Box(RowColor, BorderColor, 2, 10, 10));
        parent.AddChild(frame);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        frame.AddChild(row);
        row.AddChild(GetCardHolder(auction));

        var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(info);
        info.AddChild(new Label { Text = $"{auction.Title} from {Trade.NameOf(auction.Seller)}" });
        string bid = auction.HighBidder == null ? "No bids yet" : $"Top bid: {auction.Price} gold by {Trade.NameOf(auction.HighBidder)}";
        var bidLabel = new Label { Text = bid };
        bidLabel.AddThemeColorOverride("font_color", Gold);
        info.AddChild(bidLabel);
        var timer = new Label { Modulate = Muted };
        info.AddChild(timer);
        _timers[auction.Id] = timer;

        if (auction.Seller == me)
        {
            info.AddChild(new Label { Text = "Your card", Modulate = Muted });
            return;
        }
        int min = Trade.MinimumBid(auction);
        int max = Trade.MaximumBid(auction, me);
        if (max < min)
        {
            info.AddChild(new Label { Text = "Not enough gold to bid", Modulate = Muted });
            return;
        }
        uint id = auction.Id;
        int amount = Math.Clamp(_bidAmounts.GetValueOrDefault(id, min), min, max);
        _bidAmounts[id] = amount;
        HBoxContainer controls = Stepper($"bid{id}", amount, min, max, value => _bidAmounts[id] = value, out List<Button> focusRow);
        Button confirm = MakeButton("Bid", $"confirm{id}", () => SendPending(new NetBidAction { auctionId = id, amount = _bidAmounts[id] }.ToGameAction(me)), primary: true);
        controls.AddChild(confirm);
        focusRow.Add(confirm);
        info.AddChild(controls);
        _focusRows.Add(focusRow);
    }

    private static Control GetCardHolder(Auction auction)
    {
        if (_cardHolders.TryGetValue(auction.Id, out Control? holder))
        {
            return holder;
        }
        Vector2 size = NCard.defaultSize * CardScale;
        holder = new Control { CustomMinimumSize = size, MouseFilter = Control.MouseFilterEnum.Ignore };
        _cardHolders[auction.Id] = holder;
        // The card must be in the tree before its visuals update, so finish once the row is attached.
        Control created = holder;
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(created) && NCard.Create(auction.Card) is { } card)
            {
                created.AddChild(card);
                card.Position = size / 2;
                card.Scale = Vector2.One * CardScale;
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            }
        }).CallDeferred();
        return holder;
    }

    private static void BuildLog()
    {
        if (Trade.Log.Count == 0)
        {
            return;
        }
        _content.AddChild(new HSeparator());
        var log = new Label { Text = string.Join("\n", Trade.Log), Modulate = Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        log.AddThemeFontSizeOverride("font_size", 22);
        _content.AddChild(log);
    }

    private static HBoxContainer Stepper(string key, int value, int min, int max, Action<int> set, out List<Button> focusRow)
    {
        var row = new HBoxContainer();
        var buttons = new List<Button>();
        var label = new Label
        {
            Text = value.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(84, 0)
        };
        label.AddThemeColorOverride("font_color", Gold);

        void AddStep(int step)
        {
            Button button = MakeButton(step > 0 ? $"+{step}" : step.ToString(), $"{key}{step}", () =>
            {
                set(Math.Clamp(value + step, min, max));
                _dirty = true;
            });
            button.Disabled = step > 0 ? value >= max : value <= min;
            row.AddChild(button);
            buttons.Add(button);
        }

        AddStep(-10);
        AddStep(-1);
        row.AddChild(label);
        AddStep(1);
        AddStep(10);
        focusRow = buttons;
        return row;
    }

    private static Label Heading(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 30);
        label.AddThemeColorOverride("font_color", Gold);
        return label;
    }

    private static Button MakeButton(string text, string key, Action action, bool primary = false)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.All };
        button.SetMeta("key", key);
        if (primary)
        {
            button.AddThemeStyleboxOverride("normal", Box(Confirm, Gold, 2, 8, 12));
            button.AddThemeStyleboxOverride("hover", Box(ConfirmHover, Gold, 3, 8, 12));
        }
        void Activate()
        {
            // A single press can arrive as both a click and the controller's select action.
            ulong frame = Engine.GetProcessFrames();
            if (!button.Disabled && frame != _lastActivationFrame)
            {
                _lastActivationFrame = frame;
                action();
            }
        }
        button.Pressed += Activate;
        button.GuiInput += input =>
        {
            if (input.IsActionPressed(MegaInput.select))
            {
                button.AcceptEvent();
                Activate();
            }
        };
        _buttonsByKey[key] = button;
        return button;
    }

    // Explicit neighbours stop controller navigation from wandering onto the game UI behind the screen.
    private static void LinkFocus()
    {
        List<List<Button>> rows = _focusRows.Where(r => r.Count > 0).ToList();
        for (int r = 0; r < rows.Count; r++)
        {
            List<Button> row = rows[r];
            List<Button> above = rows[(r + rows.Count - 1) % rows.Count];
            List<Button> below = rows[(r + 1) % rows.Count];
            for (int c = 0; c < row.Count; c++)
            {
                Button button = row[c];
                Button leftOf = row[(c + row.Count - 1) % row.Count];
                Button rightOf = row[(c + 1) % row.Count];
                button.FocusNeighborLeft = button.GetPathTo(leftOf);
                button.FocusNeighborRight = button.GetPathTo(rightOf);
                button.FocusNeighborTop = button.GetPathTo(above[Math.Min(c, above.Count - 1)]);
                button.FocusNeighborBottom = button.GetPathTo(below[Math.Min(c, below.Count - 1)]);
                button.FocusPrevious = button.GetPathTo(leftOf);
                button.FocusNext = button.GetPathTo(rightOf);
            }
        }
    }

    private static async Task PickCardToSell(Player me)
    {
        List<CardModel> cards = me.Deck.Cards.Where(c => c.IsRemovable).ToList();
        if (cards.Count == 0)
        {
            return;
        }
        Close();
        _pickingCard = true;
        try
        {
            var prefs = new CardSelectorPrefs(new LocString(PromptTable, "SELL"), 1)
            {
                Cancelable = true,
                RequireManualConfirmation = true
            };
            NDeckCardSelectScreen screen = NDeckCardSelectScreen.Create(cards, prefs);
            NOverlayStack.Instance?.Push(screen);
            CardModel? card = (await screen.CardsSelected()).FirstOrDefault();
            if (card?.Pile != null)
            {
                SendPending(new NetListCardAction { card = NetDeckCard.FromModel(card), cardId = card.Id }.ToGameAction(me));
            }
        }
        finally
        {
            _pickingCard = false;
        }
        Open(TradeWindow.Auctions);
    }

    private static void OnGiveGold(Player me)
    {
        if (_giveAmount >= 1 && _giveAmount <= me.Gold)
        {
            SendPending(new NetGiveGoldAction { target = _targetId, amount = _giveAmount }.ToGameAction(me));
        }
    }

    private static void OnGivePotion(Player me, int slot)
    {
        if (me.RunState.GetPlayer(_targetId) is { HasOpenPotionSlots: true })
        {
            Send(new NetGivePotionAction { target = _targetId, slot = (uint)slot }.ToGameAction(me));
        }
    }

    private static void SendPending(GameAction action)
    {
        Trade.MarkLocalTradePending();
        Send(action);
    }

    private static void Send(GameAction action)
    {
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);
    }
}
