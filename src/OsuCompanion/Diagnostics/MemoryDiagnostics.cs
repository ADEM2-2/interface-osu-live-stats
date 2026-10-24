using System.Diagnostics;
using System.Globalization;
using OsuCompanion.Calculation;
using OsuCompanion.Memory;

namespace OsuCompanion.Diagnostics;

public sealed class MemoryDiagnostics
{
    private readonly SafeMemoryReader _reader;
    private readonly PointerConfig _config;

    public MemoryDiagnostics(SafeMemoryReader reader, PointerConfig config)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _config.Normalize();
    }

    public void RunWatch(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("================ LIVE WATCH (Ctrl+C to stop) ================");
        Console.WriteLine("Enter a beatmap and play. Resolution is retried every second.");

        var resolver = new SignatureResolver(_reader, _config);
        var announced = false;
        var lastReport = string.Empty;

        while (!token.IsCancellationRequested)
        {
            var result = resolver.Resolve(force: true);

            if (result.Success)
            {
                if (!announced)
                {
                    announced = true;
                    Console.WriteLine();
                    Console.WriteLine($"[ OK ] Player base 0x{result.PlayerBase.ToInt64():X} via {result.Method}");
                    foreach (var line in resolver.LastRunLog)
                    {
                        Console.WriteLine($"       {line}");
                    }

                    PrintFields(resolver, result.PlayerBase);
                    Console.WriteLine("--- live values ---");
                }

                var report = DescribeCompact(resolver, result.PlayerBase);
                if (report != lastReport)
                {
                    lastReport = report;
                    Console.WriteLine($"  {DateTime.Now:HH:mm:ss}  {report}");
                }
            }
            else if (!announced || lastReport != "FAIL")
            {
                announced = false;
                lastReport = "FAIL";
                Console.WriteLine();
                Console.WriteLine($"[WAIT] {result.Error}");
                foreach (var line in resolver.LastRunLog)
                {
                    Console.WriteLine($"       {line}");
                }
            }

            try
            {
                Task.Delay(1000, token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void PrintFields(SignatureResolver resolver, IntPtr playerBase)
    {
        Console.WriteLine("--- Fields (playerBase + offset) ---");
        foreach (var name in new[]
                 {
                     "score", "combo", "maxCombo", "hit300", "hit100", "hit50", "miss",
                     "playerName", "hitErrors", "mapId", "mods"
                 })
        {
            ReportField(resolver, playerBase, name);
        }
    }

    private string DescribeCompact(SignatureResolver resolver, IntPtr playerBase)
    {
        var score = ReadScalar(resolver, playerBase, "score");
        var combo = ReadScalar(resolver, playerBase, "combo");
        var maxCombo = ReadScalar(resolver, playerBase, "maxCombo");
        var hit300 = ReadScalar(resolver, playerBase, "hit300");
        var hit100 = ReadScalar(resolver, playerBase, "hit100");
        var hit50 = ReadScalar(resolver, playerBase, "hit50");
        var miss = ReadScalar(resolver, playerBase, "miss");
        var name = "-";

        if (_config.EffectiveFields.TryGetValue("playerName", out var field))
        {
            var address = resolver.ResolveFieldAddress(playerBase, field);
            if (address != IntPtr.Zero && _reader.ReadDotNetString(address, out var text))
            {
                name = text ?? "-";
            }
        }

        return $"name={name} score={score} combo={combo}/{maxCombo} hits={hit300}/{hit100}/{hit50}/{miss}";
    }

    private double ReadScalar(SignatureResolver resolver, IntPtr playerBase, string name)
    {
        if (!_config.EffectiveFields.TryGetValue(name, out var field))
        {
            return 0;
        }

        var address = resolver.ResolveFieldAddress(playerBase, field);
        return address != IntPtr.Zero && TryReadScalar(address, field.Type, out var value) ? value : 0;
    }

    public void ProbeOnly()
    {
        Console.WriteLine("================ SIGNATURE PROBE ================");
        ProbeSignatures();
        DumpModules();
        ProbeBeatmap();
        Console.WriteLine("==================================================");
    }

    /// <summary>
    /// Prints every candidate for the beatmap signature with the values each candidate
    /// produces, so the correct chain for this build can be identified without guessing.
    /// </summary>
    private void ProbeBeatmap()
    {
        if (!_config.EffectiveFields.TryGetValue("mapId", out var field) ||
            field.Steps is not { Count: > 0 })
        {
            Console.WriteLine("--- Beatmap probe: no mapId steps configured ---");
            return;
        }

        var patternStep = field.Steps.FirstOrDefault(s => s.Op == ChainStep.Pattern);
        if (patternStep?.Signature is null)
        {
            Console.WriteLine("--- Beatmap probe: no pattern in mapId steps ---");
            return;
        }

        var matches = _reader.FindPatternsInProcess(patternStep.Signature);
        Console.WriteLine($"--- Beatmap probe: {matches.Count} match(es) for \"{patternStep.Signature}\" ---");

        foreach (var match in matches.Take(8))
        {
            var anchor = match + (int)SignatureResolver.ParseValue(patternStep.Value);
            if (!_reader.ReadInt32(IntPtr.Add(anchor, -0xC), out var beatmap) || beatmap == 0)
            {
                Console.WriteLine($"  match 0x{match.ToInt64():X}: anchor 0x{anchor.ToInt64():X} -> unreadable beatmap");
                continue;
            }

            var candidate = ToPtr(beatmap);
            for (var depth = 0; depth <= 2 && candidate != IntPtr.Zero; depth++)
            {
                _reader.ReadInt32(IntPtr.Add(candidate, 0xC8), out var mapId);
                _reader.ReadInt32(IntPtr.Add(candidate, 0xCC), out var setId);

                var audio = ReadStringField(candidate, 0x64);
                var folder = ReadStringField(candidate, 0x78);
                var artist = ReadStringField(candidate, 0x18);
                var title = ReadStringField(candidate, 0x24);

                _reader.ReadSingle(IntPtr.Add(candidate, 0x2C), out var ar);
                _reader.ReadSingle(IntPtr.Add(candidate, 0x30), out var cs);
                _reader.ReadSingle(IntPtr.Add(candidate, 0x34), out var hp);
                _reader.ReadSingle(IntPtr.Add(candidate, 0x38), out var od);

                Console.WriteLine($"  match 0x{match.ToInt64():X} depth {depth}: object 0x{candidate.ToInt64():X} mapId={mapId} setId={setId}");
                Console.WriteLine($"      artist=\"{artist}\" title=\"{title}\" audio=\"{audio}\" folder=\"{folder}\"");
                Console.WriteLine($"      ar={ar:0.##} cs={cs:0.##} hp={hp:0.##} od={od:0.##}");

                if (!_reader.ReadInt32(candidate, out var next) || next == 0)
                {
                    break;
                }

                candidate = ToPtr(next);
            }
        }
    }

    /// <summary>
    /// Converts a 32-bit pointer read as a signed int back to an address without
    /// sign extension (x86 targets can allocate above 0x80000000).
    /// </summary>
    private static IntPtr ToPtr(int value) => new((long)(uint)value);

    /// <summary>Readable mod acronyms for the diagnostics dump (stable bitmask order).</summary>
    private static string DescribeMods(uint mods)
    {
        if (mods == 0)
        {
            return "NM";
        }

        var names = new List<string>();
        void Add(uint bit, string label)
        {
            if ((mods & bit) != 0)
            {
                names.Add(label);
            }
        }

        Add(1u << 0, "NF");
        Add(1u << 1, "EZ");
        Add(1u << 2, "TD");
        Add(1u << 3, "HD");
        Add(1u << 4, "HR");
        Add(1u << 5, "SD");
        Add(1u << 6, "DT");
        Add(1u << 7, "RX");
        Add(1u << 8, "HT");
        Add(1u << 9, "NC");
        Add(1u << 10, "FL");
        Add(1u << 11, "AT");
        Add(1u << 12, "SO");
        Add(1u << 13, "AP");
        Add(1u << 14, "PF");
        Add(1u << 15, "4K");
        Add(1u << 16, "5K");
        Add(1u << 17, "6K");
        Add(1u << 18, "7K");
        Add(1u << 19, "8K");
        Add(1u << 20, "FI");
        Add(1u << 21, "RN");
        Add(1u << 22, "CN");
        Add(1u << 23, "TP");
        Add(1u << 24, "9K");
        Add(1u << 25, "CO");
        Add(1u << 26, "1K");
        Add(1u << 27, "3K");
        Add(1u << 28, "2K");
        Add(1u << 29, "V2");
        Add(1u << 30, "MR");

        return names.Count > 0 ? string.Join(", ", names) : "unknown";
    }

    private string ReadStringField(IntPtr baseAddress, int offset)
    {
        if (!_reader.ReadInt32(IntPtr.Add(baseAddress, offset), out var pointer) || pointer == 0)
        {
            return "-";
        }

        return _reader.ReadDotNetString(ToPtr(pointer), out var text) && !string.IsNullOrEmpty(text)
            ? text!
            : "-";
    }

    private void ReportHitErrorLayout(SignatureResolver resolver, IntPtr playerBase)
    {
        if (!_config.EffectiveFields.TryGetValue("hitErrors", out var field))
        {
            return;
        }

        var listAddress = resolver.ResolveFieldAddress(playerBase, field);
        Console.WriteLine($"--- Hit error layout (field address 0x{listAddress.ToInt64():X}) ---");

        if (listAddress == IntPtr.Zero)
        {
            Console.WriteLine("  list pointer is null");
            return;
        }

        _reader.ReadInt32(IntPtr.Add(listAddress, 4), out var items);
        _reader.ReadInt32(IntPtr.Add(listAddress, 8), out var sizeAt8);
        _reader.ReadInt32(IntPtr.Add(listAddress, 0xC), out var sizeAt12);

        Console.WriteLine($"  items(+0x4)=0x{items:X} size(+0x8)={sizeAt8} size(+0xC)={sizeAt12}");

        if (items > 0x10000)
        {
            _reader.ReadInt32(IntPtr.Add(ToPtr(items), 4), out var arrayLength);
            Console.WriteLine($"  backing array length(+0x4)={arrayLength}");
        }
    }

    /// <summary>
    /// One-shot, low-noise resolution report for normal startup. The full field dump and
    /// module listing stay behind <c>--probe</c> so launching the companion is instant.
    /// </summary>
    public void RunSummary()
    {
        var resolver = new SignatureResolver(_reader, _config);
        var result = resolver.Resolve(force: true);

        if (result.Success)
        {
            Console.WriteLine($"[memory] score block 0x{result.PlayerBase.ToInt64():X} via {result.Method}.");
            return;
        }

        Console.WriteLine($"[memory] no live score block yet ({result.Error}). Waiting for a map.");
    }

    public bool Run()
    {
        Console.WriteLine();
        Console.WriteLine("================ MEMORY DIAGNOSTICS ================");
        Console.WriteLine($"Process : {_reader.Process.ProcessName} (PID {_reader.Process.Id})");
        Console.WriteLine($"Bitness : {(_reader.Is64Bit ? "x64" : "x86")}");

        var resolver = new SignatureResolver(_reader, _config);

        Console.WriteLine("--- Configured profiles ---");
        foreach (var profile in _config.EffectiveProfiles)
        {
            Console.WriteLine($"  {profile.Name}  module={profile.ModuleName}");
            foreach (var step in profile.Steps)
            {
                Console.WriteLine($"      {step}");
            }
        }

        if (_config.EffectiveProfiles.Count == 0)
        {
            Console.WriteLine("  (none) - config has no resolvable profile.");
        }

        var result = resolver.Resolve(force: true);

        Console.WriteLine("--- Resolution trace ---");
        foreach (var line in resolver.LastRunLog)
        {
            Console.WriteLine($"  {line}");
        }

        if (!result.Success)
        {
            Console.WriteLine($"[FAIL] {result.Error}");
            ProbeSignatures();
            DumpModules();
            Console.WriteLine("===================================================");
            return false;
        }

        Console.WriteLine($"[ OK ] Player base 0x{result.PlayerBase.ToInt64():X} via {result.Method}");

        PrintFields(resolver, result.PlayerBase);
        ReportHitErrorLayout(resolver, result.PlayerBase);

        Console.WriteLine("===================================================");
        return true;
    }

    private void ReportField(SignatureResolver resolver, IntPtr playerBase, string name)
    {
        if (!_config.EffectiveFields.TryGetValue(name, out var field))
        {
            Console.WriteLine($"[ - ] {name,-10} not configured");
            return;
        }

        var address = resolver.ResolveFieldAddress(playerBase, field);
        if (address == IntPtr.Zero)
        {
            Console.WriteLine($"[FAIL] {name,-10} offset {field.Offset} -> unreadable pointer");
            return;
        }

        var described = $"{name,-10} @ 0x{address.ToInt64():X}";

        if (string.Equals(field.Type, "string", StringComparison.OrdinalIgnoreCase))
        {
            if (_reader.ReadDotNetString(address, out var text))
            {
                Console.WriteLine($"[ OK ] {described} = \"{text}\"");
            }
            else
            {
                Console.WriteLine($"[ 0 ] {described} = NULL/0x0");
            }

            return;
        }

        if (string.Equals(field.Type, "int32List", StringComparison.OrdinalIgnoreCase))
        {
            var errorReader = new HitErrorReader(_reader);
            if (errorReader.TryRead(address, out var errors))
            {
                var sample = errors.Length > 0
                    ? string.Join(", ", errors.Take(6).Select(e => e.ToString("F0", CultureInfo.InvariantCulture)))
                    : "empty";

                Console.WriteLine($"[ OK ] {described} = count={errors.Length}, sample=[{sample}]");
            }
            else
            {
                Console.WriteLine($"[ 0 ] {described} = list structure invalid");
            }

            return;
        }

        if (string.Equals(field.Type, "modsXor", StringComparison.OrdinalIgnoreCase))
        {
            if (field.Parts is not { Count: > 0 })
            {
                Console.WriteLine($"[FAIL] {described} = modsXor needs parts");
                return;
            }

            var mods = 0;
            var slots = new List<string>();
            foreach (var part in field.Parts)
            {
                var slot = IntPtr.Add(address, (int)SignatureResolver.ParseValue(part));
                if (!_reader.ReadInt32(slot, out var partValue))
                {
                    Console.WriteLine($"[FAIL] {described} = unreadable mods slot {part}");
                    return;
                }

                slots.Add($"0x{partValue:X}");
                mods ^= partValue;
            }

            Console.WriteLine($"[ OK ] {described} = 0x{(uint)mods:X} ({DescribeMods((uint)mods)}) slots {string.Join(" ^ ", slots)}");
            return;
        }

        if (!TryReadScalar(address, field.Type, out var value))
        {
            Console.WriteLine($"[FAIL] {described} = read failed (type {field.Type})");
            return;
        }

        var tag = value != 0 ? " OK " : " 0 ";
        Console.WriteLine($"[{tag}] {described} = {value}");
    }

    private void ProbeSignatures()
    {
        Console.WriteLine("--- Signature probe (whole process) ---");
        var signatures = _config.EffectiveProfiles
            .SelectMany(p => p.Steps)
            .Concat(_config.EffectiveFields.Values
                .Where(f => f.Steps is { Count: > 0 })
                .SelectMany(f => f.Steps!))
            .Where(s => s.Op == ChainStep.Pattern && !string.IsNullOrWhiteSpace(s.Signature))
            .DistinctBy(s => s.Signature);

        foreach (var step in signatures)
        {
            try
            {
                if (!_reader.FindPatternInProcess(step.Signature!, out var match))
                {
                    Console.WriteLine($"  MISS  \"{step.Signature}\"");
                    continue;
                }

                var all = _reader.FindPatternsInProcess(step.Signature!);
                var owner = "heap/JIT";
                foreach (ProcessModule module in _reader.Process.Modules)
                {
                    if (match.ToInt64() >= module.BaseAddress.ToInt64() &&
                        match.ToInt64() < module.BaseAddress.ToInt64() + module.ModuleMemorySize)
                    {
                        owner = module.ModuleName;
                        break;
                    }
                }

                var extra = all.Count > 1
                    ? " | all: " + string.Join(", ", all.Take(6).Select(a => $"0x{a.ToInt64():X}"))
                    : string.Empty;

                Console.WriteLine($"  FOUND \"{step.Signature}\" @ 0x{match.ToInt64():X} ({owner}) matches={all.Count}{extra}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR \"{step.Signature}\": {ex.Message}");
            }
        }
    }

    private void DumpModules()
    {
        Console.WriteLine("--- Loaded modules ---");
        try
        {
            foreach (ProcessModule module in _reader.Process.Modules)
            {
                if (module.BaseAddress == IntPtr.Zero)
                {
                    continue;
                }

                Console.WriteLine($"  {module.ModuleName,-40} base=0x{module.BaseAddress.ToInt64():X}  size=0x{module.ModuleMemorySize:X}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  module dump failed: {ex.Message}");
        }
    }

    private bool TryReadScalar(IntPtr address, string type, out double value)
    {
        value = 0;

        switch (type.ToLowerInvariant())
        {
            case "int16":
                if (_reader.ReadInt16(address, out var s)) { value = s; return true; }
                return false;
            case "int32":
                if (_reader.ReadInt32(address, out var i)) { value = i; return true; }
                return false;
            case "uint32":
                if (_reader.ReadUInt32(address, out var u)) { value = u; return true; }
                return false;
            case "int64":
                if (_reader.ReadInt64(address, out var l)) { value = l; return true; }
                return false;
            case "float":
                if (_reader.ReadSingle(address, out var f)) { value = f; return true; }
                return false;
            case "double":
                if (_reader.ReadDouble(address, out var d)) { value = d; return true; }
                return false;
            default:
                return false;
        }
    }
}
