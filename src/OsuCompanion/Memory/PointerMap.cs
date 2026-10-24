using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuCompanion.Memory;

public sealed class PointerConfig
{
    /// <summary>
    /// Ordered list of resolution profiles. The first profile whose chain produces a
    /// structurally valid player base wins. This is the only supported way to find
    /// the player base: every address comes from a byte signature in the game module.
    /// </summary>
    [JsonPropertyName("profiles")]
    public List<PointerProfile>? Profiles { get; set; }

    [JsonPropertyName("ScoreOffsets")]
    public Dictionary<string, FieldPointer>? ScoreOffsets { get; set; }

    // Legacy format (still supported)
    [JsonPropertyName("PlayerBase")]
    public PlayerBaseConfig? PlayerBase { get; set; }

    [JsonPropertyName("moduleName")]
    public string? ModuleName { get; set; }

    [JsonPropertyName("root")]
    public RootPointer? Root { get; set; }

    [JsonPropertyName("playerOffsets")]
    public List<string>? PlayerOffsets { get; set; }

    [JsonPropertyName("fields")]
    public Dictionary<string, FieldPointer>? Fields { get; set; }

    [JsonPropertyName("calculation")]
    public CalculationConfig Calculation { get; set; } = new();

    // Normalized accessors (populated by Normalize())
    [JsonIgnore]
    public RootPointer EffectiveRoot { get; private set; } = new();

    [JsonIgnore]
    public List<string> EffectivePlayerOffsets { get; private set; } = new();

    [JsonIgnore]
    public Dictionary<string, FieldPointer> EffectiveFields { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public List<PointerProfile> EffectiveProfiles { get; private set; } = new();

    public void Normalize()
    {
        EffectiveProfiles = new List<PointerProfile>();

        if (Profiles is not null)
        {
            foreach (var profile in Profiles)
            {
                if (profile is null || profile.Steps is null || profile.Steps.Count == 0)
                {
                    continue;
                }

                profile.ModuleName ??= ModuleName ?? "osu!.exe";
                EffectiveProfiles.Add(profile);
            }
        }

        // PlayerBase -> Root + PlayerOffsets (legacy)
        if (PlayerBase is not null)
        {
            EffectiveRoot = new RootPointer
            {
                ModuleName = PlayerBase.ModuleName ?? ModuleName ?? "osu!.exe",
                ModuleOffset = PlayerBase.ModuleOffset,
                AbsoluteAddress = PlayerBase.AbsoluteAddress,
                Patterns = PlayerBase.Patterns ?? new(),
                Signature = PlayerBase.Signature,
                SignatureOffset = PlayerBase.SignatureOffset,
                SignatureReadPointer = PlayerBase.SignatureReadPointer,
            };
            EffectivePlayerOffsets = PlayerBase.Offsets ?? new();

            // Allow the legacy PlayerBase block to act as a profile too.
            if (EffectiveProfiles.Count == 0 &&
                (EffectiveRoot.Patterns.Count > 0 || !string.IsNullOrWhiteSpace(EffectiveRoot.Signature)))
            {
                EffectiveProfiles.Add(new PointerProfile
                {
                    Name = "legacy-player-base",
                    ModuleName = EffectiveRoot.ModuleName ?? "osu!.exe",
                    Steps = BuildLegacySteps(EffectiveRoot, EffectivePlayerOffsets),
                });
            }
        }
        else
        {
            EffectiveRoot = Root ?? new RootPointer { ModuleName = ModuleName ?? "osu!.exe" };
            EffectivePlayerOffsets = PlayerOffsets ?? new();
        }

        // ScoreOffsets -> Fields
        if (ScoreOffsets is not null && ScoreOffsets.Count > 0)
        {
            EffectiveFields = new Dictionary<string, FieldPointer>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, field) in ScoreOffsets)
            {
                var fieldName = name.Equals("hitMiss", StringComparison.OrdinalIgnoreCase) ? "miss" : name;
                field.Name = fieldName;
                if (string.IsNullOrWhiteSpace(field.Type) || field.Type == FieldPointer.DefaultType)
                {
                    field.Type = InferType(fieldName);
                }

                EffectiveFields[fieldName] = field;
            }
        }
        else if (Fields is not null && Fields.Count > 0)
        {
            EffectiveFields = new Dictionary<string, FieldPointer>(Fields, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, field) in EffectiveFields)
            {
                field.Name = name;
                if (string.IsNullOrWhiteSpace(field.Type) || field.Type == FieldPointer.DefaultType)
                {
                    field.Type = InferType(name);
                }
            }
        }

        // Merge: if both old Fields and new ScoreOffsets present, prefer ScoreOffsets but keep extras
        if (Fields is not null && Fields.Count > 0 && ScoreOffsets is not null && ScoreOffsets.Count > 0)
        {
            foreach (var (name, field) in Fields)
            {
                if (!EffectiveFields.ContainsKey(name))
                {
                    field.Name = name;
                    if (string.IsNullOrWhiteSpace(field.Type) || field.Type == FieldPointer.DefaultType)
                    {
                        field.Type = InferType(name);
                    }

                    EffectiveFields[name] = field;
                }
            }
        }

        // Also populate legacy properties for backward compat
        if (Root is null) Root = EffectiveRoot;
        if (PlayerOffsets is null) PlayerOffsets = EffectivePlayerOffsets;
        if (Fields is null) Fields = EffectiveFields;
    }

    private static List<ChainStep> BuildLegacySteps(RootPointer root, List<string> offsets)
    {
        var steps = new List<ChainStep>();

        if (root.Patterns.Count > 0)
        {
            var pattern = root.Patterns[0];
            steps.Add(new ChainStep
            {
                Op = ChainStep.Pattern,
                Signature = pattern.Signature,
                Value = pattern.Offset.ToString(),
                ModuleOnly = pattern.ModuleOnly,
            });
            if (pattern.ReadPointer)
            {
                steps.Add(new ChainStep { Op = ChainStep.Read });
            }
        }
        else if (!string.IsNullOrWhiteSpace(root.Signature))
        {
            steps.Add(new ChainStep
            {
                Op = ChainStep.Pattern,
                Signature = root.Signature,
                Value = root.SignatureOffset.ToString(),
                ModuleOnly = true,
            });
            if (root.SignatureReadPointer)
            {
                steps.Add(new ChainStep { Op = ChainStep.Read });
            }
        }
        else if (!string.IsNullOrWhiteSpace(root.ModuleOffset))
        {
            steps.Add(new ChainStep { Op = ChainStep.Module, Value = root.ModuleOffset });
        }
        else if (!string.IsNullOrWhiteSpace(root.AbsoluteAddress))
        {
            steps.Add(new ChainStep { Op = ChainStep.Absolute, Value = root.AbsoluteAddress });
        }

        foreach (var offset in offsets)
        {
            steps.Add(new ChainStep { Op = ChainStep.Add, Value = offset });
            steps.Add(new ChainStep { Op = ChainStep.Read });
        }

        return steps;
    }

    private static string InferType(string fieldName)
    {
        return fieldName.ToLowerInvariant() switch
        {
            "score" => "int32",
            "playername" => "string",
            "hit300" or "hit100" or "hit50" or "hitmiss" or "miss" or "maxcombo" or "combo" => "int16",
            "hiterrors" => "int32List",
            _ => "int32",
        };
    }
}

