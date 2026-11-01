using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace SpireTrade;

/// <summary>
/// Wraps a synchronous trade in a GameAction, which the host orders by id for every peer.
/// </summary>
public sealed class TradeAction : GameAction
{
    private readonly Player _owner;
    private readonly INetAction _netAction;
    private readonly Action _execute;

    public override ulong OwnerId => _owner.NetId;

    public override GameActionType ActionType => GameActionType.NonCombat;

    public TradeAction(Player owner, INetAction netAction, Action execute)
    {
        _owner = owner;
        _netAction = netAction;
        _execute = execute;
    }

    protected override Task ExecuteAction()
    {
        if (LocalContext.IsMe(_owner) && _netAction is not NetSettleAuctionAction)
        {
            Trade.MarkLocalTradeResolved();
        }
        _execute();
        return Task.CompletedTask;
    }

    public override INetAction ToNetAction() => _netAction;

    public override string ToString() => $"{nameof(TradeAction)} {_netAction}";
}

public struct NetGiveGoldAction : INetAction
{
    public ulong target;
    public int amount;

    public GameAction ToGameAction(Player player)
    {
        NetGiveGoldAction self = this;
        return new TradeAction(player, self, () =>
        {
            if (player.RunState.GetPlayer(self.target) is { } to)
            {
                Trade.GiveGold(player, to, self.amount);
            }
        });
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(target);
        writer.WriteInt(amount);
    }

    public void Deserialize(PacketReader reader)
    {
        target = reader.ReadULong();
        amount = reader.ReadInt();
    }
}

public struct NetGivePotionAction : INetAction
{
    public ulong target;
    public uint slot;

    public GameAction ToGameAction(Player player)
    {
        NetGivePotionAction self = this;
        return new TradeAction(player, self, () =>
        {
            if (player.RunState.GetPlayer(self.target) is { } to)
            {
                Trade.GivePotion(player, to, (int)self.slot);
            }
        });
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(target);
        writer.WriteUInt(slot, 8);
    }

    public void Deserialize(PacketReader reader)
    {
        target = reader.ReadULong();
        slot = reader.ReadUInt(8);
    }
}

public struct NetListCardAction : INetAction
{
    public NetDeckCard card;
    public ModelId cardId;

    public GameAction ToGameAction(Player player)
    {
        NetListCardAction self = this;
        return new TradeAction(player, self, () =>
        {
            // The id check rejects a request whose deck index no longer points at the chosen card.
            if (self.card.DeckIndex < player.Deck.Cards.Count && self.card.ToCardModel(player) is { } model && model.Id.Equals(self.cardId))
            {
                Trade.ListCard(player, model);
            }
        });
    }

    public void Serialize(PacketWriter writer)
    {
        writer.Write(card);
        writer.WriteFullModelId(cardId);
    }

    public void Deserialize(PacketReader reader)
    {
        card = reader.Read<NetDeckCard>();
        cardId = reader.ReadFullModelId();
    }
}

public struct NetBidAction : INetAction
{
    public uint auctionId;
    public int amount;

    public GameAction ToGameAction(Player player)
    {
        NetBidAction self = this;
        return new TradeAction(player, self, () => Trade.Bid(player, self.auctionId, self.amount));
    }

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(auctionId);
        writer.WriteInt(amount);
    }

    public void Deserialize(PacketReader reader)
    {
        auctionId = reader.ReadUInt();
        amount = reader.ReadInt();
    }
}

public struct NetSettleAuctionAction : INetAction
{
    public uint auctionId;

    public GameAction ToGameAction(Player player)
    {
        NetSettleAuctionAction self = this;
        return new TradeAction(player, self, () => Trade.Settle(self.auctionId));
    }

    public void Serialize(PacketWriter writer) => writer.WriteUInt(auctionId);

    public void Deserialize(PacketReader reader) => auctionId = reader.ReadUInt();
}
