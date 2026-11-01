using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;

namespace SpireTrade;

[ModInitializer(nameof(Initialize))]
public static class Main
{
    public const string ModId = "SpireTrade";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());
        Logger.Info("Spire Trade loaded");
    }
}

[HarmonyPatch(typeof(NRun), nameof(NRun._Ready))]
internal static class InstallUiPatch
{
    private static void Postfix(NRun __instance)
    {
        Trade.Reset();
        TradeUi.Install(__instance);
    }
}

[HarmonyPatch(typeof(NMerchantInventory), "UpdateNavigation")]
internal static class AttachToShopPatch
{
    private static void Postfix(NMerchantInventory __instance) => TradeUi.AttachToShop(__instance);
}

[HarmonyPatch(typeof(NRewardsScreen), nameof(NRewardsScreen._Ready))]
internal static class AttachToRewardsPatch
{
    private static void Postfix(NRewardsScreen __instance) => TradeUi.AttachToRewards(__instance);
}

// The deck picker only takes a localized prompt, and a code-only mod ships no localization tables.
[HarmonyPatch(typeof(LocString), nameof(LocString.GetFormattedText))]
internal static class SellPromptPatch
{
    private static bool Prefix(LocString __instance, ref string __result)
    {
        if (__instance.LocTable != TradeUi.PromptTable)
        {
            return true;
        }
        __result = TradeUi.SellPrompt;
        return false;
    }
}

// Runs on every peer at the same point of combat teardown. The first player in the loop is
// handled before anyone has been revived, so every fallen player can be seen as dead at once.
[HarmonyPatch(typeof(Player), nameof(Player.ReviveBeforeCombatEnd))]
internal static class InheritOnCombatEndPatch
{
    private static void Prefix(Player __instance)
    {
        var players = __instance.Creature.CombatState?.Players;
        if (players != null && players.Count > 1 && players[0] == __instance)
        {
            Trade.DistributeFallenPlayers(players);
        }
    }
}

// Travel is itself a host-ordered action, so closing auctions here happens at the same
// position in the action order on every peer.
[HarmonyPatch(typeof(MoveToMapCoordAction), "ExecuteAction")]
internal static class CloseAuctionsOnTravelPatch
{
    private static void Prefix() => Trade.SettleAll();
}

// Shop purchases apply locally before the host sees them, while trades apply in host order.
// Holding purchases until our own trade has executed keeps both orders identical across peers.
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
internal static class HoldPurchasePatch
{
    private static bool Prefix(bool ignoreCost, ref Task<bool> __result)
    {
        if (ignoreCost || !Trade.HasPendingLocalTrade)
        {
            return true;
        }
        __result = Task.FromResult(false);
        return false;
    }
}

[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper))]
internal static class HoldCardRemovalPatch
{
    private static bool Prefix(bool ignoreCost, ref Task<bool> __result)
    {
        if (ignoreCost || !Trade.HasPendingLocalTrade)
        {
            return true;
        }
        __result = Task.FromResult(false);
        return false;
    }
}