/// <summary>
/// A single build-specific pointer definition. Every step is explicit so the whole
/// path from module byte pattern to player base can be audited in the JSON file.
/// </summary>
public sealed class PointerProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "default";

    [JsonPropertyName("moduleName")]
    public string? ModuleName { get; set; }

    [JsonPropertyName("steps")]
    public List<ChainStep> Steps { get; set; } = new();
}

public sealed class ChainStep
{
    public const string Pattern = "pattern";
    public const string Read = "read";
    public const string Add = "add";
    public const string Module = "module";
    public const string Absolute = "absolute";

    [JsonPropertyName("op")]
    public string Op { get; set; } = Add;

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>Hex ("0x6C") or decimal ("108") value. May be negative.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("moduleOnly")]
    public bool ModuleOnly { get; set; } = true;

    public override string ToString()
    {
        return Op switch
        {
            Pattern => $"pattern \"{Signature}\" {Value}",
            Read => "read",
            Add => $"add {Value}",
            Module => $"module {Value}",
            Absolute => $"absolute {Value}",
            _ => Op,
        };
    }
}

public sealed class PlayerBaseConfig
{
    [JsonPropertyName("moduleName")]
    public string? ModuleName { get; set; }

    [JsonPropertyName("moduleOffset")]
    public string? ModuleOffset { get; set; }

    [JsonPropertyName("absoluteAddress")]
    public string? AbsoluteAddress { get; set; }

    [JsonPropertyName("offsets")]
    public List<string>? Offsets { get; set; }

    [JsonPropertyName("computed")]
    public bool Computed { get; set; }

