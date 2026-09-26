using System.ComponentModel;

namespace KRPopupTamer;

enum PatchState { Off, On, Partial, Unknown }

// Applies and removes options in the running game and reads the suppression counters.
// Skip patches jump into a small "cave" page that this class allocates next to the game module:
//   +0x00 "KRPT" magic, +0x04 layout id, +0x08 local player id, +0x10 uint32 counters[stub index],
//   +0x100 + index * 0x40: stub = mov eax,[reg+off]; cmp eax,[pid]; jne +7; lock inc [counter]; jmp resume
// The layout id is a hash of the skip-patch list and stub encoding. A page with another layout id (made by another
// build of this app) is never modified: its sites are re-pointed to a fresh page, or restored.
sealed class Patcher(GameProcess game, IReadOnlyList<GameOption> options)
{
    const uint Magic = 0x5450524B; // "KRPT"
    const int CaveSize = 0x1000, PlayerIdField = 0x08, CountersField = 0x10, StubsField = 0x100, StubStride = 0x40;
    const int StubEncoding = 1; // bump when StubCode changes
    readonly List<SkipPatch> _skipPatches = options.SelectMany(o => o.Patches).OfType<SkipPatch>().ToList();
    ulong _cave;
    bool _caveSearched;

    uint LayoutId
    {
        get
        {
            var hash = 2166136261u ^ StubEncoding; // FNV-1a over the stub list
            foreach (var p in _skipPatches)
                foreach (var v in new[] { p.Site.Rva, p.ResumeRva, (uint)p.PlayerIdBase, (uint)p.PlayerIdOffset })
                    hash = (hash ^ v) * 16777619u;
            return hash;
        }
    }

    public PatchState StateOf(GameOption option)
    {
        var states = option.Patches.Select(StateOf).ToList();
        if (states.Contains(PatchState.Unknown)) return PatchState.Unknown;
        if (states.All(s => s == PatchState.On)) return PatchState.On;
        return states.All(s => s == PatchState.Off) ? PatchState.Off : PatchState.Partial;
    }

    // Returns false when a game thread was stopped inside code being changed; nothing was written, retry later.
    public bool Set(GameOption option, bool on)
    {
        var writes = new List<(ulong, byte[])>();
        foreach (var patch in option.Patches)
        {
            if (StateOf(patch) == PatchState.Unknown)
                throw new InvalidOperationException($"Unexpected game code at 0x{patch.Site.Rva:x}; nothing was changed.");
            var address = game.Base + patch.Site.Rva;
            var desired = on ? DesiredOnBytes(patch) : patch.Site.OriginalBytes;
            if (!game.Read(address, desired.Length).SequenceEqual(desired)) writes.Add((address, desired));
        }
        if (writes.Count == 0) return true;
        // No thread may be stopped strictly inside a changed instruction range, nor inside the option's own guards.
        var guards = option.Patches
            .Select(p => (game.Base + p.Site.Rva + 1, game.Base + p.Site.Rva + (ulong)p.Site.OriginalBytes.Length))
            .Concat(option.Guards.Select(g => (game.Base + g.Start, game.Base + g.End + 1)))
            .ToList();
        return game.WriteCode(writes, guards);
    }

    // Suppression count per option since the counting page was created in this game session.
    public long CountOf(GameOption option)
    {
        var cave = FindCave();
        if (cave == 0) return 0;
        long total = 0;
        foreach (var patch in option.Patches.OfType<SkipPatch>())
            total += BitConverter.ToUInt32(game.Read(cave + CountersField + (ulong)(4 * _skipPatches.IndexOf(patch)), 4));
        return total;
    }

    public void SetLocalPlayer(int playerId)
    {
        var cave = FindCave();
        if (cave != 0 && game.I32(cave + PlayerIdField) != playerId)
            game.WriteData(cave + PlayerIdField, BitConverter.GetBytes(playerId));
    }

    PatchState StateOf(Patch patch)
    {
        var site = patch.Site;
        var length = site.OriginalBytes.Length;
        var address = game.Base + site.Rva;
        var live = game.Read(address - 8, 8 + length + 8);
        if (!live[..8].SequenceEqual(site.BeforeBytes) || !live[(8 + length)..].SequenceEqual(site.AfterBytes))
            return PatchState.Unknown;
        var current = live[8..(8 + length)];
        if (current.SequenceEqual(site.OriginalBytes)) return PatchState.Off;
        return patch switch
        {
            BytesPatch b => current.SequenceEqual(b.NewBytes) ? PatchState.On : PatchState.Unknown,
            SkipPatch s when s.LegacyBytes != null && current.SequenceEqual(s.LegacyBytes) => PatchState.On,
            // A jump into any KRPT page is ours (possibly from another build); Set() re-points or restores it.
            SkipPatch => JumpTarget(address, current) is { } target && LayoutOf(target & ~0xFFFUL) != null ? PatchState.On : PatchState.Unknown,
            _ => PatchState.Unknown,
        };
    }

