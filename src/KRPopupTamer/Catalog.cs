namespace KRPopupTamer;

// Every option is a set of verified patches for one game build. RVAs, bytes and register facts come from the
// game's own PDB (see docs/how-it-works.md and tools/). Before writing, each site's surrounding bytes are checked.
static class Catalog
{
    public const uint BuildTimeDateStamp = 0x698c804c, BuildSizeOfImage = 0x16cc6000;
    public const string SteamAppId = "1307890";

    public static readonly GameOption[] Options =
    [
        new("yearly-action", "Yearly Action (Buy Wood / Steal, Kidnap / Immigrants)", OptionKind.Popup, DefaultOn: true,
            "The 'Yearly Action - Choose an Action' card window. The game queues two of these every year and they " +
            "block every later card choice until answered. Suppressed hands are made empty, exactly like the game " +
            "already does for Townhall level 2-5 hands; prize, era and bonus card choices still appear. You lose the " +
            "free pick from these windows (yearly and after townhall level 2/3 upgrades); the cards can still show " +
            "up in your normal card hand.",
        [
            // BuildingCardSystem::RollRareHandExecute, case Actions_StealBuyWood: `mov ecx,4` -> exit 0x2640443.
            new SkipPatch(new CodeSite(0x26400f4, "B9 04 00 00 00", "F9 23 0F 85 8A 00 00 00", "C7 44 24 50 65 02 35 02"),
                ResumeRva: 0x2640443, Reg.R14, PlayerIdOffset: 0x68, Legacy: "E9 4A 03 00 00"),
            // Same function, case Actions_KidnapImmigrants.
            new SkipPatch(new CodeSite(0x2640187, "B9 04 00 00 00", "F9 24 0F 85 8A 00 00 00", "C7 44 24 50 67 02 3F 02"),
                ResumeRva: 0x2640443, Reg.R14, PlayerIdOffset: 0x68, Legacy: "E9 B7 02 00 00"),
        ]),
        new("year-summary", "Year Summary", OptionKind.Popup, DefaultOn: true,
            "The 'Year N Summary' statistics window (population growth, revenue, science). It only shows " +
            "statistics, so skipping it changes nothing in your game; the popup is simply not added to the queue.",
        [
            // PlayerOwnedManager::TickRound: skip copying the YearlySummary PopupInfo and GameSimulationCore::AddPopup.
            new SkipPatch(new CodeSite(0x2712ee7, "48 8B 86 88 01 00 00", "00 48 63 C3 44 89 24 81", "48 8D 95 40 04 00 00 48"),
                ResumeRva: 0x2712f14, Reg.Rsi, PlayerIdOffset: 0x190),
        ]),
        new("wandering-traders", "Wandering Traders", OptionKind.Popup, DefaultOn: true,
            "'Wandering Traders have arrived. They wish to buy any goods you might have.' (Trade / Refuse). " +
            "Refuse does nothing in the game; Trade only opens the trade window. Suppressing it removes that " +
            "yearly shortcut to the trade window (it only appears while you have no Trading Post / Port).",
        [
            // PlayerOwnedManager::TickRound: skip building the CaravanBuyer popup, the same path the game takes on apocalypse maps.
            new SkipPatch(new CodeSite(0x2712483, "4C 8D 0D 4E 6C 7E 02", "84 C0 0F 85 56 01 00 00", "4C 8D 05 D7 63 7E 02 48"),
                ResumeRva: 0x27125d9, Reg.Rsi, PlayerIdOffset: 0x190),
        ]),
        new("slave-traders", "Slave Traders", OptionKind.Popup, DefaultOn: true,
            "'Noticing you need workers, slave traders have arrived...' (Buy 5 slaves with 300 / Refuse). " +
            "Refuse does nothing in the game. Suppressing it means you cannot buy those 5 workers.",
        [
            // PlayerOwnedManager::TickRound: skip building the SlaveTrader popup after its eligibility test.
            new SkipPatch(new CodeSite(0x27126f2, "4C 8D 0D AF 6A 7E 02", "3B D8 0F 8D 57 01 00 00", "4C 8D 05 68 61 7E 02 48"),
                ResumeRva: 0x2712849, Reg.Rsi, PlayerIdOffset: 0x190),
        ]),
        new("yearly-immigrants", "Immigrants asking to join (yearly)", OptionKind.Popup, DefaultOn: false,
            "'3 Immigrants wishes to join your City.' (Accept / Refuse). Suppressing it has the same result as " +
            "letting it expire unanswered: no immigrants and no 'A gift from the immigrants' prize choice. " +
            "Off by default because Accept is usually worth it.",
        [
            // PlayerOwnedManager::TickRound: skip the yearly GameSimulationCore::ImmigrationEvent(receiver 0x1b) call.
            new SkipPatch(new CodeSite(0x27128ac, "48 8B 86 88 01 00 00", "10 0A 00 00 85 C0 7E 49", "4C 8D 0D 56 66 7E 02 4C"),
                ResumeRva: 0x27128f5, Reg.Rsi, PlayerIdOffset: 0x190),
        ]),
        new("irrigation-capacity", "Irrigation pump capacity x2", OptionKind.Gameplay, DefaultOn: false,
            "Doubles each Irrigation Pump's water: it fills 2 x efficiency + 16 canal tiles instead of efficiency + 16 " +
            "(about twice as many; the 'Water usage' line shows 2 x efficiency). Takes effect when a town's irrigation is recalculated: " +
            "place or remove one canal tile in each town after turning this on or off. Applies to all towns, AI included.",
        [
            // OverlaySystem::RefreshIrrigationFill: lea r13d,[rax+0x10]; cmp rdi,rsi -> lea r13d,[rax+rax+0x10]; cmp edi,esi
            new BytesPatch(new CodeSite(0x286380c, "44 8D 68 10 48 3B FE", "42 D3 FF 48 8B 74 24 50", "74 0C 44 89 2F 48 83 C7"),
                "44 8D 6C 00 10 3B FE"),
            // IrrigationPump::displayWaterUsage: add eax,0x10 ... sub eax,0x10 -> add eax,eax ... (2*eff - waterLeft)
            new BytesPatch(new CodeSite(0x27b2a1e, "83 C0 10 2B 83 18 03 00 00 83 E8 10", "48 8B D9 E8 E2 50 DE FF", "48 83 C4 20 5B C3 6B 51"),
                "01 C0 90 2B 83 18 03 00 00 0F 1F 00"),
            // UObjectDescriptionUISystem::UpdateDescriptionUI, pump branch: displayed capacity ebx = 2*efficiency.
            new BytesPatch(new CodeSite(0x279c939, "48 8B 4C 24 50 BA 05 00 00 00 8B D8", "49 8B CC E8 C7 B1 DF FF", "E8 16 76 FE FF 0F 57 C0"),
                "8D 1C 00 48 8B 4C 24 50 31 D2 B2 05"),
        ]),
        new("irrigation-reach", "Irrigation reach x2", OptionKind.Gameplay, DefaultOn: false,
            "Canals irrigate twice as far: full 95% fertility out to radius 10 (was 5), fading to 0 at radius 20 " +
            "(was 10). Shows in the fertility overlay and placement preview after a town's irrigation is recalculated: " +
            "place or remove one canal tile in each town after turning this on or off. Farms keep the fertility they " +
            "had when built (the game caches it), so rebuild farms to benefit; farms built while this is on keep the boost.",
        [
            // lambda in OverlaySystem::RefreshHumanFertility: fertility = min(95, (10 - dist) * 100 / 5)
            // -> min(95, (20 - dist) * 50 / 5). Both constants change together.
            new BytesPatch(new CodeSite(0x267bc28, "BA 0A 00 00 00", "D2 41 03 D1 66 0F 6E C2", "F3 0F E6 C0 66 0F 51 C8"),
                "BA 14 00 00 00"),
            new BytesPatch(new CodeSite(0x267bc40, "44 6B C2 64", "C1 2B D0 B8 67 66 66 66", "41 F7 E8 D1 FA 8B C2 C1"),
                "44 6B C2 32"),
        ],
        // A thread between the two constants would compute one tile with a mixed formula.
        GuardRanges: [(0x267bc2d, 0x267bc40)]),
        new("auto-trade-8x", "Auto-Trade 8 times a year (every round)", OptionKind.Gameplay, DefaultOn: false,
            "Town Auto-Trade (the orders set with the Trading Company's Auto-Trade button) runs every round, 8 times a " +
            "game year, instead of once at the end of the year. Each trade can move your full trade capacity, so up to " +
            "8x the yearly volume; exports never exceed your stock, and frequent selling lowers world prices. " +
            "'Trade Quantity per Year' and 'Net Profit per Year' in the game now mean per trade. Applies to AI towns too.",
        [
            // TownManager::TickRound: the auto-trade block is gated by Ticks % 72000 == 0 (`jne` past it). TickRound only
            // runs at Ticks % 9000 == 0, so removing the gate trades every round.
            new BytesPatch(new CodeSite(0x271f85b, "0F 85 A7 15 00 00", "CA 40 19 01 00 44 3B C1", "40 88 74 24 48 48 8D 45"),
                "66 0F 1F 44 00 00"),
        ]),
        new("output-multiplier", "Building output multiplier (your towns)", OptionKind.Gameplay, DefaultOn: false,
            "Your buildings make more goods each time they produce, by the factor set below the list (x1.0 to x10.0): " +
            "workshops, mines, fishers and other batch buildings, farms, fruit gatherers, foresters, hunters and ranch " +
            "slaughter; also what the Mint, Card Maker, Inventor's Workshop, Immigration Office and Barrack make per batch. " +
            "Workshops use the same inputs per batch, so x2 means twice the goods from the same materials. " +
            "Only your own towns and colonies; AI towns are not changed. A building's Efficiency line stays the same, its " +
            "output per batch shows the boost. Not covered: the cow ranch's per-round product, and citizens harvesting " +
            "trees or stone without a workplace.",
        [
            // Building::efficiency calls that compute produced amounts -> output wrapper (efficiency x factor for the
            // local player's buildings). Other callers (trade, irrigation, combat, caravans, UI) keep the plain value.
            new RedirectPatch(new CodeSite(0x259851b, "E8 E0 F5 FF FF", "01 00 00 48 8B CB 8B F8", "48 8B 5C 24 30 0F AF F8"), Wrapper.Output), // Building::outputPerBatch
            new RedirectPatch(new CodeSite(0x267890d, "E8 EE F1 F1 FF", "57 48 83 EC 20 48 8B D9", "48 8B CB 8B F8 E8 E4 F1"), Wrapper.Output), // ConsumerIndustrialBuilding::outputPerBatch (Mint, CardMaker, ...)
            new RedirectPatch(new CodeSite(0x2644056, "E8 A5 3A F5 FF", "03 D8 41 2B D8 83 C3 64", "8B 7D 6B 44 8B C0 B9 0A"), Wrapper.Output), // Farm::DoFarmWork
            new RedirectPatch(new CodeSite(0x272b1f8, "E8 03 C9 E6 FF", "EB 03 48 8B C6 48 8B C8", "0F AF 44 24 34 8B C8 E8"), Wrapper.Output), // UnitStateAI::GatherFruit
            new RedirectPatch(new CodeSite(0x272df1a, "E8 E1 9B E6 FF", "00 EB 02 33 C0 48 8B C8", "83 C0 9C 33 DB 85 C0 0F"), Wrapper.Output), // UnitStateAI::HarvestTileObj (Forester)
            new RedirectPatch(new CodeSite(0x2726371, "E8 8A 17 E7 FF", "48 85 C0 74 0C 48 8B C8", "8B C8 EB 02 8B CE 43 0F"), Wrapper.Output), // UnitStateAI::AttackIncoming (hunter drops)
        ],
        FactorLabel: "Output x"),
        new("speed-multiplier", "Building work speed multiplier (your towns)", OptionKind.Gameplay, DefaultOn: false,
            "Work on your finished buildings counts more, by the factor set below the list (x1.0 to x10.0), so batches " +
            "finish that much sooner: workshops, mines, fishers, card makers, immigration offices. Workshops also use " +
            "their inputs that much faster. Farms, gatherers and construction are not affected. Work beyond one batch per " +
            "work action is lost, so very high factors stop helping. Only your own towns and colonies; AI towns are not changed.",
        [
            // Building::DoWork calls -> speed wrapper (work amount x factor for the local player's constructed buildings).
            new RedirectPatch(new CodeSite(0x2728cfe, "E9 5D 69 F0 FF", "48 81 C4 80 00 00 00 5B", "CC CC CC CC CC CC CC CC"), Wrapper.Speed), // UnitStateAI::DoWork (tail jump)
            new RedirectPatch(new CodeSite(0x272ac8f, "E8 CC 49 F0 FF", "B8 44 0F AF C7 49 8B CE", "66 41 83 BE 98 00 00 00"), Wrapper.Speed), // UnitStateAI::FisherDoWork
            new RedirectPatch(new CodeSite(0x273238b, "E8 D0 D2 EF FF", "73 D8 08 66 41 0F 7E C0", "80 BD 18 01 00 00 00 0F"), Wrapper.Speed), // UnitStateAI::Produce
        ],
        FactorLabel: "Work speed x"),
    ];
}

