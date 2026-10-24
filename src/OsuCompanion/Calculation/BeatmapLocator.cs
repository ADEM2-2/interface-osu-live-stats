using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OsuCompanion.Calculation;

/// <summary>
/// Maps beatmap IDs to .osu files inside the osu! Songs folder.
///
/// Indexing runs on a background thread (Songs folders can contain thousands of files),
/// so the live 16 ms streaming loop never blocks. The result is cached on disk keyed by
/// folder timestamp, so a restart only re-reads folders that actually changed instead of
/// parsing every .osu file again.
/// </summary>
public sealed class BeatmapLocator
{
    private const int MaxHeaderLines = 120;

    private readonly string _songsFolder;
    private readonly ConcurrentDictionary<int, string> _index = new();
    private readonly ConcurrentDictionary<int, double> _bpmCache = new();
    private readonly object _gate = new();
    private Task? _indexTask;

    public BeatmapLocator(string songsFolder)
    {
        _songsFolder = songsFolder ?? string.Empty;
    }

    public bool IsIndexed { get; private set; }

    public string? Find(int beatmapId)
    {
        if (beatmapId <= 0)
        {
            return null;
        }

        EnsureIndexStarted();
        return _index.TryGetValue(beatmapId, out var path) ? path : null;
    }

    /// <summary>
    /// Returns the map's base BPM (from the first uninherited timing point) or 0 while
    /// the index is still building. Parsed once per beatmap and cached.
    /// </summary>
    public double GetBpm(int beatmapId)
    {
        if (beatmapId <= 0)
        {
            return 0;
        }

        if (_bpmCache.TryGetValue(beatmapId, out var cached))
        {
            return cached;
        }

        var path = Find(beatmapId);
        if (path is null)
        {
            return 0;
        }

        var bpm = ReadBpm(path);
        if (bpm > 0)
        {
            _bpmCache[beatmapId] = bpm;
        }

        return bpm;
    }

    private static double ReadBpm(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var inTimingPoints = false;

            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith('['))
                {
                    if (inTimingPoints)
                    {
                        break;
                    }

                    inTimingPoints = trimmed.Equals("[TimingPoints]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inTimingPoints || trimmed.Length == 0)
                {
                    continue;
                }

                // Timing point: "offset,beatLength,meter,..." - beatLength is the second column.
                var parts = trimmed.Split(',');
                if (parts.Length < 2)
                {
                    continue;
                }

                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var beatLength))
                {
                    continue;
                }

                // Positive beat length = uninherited (real BPM) point; negative = inherited.
                // Some maps carry editor artifacts, so only accept the playable range
                // (15-600 BPM).
                if (beatLength >= 100 && beatLength <= 4000)
                {
                    return 60000.0 / beatLength;
                }
            }
        }
        catch
        {
            // A malformed or locked .osu file must never break live stats.
        }

        return 0;
    }

    private void EnsureIndexStarted()
    {
        if (_indexTask is not null || string.IsNullOrWhiteSpace(_songsFolder))
        {
            return;
        }

        lock (_gate)
        {
            _indexTask ??= Task.Run(IndexAll);
        }
    }

    private void IndexAll()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var folders = new List<FolderStamp>();
            foreach (var directory in Directory.EnumerateDirectories(_songsFolder))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    folders.Add(new FolderStamp(directory, info.Name, info.LastWriteTimeUtc.Ticks));
                }
                catch
                {
                    // A folder we cannot stat is simply skipped.
                }
            }

            var cached = LoadCache();
            var changed = new List<FolderStamp>();
            var current = new ConcurrentDictionary<string, CachedFolder>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                if (cached is not null &&
                    cached.TryGetValue(folder.Name, out var entry) &&
                    entry.Mtime == folder.Modified)
                {
                    current[folder.Name] = entry;
                    AddToIndex(folder.Path, entry.Maps);
                }
                else
                {
                    changed.Add(folder);
                }
            }

            if (changed.Count > 0)
            {
                Parallel.ForEach(
                    changed,
                    new ParallelOptions { MaxDegreeOfParallelism = 8 },
                    folder =>
                    {
                        var maps = ReadFolder(folder.Path);
                        current[folder.Name] = new CachedFolder { Mtime = folder.Modified, Maps = maps };
                        AddToIndex(folder.Path, maps);
                    });

                SaveCache(current);
            }

            Console.WriteLine($"[beatmap] {folders.Count} folders indexed ({folders.Count - changed.Count} cached, {changed.Count} read) in {started.ElapsedMilliseconds} ms.");
        }
        catch
        {
            // A malformed songs folder must never take the companion down.
        }
        finally
        {
            IsIndexed = true;
        }
    }

    private void AddToIndex(string folderPath, Dictionary<int, string> maps)
    {
        foreach (var (id, fileName) in maps)
        {
            _index.TryAdd(id, Path.Combine(folderPath, fileName));
        }
    }

    private static Dictionary<int, string> ReadFolder(string folderPath)
    {
        var maps = new Dictionary<int, string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, "*.osu"))
            {
                var id = ReadBeatmapId(file);
                if (id > 0)
                {
                    maps.TryAdd(id, Path.GetFileName(file));
                }
            }
        }
        catch
        {
            // Unreadable folders stay out of the index.
        }

        return maps;
    }

    private string CachePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OsuCompanion",
            "beatmap-index.json");

    private Dictionary<string, CachedFolder>? LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<BeatmapIndexCache>(File.ReadAllText(CachePath));
            if (cache is null ||
                !string.Equals(cache.SongsFolder, _songsFolder, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return cache.Folders;
        }
        catch (Exception ex)
        {
            // A corrupt cache is just a cache miss, but say so once.
            Console.WriteLine($"[beatmap] cache ignored: {ex.Message}");
            return null;
        }
    }

    private void SaveCache(ConcurrentDictionary<string, CachedFolder> folders)
    {
        try
        {
            var directory = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new BeatmapIndexCache
            {
                SongsFolder = _songsFolder,
                Folders = new Dictionary<string, CachedFolder>(folders, StringComparer.OrdinalIgnoreCase),
            };

            // Write to a temp file first so a crash mid-write cannot corrupt the cache.
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(cache));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Caching is best effort; failing to save must not stop the index.
            Console.WriteLine($"[beatmap] cache write failed: {ex.Message}");
        }
    }

    private static int ReadBeatmapId(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            for (var i = 0; i < MaxHeaderLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (!line.StartsWith("BeatmapID:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line["BeatmapID:".Length..].Trim();
                return int.TryParse(value, out var id) ? id : 0;
            }
        }
        catch
        {
        }

        return 0;
    }

    private readonly record struct FolderStamp(string Path, string Name, long Modified);

    internal sealed class BeatmapIndexCache
    {
        public BeatmapIndexCache()
        {
        }

        [JsonPropertyName("songsFolder")]
        public string? SongsFolder { get; set; }

        [JsonPropertyName("folders")]
        public Dictionary<string, CachedFolder> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class CachedFolder
    {
        public CachedFolder()
        {
        }

        [JsonPropertyName("mtime")]
        public long Mtime { get; set; }

        /// <summary>Beatmap ID to .osu file name inside the folder.</summary>
        [JsonPropertyName("maps")]
        public Dictionary<int, string> Maps { get; set; } = new();
    }
}
