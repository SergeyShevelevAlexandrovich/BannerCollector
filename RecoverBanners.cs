using System.Collections.Generic;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace BannerCollector
{
    /// <summary>
    /// Chat command "/recoverbanners".
    ///
    /// PURPOSE
    /// -------
    /// Grants every enemy banner the player is currently entitled to, in the exact quantity
    /// vanilla Terraria itself would have granted, by re-deriving the result from the game's
    /// own authoritative kill-count data. Intended as a recovery tool for cases where an
    /// external bug (e.g. in the host mod) has wiped the player's banner collection.
    ///
    /// THE NATIVE VANILLA RULE THIS REPLICATES
    /// -----------------------------------------
    /// Since Journey's End (which includes 1.4.4), a banner is NOT a random drop. It is
    /// awarded deterministically the instant a per-BannerID kill counter becomes an exact
    /// multiple of that banner's required kill count. For a given BannerID:
    ///
    ///     bannersEarned = NPC.killCount[bannerId] / ItemID.Sets.KillsToBanner[bannerItemType]
    ///
    /// Two details are commonly implemented incorrectly and are handled correctly here:
    ///
    /// 1) SEVERAL NPC TYPES CAN SHARE ONE BANNER.
    ///    Killing any zombie reskin, for example, all count toward the same "Zombie" banner.
    ///    NPC.killCount is indexed by BannerID (Item.NPCtoBanner(npcType)), NOT by NPC.type.
    ///    Iterating over NPC types instead of BannerIDs would double- or triple-count kills
    ///    that share a banner and hand out the wrong amount.
    ///
    /// 2) THE KILL THRESHOLD IS NOT ALWAYS 50.
    ///    Most enemies need 50 kills, but several (mostly Old One's Army enemies, e.g. the
    ///    Etherian Goblin at 1000, or the Kobold at 200) use a different value. Hardcoding 50
    ///    would silently miscalculate those banners. ItemID.Sets.KillsToBanner is the exact
    ///    same lookup table the base game itself reads from, so this command can never
    ///    disagree with vanilla's own math - including for modded NPCs that register a custom
    ///    threshold through the same data set.
    ///
    /// WHY BANNERS ARE HANDED OUT ONE AT A TIME INSTEAD OF ALL AT ONCE
    /// ------------------------------------------------------------------
    /// Player.QuickSpawnItem does not put an item directly into the inventory - it spawns a
    /// real item entity in the world at the player's position, exactly like a killed NPC drops
    /// its loot. The player then picks it up through the same in-world pickup path a genuine
    /// kill reward uses, so a single banner granted this way is already indistinguishable from
    /// one earned by playing normally.
    ///
    /// The one aspect that IS different from normal play is timing: during normal play, a
    /// player never earns more than one banner in the same instant, because kills are spread
    /// out over real time. A recovery command that owes the player dozens or hundreds of
    /// banners at once would, if it granted them all in a single call, spawn all of them on
    /// the exact same game tick - something that could never happen through ordinary play, and
    /// that any code reacting to "a banner was just picked up" (in this mod or any other) has
    /// no reason to expect or be tested against.
    ///
    /// To stay truly identical to natural play, this command does not grant anything directly.
    /// Instead it enqueues one entry per banner chunk into a shared queue, and
    /// BannerRecoveryDripFeeder (a ModSystem below) releases exactly one entry per game tick.
    /// From the game's perspective, this looks exactly like the player killing one banner-
    /// worthy enemy every 1/60th of a second - each pickup happens on its own tick, one at a
    /// time, just spaced far closer together than real kills would be.
    ///
    /// STATELESS BY DESIGN (per recovery run)
    /// -----------------------------------------
    /// This command keeps no memory of previous runs. It always computes the FULL amount of
    /// every banner currently earned according to NPC.killCount, every single time it is
    /// executed. This is a deliberate choice: the whole point of the command is to recover
    /// from an external bug that wipes the player's banners, possibly more than once. If the
    /// command instead tracked "how much was already granted" and only topped up the
    /// difference, that tracking data would survive the very bug this command exists to fix -
    /// so after a second wipe it would see "nothing new earned since last time" and silently
    /// grant nothing, even though the player again owns zero banners. A stateless,
    /// always-grant-everything command has no such failure mode. The accepted trade-off is
    /// that running it twice in a row without an actual wipe in between will duplicate
    /// banners - a minor, easily self-avoided cost for a manual recovery tool, versus a
    /// recovery tool that can silently stop working.
    /// </summary>
    public class RecoverBannersCommand : ModCommand
    {
        // Chat commands execute on the client that typed them, which is exactly what we want
        // here: the command reads the local player's own kill-count data and queues drops for
        // that same player.
        //
        // MULTIPLAYER NOTE: NPC.killCount is a WORLD statistic shared by every player on the
        // server, not a personal one - this mirrors vanilla, where all players on a server
        // contribute to (and benefit from) the same banner kill counters. On a shared server
        // this command computes "banners earned by the world", not "banners earned by this
        // character alone".
        public override CommandType Type => CommandType.Chat;

        public override string Command => "recoverbanners";

        public override string Usage => "/recoverbanners";

        public override string Description =>
            "Grants every enemy banner you are currently owed based on this world's recorded kill counts, one at a time, exactly as if freshly earned.";

        public override void Action(CommandCaller caller, string input, string[] args)
        {
            Player player = caller.Player;

            int bannerTypesOwed = 0;
            long totalItemsOwed = 0;

            // NPC.killCount is indexed by BannerID. Index 0 (NPCID.None) is never meaningful,
            // so the loop starts at 1.
            for (int bannerId = 1; bannerId < NPC.killCount.Length; bannerId++)
            {
                int worldKillsForThisBanner = NPC.killCount[bannerId];
                if (worldKillsForThisBanner <= 0)
                    continue;

                // Resolve the BannerID to the actual placeable banner item. A BannerID with no
                // matching item (0) means there is nothing to grant for it.
                int bannerItemType = Item.BannerToItem(bannerId);
                if (bannerItemType <= 0)
                    continue;

                // Ask vanilla's own data table how many kills this specific banner needs.
                // Falling back to the vanilla default of 50 only guards against the extremely
                // unlikely case of a missing table entry (e.g. a division by zero).
                int killsRequiredPerBanner = ItemID.Sets.KillsToBanner[bannerItemType];
                if (killsRequiredPerBanner <= 0)
                    killsRequiredPerBanner = 50;

                int totalBannersEarned = worldKillsForThisBanner / killsRequiredPerBanner;
                if (totalBannersEarned <= 0)
                    continue;

                EnqueueBannerStack(player, bannerItemType, totalBannersEarned);

                bannerTypesOwed++;
                totalItemsOwed += totalBannersEarned;
            }

            if (bannerTypesOwed == 0)
            {
                caller.Reply("No banners are owed - this world's recorded kill counts don't reach any banner threshold yet.", Color.Yellow);
                return;
            }

            caller.Reply($"Queued {totalItemsOwed} banner(s) across {bannerTypesOwed} banner type(s). They'll drop in one at a time over the next few seconds, exactly as if freshly earned.", Color.LightGreen);
        }

        /// <summary>
        /// Splits the requested total quantity into chunks that never exceed the item's own
        /// maxStack, and queues one drop per chunk.
        ///
        /// WHY THIS EXISTS: earned banner counts can legitimately exceed a single stack's
        /// maxStack (999 for ordinary banners) after enough kills. A genuine kill never drops
        /// more than one banner at a time, so each queued chunk here is capped the same way a
        /// single real drop would be, and is released as its own separate pickup event by
        /// BannerRecoveryDripFeeder.
        /// </summary>
        private static void EnqueueBannerStack(Player player, int itemType, int totalAmount)
        {
            int maxStack = ContentSamples.ItemsByType[itemType].maxStack;
            if (maxStack <= 0)
                maxStack = 999;

            int remaining = totalAmount;
            while (remaining > 0)
            {
                int chunk = remaining < maxStack ? remaining : maxStack;
                remaining -= chunk;

                BannerRecoveryDripFeeder.PendingDrops.Enqueue(new PendingBannerDrop(player.whoAmI, itemType, chunk));
            }
        }
    }

    /// <summary>
    /// One banner stack still waiting to be dropped for a specific player.
    /// </summary>
    internal readonly struct PendingBannerDrop
    {
        public readonly int PlayerWhoAmI;
        public readonly int ItemType;
        public readonly int Stack;

        public PendingBannerDrop(int playerWhoAmI, int itemType, int stack)
        {
            PlayerWhoAmI = playerWhoAmI;
            ItemType = itemType;
            Stack = stack;
        }
    }

    /// <summary>
    /// Releases exactly one queued banner drop per game tick.
    ///
    /// This is what makes /recoverbanners indistinguishable from natural play from the game's
    /// point of view: instead of a single call spawning a hundred item entities on the same
    /// tick (something that could never happen through ordinary kills), one entry leaves the
    /// queue and becomes a real, independent world-item pickup every tick - the same rate-
    /// limited, one-at-a-time shape any genuine sequence of kills would produce, just
    /// compressed into a few seconds instead of hours of play.
    /// </summary>
    public class BannerRecoveryDripFeeder : ModSystem
    {
        internal static readonly Queue<PendingBannerDrop> PendingDrops = new Queue<PendingBannerDrop>();

        public override void PostUpdateEverything()
        {
            if (PendingDrops.Count == 0)
                return;

            PendingBannerDrop drop = PendingDrops.Dequeue();

            Player player = Main.player[drop.PlayerWhoAmI];
            if (player == null || !player.active)
                return; // The player disconnected or is no longer valid; simply drop this entry.

            IEntitySource source = player.GetSource_GiftOrReward("RecoverBanners");
            player.QuickSpawnItem(source, drop.ItemType, drop.Stack);
        }
    }
}