enum OptionKind { Popup, Gameplay }

// Code in the app's page that a RedirectPatch sends a call (or tail jump) to. Both read their factor from the page.
enum Wrapper { Output, Speed }

enum Reg { Rax = 0, Rcx = 1, Rdx = 2, Rbx = 3, Rbp = 5, Rsi = 6, Rdi = 7, R8 = 8, R9, R10, R11, R13 = 13, R14, R15 }

// Bytes at Rva (Original) plus 8 bytes before and after, which must match before anything is written.
sealed record CodeSite(uint Rva, string Original, string Before, string After)
{
    public byte[] OriginalBytes { get; } = Hex(Original);
    public byte[] BeforeBytes { get; } = Hex(Before);
    public byte[] AfterBytes { get; } = Hex(After);
    public static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));
}

abstract record Patch(CodeSite Site);

// Skips the instruction(s) at Site: jumps to a stub that counts the event for the local player and resumes at
// ResumeRva. The stub clobbers EAX and flags, which must be dead at ResumeRva. Legacy = older KRTrainer bytes.
sealed record SkipPatch(CodeSite Site, uint ResumeRva, Reg PlayerIdBase, int PlayerIdOffset, string? Legacy = null) : Patch(Site)
{
    public byte[]? LegacyBytes { get; } = Legacy == null ? null : CodeSite.Hex(Legacy);
}

// Replaces the bytes at Site with New (same length).
sealed record BytesPatch(CodeSite Site, string New) : Patch(Site)
{
    public byte[] NewBytes { get; } = CodeSite.Hex(New);
}

// Changes the rel32 of a call or jmp at Site (E8/E9 + 4 bytes) to go through Wrapper, which ends in the original target.
sealed record RedirectPatch(CodeSite Site, Wrapper Wrapper) : Patch(Site)
{
    public uint TargetRva { get; } = (uint)(Site.Rva + 5 + BitConverter.ToInt32(Site.OriginalBytes, 1));
}

// Guards: extra code ranges (RVA, inclusive) in which no game thread may be stopped while the option is switched.
// FactorLabel: the option has a factor (x1.0 to x10.0) set in the window, and this labels its box.
sealed record GameOption(string Id, string Name, OptionKind Kind, bool DefaultOn, string Description, Patch[] Patches,
    (uint Start, uint End)[]? GuardRanges = null, string? FactorLabel = null)
{
    public (uint Start, uint End)[] Guards => GuardRanges ?? [];
}
