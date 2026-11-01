using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SpireTrade;

public sealed class Auction
{
    public required uint Id { get; init; }
    public required Player Seller { get; init; }
    public required SerializableCard Card { get; init; }
    public required string Title { get; init; }
    public int Price { get; set; }
    public Player? HighBidder { get; set; }
    public ulong ExpiresAtMsec { get; set; }
    public bool SettleRequested { get; set; }
}

/// <summary>
/// All state changes here run inside host-ordered actions or deterministic game flow, so they
/// must make the same decisions on every peer. Timers are the exception: only the host's clock
/// ends an auction, and the other peers use theirs purely for display.
/// </summary>
public static class Trade
{
    public const ulong AuctionMsec = 20_000;
    public const ulong BidExtensionMsec = 10_000;
    private const ulong PendingTimeoutMsec = 5_000;

    private static readonly List<Auction> _auctions = new();
    private static readonly List<string> _log = new();
    private static uint _nextAuctionId;
    private static int _pendingLocal;
    private static ulong _pendingExpiresAtMsec;

    public static IReadOnlyList<Auction> Auctions => _auctions;
    public static IReadOnlyList<string> Log => _log;

    public static bool HasPendingLocalTrade
    {
        get
        {
            if (_pendingLocal > 0 && Time.GetTicksMsec() >= _pendingExpiresAtMsec)
            {
                _pendingLocal = 0;
            }
            return _pendingLocal > 0;
        }
    }

    public static void Reset()
    {
        _auctions.Clear();
        _log.Clear();
        _nextAuctionId = 0;
        _pendingLocal = 0;
    }

    public static void MarkLocalTradePending()
    {
        _pendingLocal++;
        _pendingExpiresAtMsec = Time.GetTicksMsec() + PendingTimeoutMsec;
    }

    public static void MarkLocalTradeResolved()
    {
        if (_pendingLocal > 0)
        {
            _pendingLocal--;
        }
    }

    public static bool IsAtMerchant(Player player) => player.RunState.CurrentRoom is MerchantRoom;

    public static string NameOf(Player player)
    {
        return PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform, player.NetId);
    }

    public static void GiveGold(Player from, Player to, int amount)
    {
        if (from == to || amount <= 0 || from.Gold < amount || !IsAtMerchant(from))
        {
            return;
        }
        from.Gold -= amount;
        to.Gold += amount;
        AddLog($"{NameOf(from)} gave {amount} gold to {NameOf(to)}");
    }

    public static void GivePotion(Player from, Player to, int slot)
    {
        if (from == to || slot >= from.PotionSlots.Count || from.PotionSlots[slot] is not { } potion || !to.HasOpenPotionSlots)
        {
            return;
        }
        string title = potion.Title.GetFormattedText();
        MovePotion(potion, to);
        AddLog($"{NameOf(from)} gave {title} to {NameOf(to)}");
    }

    public static void ListCard(Player seller, CardModel card, int startPrice)
    {
        if (startPrice < 0 || !card.IsRemovable || !IsAtMerchant(seller))
        {
            return;
        }
        // The card and every bid are held in escrow, so nothing can be spent or removed twice.
        var auction = new Auction
        {
            Id = _nextAuctionId++,
            Seller = seller,
            Card = card.ToSerializable(),
            Title = card.Title,
            Price = startPrice,
            ExpiresAtMsec = Time.GetTicksMsec() + AuctionMsec
        };
        card.RemoveFromState();
        _auctions.Add(auction);
        AddLog($"{NameOf(seller)} listed {auction.Title} from {startPrice} gold");
    }

    public static void Bid(Player bidder, uint auctionId, int amount)
    {
        Auction? auction = Find(auctionId);
        if (auction == null || auction.Seller == bidder || auction.HighBidder == bidder)
        {
            return;
        }
        int minimum = auction.HighBidder == null ? auction.Price : auction.Price + 1;
        if (amount < minimum || bidder.Gold < amount)
        {
            return;
        }
        if (auction.HighBidder != null)
        {
            auction.HighBidder.Gold += auction.Price;
        }
        bidder.Gold -= amount;
        auction.Price = amount;
        auction.HighBidder = bidder;
        auction.SettleRequested = false;
        auction.ExpiresAtMsec = Math.Max(auction.ExpiresAtMsec, Time.GetTicksMsec() + BidExtensionMsec);
        AddLog($"{NameOf(bidder)} bid {amount} on {auction.Title}");
    }

    public static void Settle(uint auctionId)
    {
        if (Find(auctionId) is { } auction)
        {
            SettleAuction(auction);
        }
    }

    public static void SettleAll()
    {
        foreach (Auction auction in _auctions.ToList())
        {
            SettleAuction(auction);
        }
    }

    private static void SettleAuction(Auction auction)
    {
        _auctions.Remove(auction);
        Player owner = auction.HighBidder ?? auction.Seller;
        owner.Deck.AddInternal(owner.RunState.LoadCard(auction.Card, owner));
        if (auction.HighBidder == null)
        {
            AddLog($"No bids: {auction.Title} returned to {NameOf(auction.Seller)}");
            return;
        }
        auction.Seller.Gold += auction.Price;
        AddLog($"{NameOf(owner)} won {auction.Title} for {auction.Price} gold");
    }

    public static void DistributeFallenPlayers(IReadOnlyList<Player> players)
    {
        List<Player> survivors = players.Where(p => !p.Creature.IsDead).ToList();
        if (survivors.Count == 0)
        {
            return;
        }
        foreach (Player fallen in players.Where(p => p.Creature.IsDead))
        {
            SplitGold(fallen, survivors);
            foreach (PotionModel potion in fallen.Potions.ToList())
            {
                // OrderBy is stable, so ties always resolve to the same player on every peer.
                Player? heir = survivors.Where(p => p.HasOpenPotionSlots).OrderBy(p => p.Potions.Count()).FirstOrDefault();
                if (heir == null)
                {
                    break;
                }
                MovePotion(potion, heir);
            }
            AddLog($"{NameOf(fallen)} fell: gold and potions passed to the survivors");
        }
    }

    private static void SplitGold(Player fallen, List<Player> survivors)
    {
        int share = fallen.Gold / survivors.Count;
        int leftover = fallen.Gold % survivors.Count;
        foreach (Player survivor in survivors)
        {
            survivor.Gold += share;
        }
        for (; leftover > 0; leftover--)
        {
            survivors.OrderBy(p => p.Gold).First().Gold++;
        }
        fallen.Gold = 0;
    }

    // A potion's owner can never change, so the heir receives a fresh copy. Potions hold no
    // per-instance state beyond their id, which is also all the game saves for them.
    private static void MovePotion(PotionModel potion, Player to)
    {
        PotionModel copy = ModelDb.GetById<PotionModel>(potion.Id).ToMutable();
        potion.Discard();
        to.AddPotionInternal(copy);
    }

    private static Auction? Find(uint auctionId) => _auctions.FirstOrDefault(a => a.Id == auctionId);

    private static void AddLog(string line)
    {
        _log.Add(line);
        if (_log.Count > 6)
        {
            _log.RemoveAt(0);
        }
        Main.Logger.Info(line);
    }
}