    byte[] DesiredOnBytes(Patch patch) => patch switch
    {
        BytesPatch b => b.NewBytes,
        SkipPatch s => Jump(game.Base + s.Site.Rva, EnsureStub(s), s.Site.OriginalBytes.Length),
        _ => throw new NotSupportedException(),
    };

    ulong EnsureStub(SkipPatch patch)
    {
        var cave = FindCave();
        if (cave == 0)
        {
            cave = game.AllocateNearModule(CaveSize); // fresh pages are zeroed: player 0, counters 0
            game.WriteData(cave, [.. BitConverter.GetBytes(Magic), .. BitConverter.GetBytes(LayoutId)]);
            _cave = cave;
        }
        var index = _skipPatches.IndexOf(patch);
        var stub = cave + StubsField + (ulong)(index * StubStride);
        var code = StubCode(patch, game.Base, cave, index, stub);
        var existing = game.Read(stub, code.Length);
        if (existing.SequenceEqual(code)) return stub;
        // Only an unused (zero) slot may be written: no site can jump to it yet, so no thread suspension is needed.
        if (existing.Any(b => b != 0)) throw new InvalidOperationException("Counting page is inconsistent; nothing was changed.");
        game.WriteData(stub, code);
        return stub;
    }

    internal static byte[] StubCode(SkipPatch patch, ulong moduleBase, ulong cave, int index, ulong stub)
    {
        var code = new List<byte>();
        void Rel32To(ulong target) => code.AddRange(BitConverter.GetBytes(Rel32(stub + (ulong)code.Count + 4, target)));
        var reg = (int)patch.PlayerIdBase;
        if ((reg & 7) == 4) throw new NotSupportedException("rsp/r12 base needs a SIB byte");
        if (reg >= 8) code.Add(0x41);
        code.AddRange([0x8B, (byte)(0x80 | (reg & 7))]);                  // mov eax, [reg + disp32]
        code.AddRange(BitConverter.GetBytes(patch.PlayerIdOffset));
        code.AddRange([0x3B, 0x05]); Rel32To(cave + PlayerIdField);      // cmp eax, [rip + playerId]
        code.AddRange([0x75, 0x07]);                                      // jne over the increment
        code.AddRange([0xF0, 0xFF, 0x05]); Rel32To(cave + CountersField + (ulong)(4 * index)); // lock inc dword [rip + counter]
        code.Add(0xE9); Rel32To(moduleBase + patch.ResumeRva);           // jmp resume
        return [.. code];
    }

    internal static byte[] Jump(ulong from, ulong to, int length)
    {
        var bytes = Enumerable.Repeat((byte)0x90, length).ToArray();
        bytes[0] = 0xE9;
        BitConverter.GetBytes(Rel32(from + 5, to)).CopyTo(bytes, 1);
        return bytes;
    }

    static int Rel32(ulong nextInstruction, ulong target)
    {
        var distance = (long)target - (long)nextInstruction;
        if (distance is < int.MinValue or > int.MaxValue) throw new InvalidOperationException("Jump target out of range");
        return (int)distance;
    }

    static ulong? JumpTarget(ulong address, byte[] site) =>
        site[0] == 0xE9 && site[5..].All(b => b == 0x90) ? (ulong)((long)address + 5 + BitConverter.ToInt32(site, 1)) : null;

    // Layout id of a KRPT page, or null if the page is not one of ours.
    uint? LayoutOf(ulong page)
    {
        try
        {
            var header = game.Read(page, 8);
            return BitConverter.ToUInt32(header, 0) == Magic ? BitConverter.ToUInt32(header, 4) : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    // The counting page of this game session with this build's layout: found through a patched jump, or by probing
    // the addresses AllocateNearModule uses (so an app restart with every option off reuses it). 0 if none yet.
    ulong FindCave()
    {
        if (_cave != 0 || _caveSearched) return _cave;
        _caveSearched = true;
        var layout = LayoutId;
        foreach (var patch in _skipPatches)
        {
            var address = game.Base + patch.Site.Rva;
            if (JumpTarget(address, game.Read(address, patch.Site.OriginalBytes.Length)) is { } target
                && LayoutOf(target & ~0xFFFUL) == layout)
                return _cave = target & ~0xFFFUL;
        }
        foreach (var candidate in game.NearModuleCandidates())
            if (game.IsCommitted(candidate) && LayoutOf(candidate) == layout)
                return _cave = candidate;
        return 0;
    }
}
