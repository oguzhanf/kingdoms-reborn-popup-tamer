using System.ComponentModel;

namespace KRPopupTamer;

sealed record GameInfo(int PlayerId, bool IsSinglePlayer, int Year, byte HandType, int HandCards, int QueuedHands, int QueuedYearlyActions)
{
    // Reads the local player's state, or null when not in a game (main menu, loading screen, torn read).
    // Pointer chain (UE 4.25 + game classes, offsets from the PDB): GWorld -> UWorld+0x188 GameInstance ->
    // +0x38 LocalPlayers[0] -> +0x30 PlayerController (must be APunPlayerController) -> +0x5d8 AGameManager ->
    // +0x298 GameSimulationCore -> +0x6b8 vector<BuildingCardSystem>[playerId (AGameManager+0x488)], stride 0x1f0.
    public static GameInfo? Read(GameProcess g)
    {
        try
        {
            var world = g.Ptr(g.Base + 0x16852af0);
            var gameInstance = g.Ptr(world + 0x188);
            if (g.I32(gameInstance + 0x40) < 1) return null; // LocalPlayers.Num
            var pc = g.Ptr(g.Ptr(g.Ptr(gameInstance + 0x38)) + 0x30);
            if (g.U64(pc) - g.Base != 0x4f0f998) return null; // APunPlayerController::`vftable'
            var gameManager = g.Ptr(pc + 0x5d8);
            var sim = g.Ptr(gameManager + 0x298);
            var playerId = g.I32(gameManager + 0x488);
            var (systems, count) = g.Vector(sim + 0x6b8, 0x1f0, 256);
            if (playerId < 0 || playerId >= count) return null;
            var cs = systems + (ulong)playerId * 0x1f0;
            var (_, cards) = g.Vector(cs + 0x158, 2, 64);            // _cardsRareHand
            var (queueFirst, queued) = g.Vector(cs + 0x198, 0x28, 100_000); // _rareHandsDataQueued
            var yearly = 0;
            if (queued > 0)
            {
                var raw = g.Read(queueFirst, queued * 0x28);
                for (var i = 0; i < queued; i++) if (raw[i * 0x28] is 35 or 36) yearly++;
            }
            var year = g.I32(g.Base + 0x61771d0); // Time::_Years
            var singlePlayer = g.Read(sim + 0x768 + 0x2f, 1)[0] != 0; // GameSimulationCore::_mapSettings.isSinglePlayer
            return new GameInfo(playerId, singlePlayer, year, g.Read(cs + 0x170, 1)[0], cards, queued, yearly);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public string HandDescription => HandCards == 0 ? "none" : HandType switch
    {
        0 or 1 => "Starting cards",
        2 => "Rare cards",
        3 => "Prize",
        4 => "Crates",
        >= 5 and <= 8 => $"Townhall level {HandType - 3}",
        >= 9 and <= 15 => $"Population quest {HandType - 8}",
        >= 16 and <= 25 => "Biome bonus",
        >= 26 and <= 28 => "Apocalypse",
        >= 29 and <= 34 => "Era bonus",
        35 or 36 => "Yearly Action",
        _ => $"type {HandType}",
    };
}
