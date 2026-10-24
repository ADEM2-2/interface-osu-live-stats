using System.Text.Json.Serialization;

namespace OsuCompanion.Models;

public sealed class GameplayData
{
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("playerName")]
    public string? PlayerName { get; set; }

    [JsonPropertyName("mapId")]
    public int MapId { get; set; }

    [JsonPropertyName("bpm")]
    public double Bpm { get; set; }

    [JsonPropertyName("pp")]
    public double Pp { get; set; }

    [JsonPropertyName("totalPp")]
    public double TotalPp { get; set; }

    [JsonPropertyName("ur")]
    public double UnstableRate { get; set; }

    [JsonPropertyName("combo")]
    public int Combo { get; set; }

    [JsonPropertyName("maxCombo")]
    public int MaxCombo { get; set; }

    [JsonPropertyName("accuracy")]
    public double Accuracy { get; set; }

    [JsonPropertyName("mods")]
    public uint Mods { get; set; }

    [JsonPropertyName("score")]
    public long Score { get; set; }

    [JsonPropertyName("hits")]
    public HitCounts Hits { get; set; } = new();
}

public sealed class HitCounts
{
    [JsonPropertyName("300")]
    public int Hit300 { get; set; }

    [JsonPropertyName("100")]
    public int Hit100 { get; set; }

    [JsonPropertyName("50")]
    public int Hit50 { get; set; }

    [JsonPropertyName("0")]
    public int Miss { get; set; }
}