    [JsonPropertyName("patterns")]
    public List<SignaturePattern> Patterns { get; set; } = new();

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("signatureOffset")]
    public int SignatureOffset { get; set; }

    [JsonPropertyName("signatureReadPointer")]
    public bool SignatureReadPointer { get; set; }
}

public sealed class RootPointer
{
    [JsonPropertyName("absoluteAddress")]
    public string? AbsoluteAddress { get; set; }

    [JsonPropertyName("moduleOffset")]
    public string? ModuleOffset { get; set; }

    [JsonPropertyName("moduleName")]
    public string? ModuleName { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("signatureOffset")]
    public int SignatureOffset { get; set; }

    [JsonPropertyName("signatureReadPointer")]
    public bool SignatureReadPointer { get; set; }

    [JsonPropertyName("patterns")]
    public List<SignaturePattern> Patterns { get; set; } = new();
}

public sealed class SignaturePattern
{
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("readPointer")]
    public bool ReadPointer { get; set; }

    [JsonPropertyName("moduleOnly")]
    public bool ModuleOnly { get; set; } = true;
}

[JsonConverter(typeof(FieldPointerConverter))]
public sealed class FieldPointer
{
    public const string DefaultType = "auto";

    [JsonIgnore]
    public string Name { get; set; } = "";

    [JsonPropertyName("offset")]
    public string Offset { get; set; } = "0x0";

    [JsonPropertyName("type")]
    public string Type { get; set; } = DefaultType;

    /// <summary>Dereference the field address once before reading (e.g. .NET string objects).</summary>
    [JsonPropertyName("pointer")]
    public bool Pointer { get; set; }

    /// <summary>Optional per-field chain override (used for beatmap data, which lives off a different base).</summary>
    [JsonPropertyName("steps")]
    public List<ChainStep>? Steps { get; set; }

    /// <summary>
    /// Sub-offsets XORed together from the dereferenced field address. Used by the
    /// <c>modsXor</c> type: osu! stores the mod bitmask split across two int32 slots.
    /// </summary>
    [JsonPropertyName("parts")]
    public List<string>? Parts { get; set; }

    [JsonPropertyName("computed")]
    public bool Computed { get; set; }
}

/// <summary>
/// Accepts both the compact form ("combo": "0x628") and the explicit form
/// ("combo": { "offset": "0x628", "type": "int16" }).
/// </summary>
internal sealed class FieldPointerConverter : JsonConverter<FieldPointer>
{
    public override FieldPointer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new FieldPointer { Offset = reader.GetString() ?? "0x0", Type = FieldPointer.DefaultType };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Field pointer must be a string or an object.");
        }

        var field = new FieldPointer();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return field;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            reader.Read();

            switch (property?.ToLowerInvariant())
            {
                case "offset":
                    field.Offset = reader.TokenType == JsonTokenType.String
                        ? reader.GetString() ?? "0x0"
                        : reader.GetInt32().ToString();
                    break;
                case "type":
                    field.Type = reader.GetString() ?? FieldPointer.DefaultType;
                    break;
                case "pointer":
                    field.Pointer = reader.GetBoolean();
                    break;
                case "computed":
                    field.Computed = reader.GetBoolean();
                    break;
                case "steps":
                    field.Steps = JsonSerializer.Deserialize<List<ChainStep>>(ref reader, options);
                    break;
                case "parts":
                    field.Parts = JsonSerializer.Deserialize<List<string>>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return field;
    }

    public override void Write(Utf8JsonWriter writer, FieldPointer value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("offset", value.Offset);
        writer.WriteString("type", value.Type);
        if (value.Pointer) writer.WriteBoolean("pointer", true);
        if (value.Computed) writer.WriteBoolean("computed", true);
        if (value.Steps is { Count: > 0 })
        {
            writer.WritePropertyName("steps");
            JsonSerializer.Serialize(writer, value.Steps, options);
        }

        if (value.Parts is { Count: > 0 })
        {
            writer.WritePropertyName("parts");
            JsonSerializer.Serialize(writer, value.Parts, options);
        }

        writer.WriteEndObject();
    }
}

public sealed class CalculationConfig
{
    [JsonPropertyName("songsFolder")]
    public string? SongsFolder { get; set; }

    [JsonPropertyName("oppaiPath")]
    public string? OppaiPath { get; set; }

    [JsonPropertyName("ppIntervalMs")]
    public int PpIntervalMs { get; set; } = 500;

    [JsonPropertyName("starRating")]
    public double StarRating { get; set; } = 5.0;

    [JsonPropertyName("overallDifficulty")]
    public double OverallDifficulty { get; set; } = 8.0;
}
