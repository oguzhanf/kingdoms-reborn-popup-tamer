using System.ComponentModel;

namespace KRPopupTamer;

enum PatchState { Off, On, Partial, Unknown }

// Applies and removes options in the running game and reads the suppression counters.
// Skip and redirect patches lead into a small "cave" page that this class allocates next to the game module:
//   +0x00 "KRPT" magic, +0x04 layout id, +0x08 local player id, +0x10 uint32 counters[stub index],
//   +0x40 int32 factor percent[wrapper] (100..1000),
//   +0x100 + index * 0x40: stub = mov eax,[reg+off]; cmp eax,[pid]; jne +7; lock inc [counter]; jmp resume
//   +0x800 + wrapper * 0x80: wrapper code (see WrapperCode)
// The layout id is a hash of the patch lists and code encoding. A page with another layout id (made by another
// build of this app) is never modified: its sites are re-pointed to a fresh page, or restored.
sealed class Patcher(GameProcess game, IReadOnlyList<GameOption> options)
{
    const uint Magic = 0x5450524B; // "KRPT"
    const int CaveSize = 0x1000, PlayerIdField = 0x08, CountersField = 0x10, FactorsField = 0x40, StubsField = 0x100, StubStride = 0x40;
    const int WrappersField = 0x800, WrapperStride = 0x80;
    const int BuildingPlayerId = 0x94, BuildingIsConstructed = 0x121; // Building::_playerId, Building::_isConstructed
    const int StubEncoding = 2; // bump when StubCode, WrapperCode or the page layout changes
    readonly List<SkipPatch> _skipPatches = options.SelectMany(o => o.Patches).OfType<SkipPatch>().ToList();
    readonly List<RedirectPatch> _redirects = options.SelectMany(o => o.Patches).OfType<RedirectPatch>().ToList();
    readonly int[] _factors = [100, 100]; // percent per Wrapper; written into a new page before any site can reach it
    int _localPlayer;
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
            foreach (var p in _redirects)
                foreach (var v in new[] { p.Site.Rva, p.TargetRva, (uint)p.Wrapper })
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
        _localPlayer = playerId;
        var cave = FindCave();
        if (cave != 0 && game.I32(cave + PlayerIdField) != playerId)
            game.WriteData(cave + PlayerIdField, BitConverter.GetBytes(playerId));
    }

    // Sets the factor of an option with redirect patches, in percent (clamped to 100..1000). The wrappers read it on
    // every call, so a change takes effect without touching game code.
    public void SetFactor(GameOption option, int percent)
    {
        percent = Math.Clamp(percent, 100, 1000);
        foreach (var wrapper in option.Patches.OfType<RedirectPatch>().Select(p => p.Wrapper).Distinct())
        {
            _factors[(int)wrapper] = percent;
            var cave = FindCave();
            var cell = cave + FactorsField + (ulong)(4 * (int)wrapper);
            if (cave != 0 && game.I32(cell) != percent) game.WriteData(cell, BitConverter.GetBytes(percent));
        }
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
            RedirectPatch => current[0] == site.OriginalBytes[0] && LayoutOf(BranchTarget(address, current) & ~0xFFFUL) != null
                ? PatchState.On : PatchState.Unknown,
            _ => PatchState.Unknown,
        };
    }

    byte[] DesiredOnBytes(Patch patch) => patch switch
    {
        BytesPatch b => b.NewBytes,
        SkipPatch s => Jump(game.Base + s.Site.Rva, EnsureStub(s), s.Site.OriginalBytes.Length),
        RedirectPatch r => [r.Site.OriginalBytes[0], .. BitConverter.GetBytes(Rel32(game.Base + r.Site.Rva + 5, EnsureWrapper(r)))],
        _ => throw new NotSupportedException(),
    };

    ulong EnsureCave()
    {
        var cave = FindCave();
        if (cave != 0) return cave;
        cave = game.AllocateNearModule(CaveSize); // fresh pages are zeroed: counters 0
        // Player id and factors first (a zero factor would stop production); the magic last makes the page findable.
        game.WriteData(cave + PlayerIdField, BitConverter.GetBytes(_localPlayer));
        game.WriteData(cave + FactorsField, [.. _factors.SelectMany(BitConverter.GetBytes)]);
        game.WriteData(cave, [.. BitConverter.GetBytes(Magic), .. BitConverter.GetBytes(LayoutId)]);
        return _cave = cave;
    }

    ulong EnsureStub(SkipPatch patch)
    {
        var cave = EnsureCave();
        var index = _skipPatches.IndexOf(patch);
        var stub = cave + StubsField + (ulong)(index * StubStride);
        WriteOnce(stub, StubCode(patch, game.Base, cave, index, stub));
        return stub;
    }

    ulong EnsureWrapper(RedirectPatch patch)
    {
        var cave = EnsureCave();
        var at = cave + WrappersField + (ulong)((int)patch.Wrapper * WrapperStride);
        WriteOnce(at, WrapperCode(patch.Wrapper, game.Base + patch.TargetRva, cave, at));
        return at;
    }

    // Only an unused (zero) slot may be written: no site can lead to it yet, so no thread suspension is needed.
    void WriteOnce(ulong at, byte[] code)
    {
        var existing = game.Read(at, code.Length);
        if (existing.SequenceEqual(code)) return;
        if (existing.Any(b => b != 0)) throw new InvalidOperationException("Counting page is inconsistent; nothing was changed.");
        game.WriteData(at, code);
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

    // Output: called instead of Building::efficiency(building); returns efficiency * factor / 100 when the building
    //   belongs to the local player. Clobbers only registers a call may clobber. Its frame (push rbx) has no unwind data.
    // Speed: called or tail-jumped to instead of Building::DoWork(building, unitId, work, bool). For the local player's
    //   constructed buildings it scales work (r8d) by factor / 100, then jumps to DoWork with the other arguments intact.
    internal static byte[] WrapperCode(Wrapper wrapper, ulong target, ulong cave, ulong at)
    {
        var code = new List<byte>();
        void Bytes(params byte[] b) => code.AddRange(b);
        void Rel32To(ulong address) => code.AddRange(BitConverter.GetBytes(Rel32(at + (ulong)code.Count + 4, address)));
        var factor = cave + FactorsField + (ulong)(4 * (int)wrapper);
        if (wrapper == Wrapper.Output)
        {
            Bytes(0x53);                                                              // push rbx
            Bytes(0x48, 0x83, 0xEC, 0x20);                                            // sub rsp, 0x20
            Bytes(0x48, 0x89, 0xCB);                                                  // mov rbx, rcx
            Bytes(0xE8); Rel32To(target);                                             // call Building::efficiency
            Bytes(0x8B, 0x8B); Bytes(BitConverter.GetBytes(BuildingPlayerId));        // mov ecx, [rbx + _playerId]
            Bytes(0x3B, 0x0D); Rel32To(cave + PlayerIdField);                         // cmp ecx, [rip + playerId]
            Bytes(0x75, 0x0F);                                                        // jne done
            Bytes(0x0F, 0xAF, 0x05); Rel32To(factor);                                 // imul eax, [rip + factor]
            Bytes(0x99);                                                              // cdq
            Bytes(0xB9, 100, 0, 0, 0);                                                // mov ecx, 100
            Bytes(0xF7, 0xF9);                                                        // idiv ecx
            Bytes(0x48, 0x83, 0xC4, 0x20);                                            // done: add rsp, 0x20
            Bytes(0x5B);                                                              // pop rbx
            Bytes(0xC3);                                                              // ret
        }
        else
        {
            Bytes(0x80, 0xB9); Bytes(BitConverter.GetBytes(BuildingIsConstructed)); Bytes(0x00); // cmp byte [rcx + _isConstructed], 0
            Bytes(0x74, 0x2B);                                                        // je go
            Bytes(0x8B, 0x81); Bytes(BitConverter.GetBytes(BuildingPlayerId));        // mov eax, [rcx + _playerId]
            Bytes(0x3B, 0x05); Rel32To(cave + PlayerIdField);                         // cmp eax, [rip + playerId]
            Bytes(0x75, 0x1D);                                                        // jne go
            Bytes(0x49, 0x89, 0xD2);                                                  // mov r10, rdx
            Bytes(0x44, 0x89, 0xC0);                                                  // mov eax, r8d
            Bytes(0x0F, 0xAF, 0x05); Rel32To(factor);                                 // imul eax, [rip + factor]
            Bytes(0x99);                                                              // cdq
            Bytes(0x41, 0xBB, 100, 0, 0, 0);                                          // mov r11d, 100
            Bytes(0x41, 0xF7, 0xFB);                                                  // idiv r11d
            Bytes(0x41, 0x89, 0xC0);                                                  // mov r8d, eax
            Bytes(0x4C, 0x89, 0xD2);                                                  // mov rdx, r10
            Bytes(0xE9); Rel32To(target);                                             // go: jmp Building::DoWork
        }
        if (code.Count > WrapperStride) throw new InvalidOperationException("Wrapper too long");
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
        site[0] == 0xE9 && site[5..].All(b => b == 0x90) ? BranchTarget(address, site) : null;

    // Target of the 5-byte call or jmp rel32 at address.
    static ulong BranchTarget(ulong address, byte[] site) => (ulong)((long)address + 5 + BitConverter.ToInt32(site, 1));

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

    // The counting page of this game session with this build's layout: found through a patched site, or by probing
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
        foreach (var patch in _redirects)
        {
            var address = game.Base + patch.Site.Rva;
            var page = BranchTarget(address, game.Read(address, 5)) & ~0xFFFUL;
            if (page != ((game.Base + patch.TargetRva) & ~0xFFFUL) && LayoutOf(page) == layout) return _cave = page;
        }
        foreach (var candidate in game.NearModuleCandidates())
            if (game.IsCommitted(candidate) && LayoutOf(candidate) == layout)
                return _cave = candidate;
        return 0;
    }
}
