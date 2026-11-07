using System.Text.RegularExpressions;

namespace ADOFAIVfxRenderer.Core;

public sealed record WorkerCachePruneResult(
    int EntriesBefore,
    int EntriesAfter,
    int DeletedEntries,
    int DeletedTemporaryDirectories,
    long LogicalBytesBefore,
    long LogicalBytesAfter);

public static partial class WorkerCacheRetention
{
    public const int DefaultMaximumEntries = 4;
    public const long DefaultMaximumLogicalBytes = 12L * 1024 * 1024 * 1024;
    public static readonly TimeSpan DefaultTemporaryMaximumAge = TimeSpan.FromDays(1);

    public static void Touch(string preparedDirectory)
    {
        try
        {
            Directory.CreateDirectory(preparedDirectory);
            var marker = Path.Combine(preparedDirectory, ".last-used");
            File.WriteAllText(marker, DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.SetLastWriteTimeUtc(preparedDirectory, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cache recency is an optimization and must never block rendering.
        }
    }

    public static WorkerCachePruneResult Prune(
        string cacheRoot,
        string? preserveKey = null,
        int maximumEntries = DefaultMaximumEntries,
        long maximumLogicalBytes = DefaultMaximumLogicalBytes,
        TimeSpan? temporaryMaximumAge = null)
    {
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumLogicalBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumLogicalBytes));

        Directory.CreateDirectory(cacheRoot);
        var now = DateTime.UtcNow;
        var deletedTemporaryDirectories = 0;
        var unknownDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<CacheEntry>();

        DirectoryInfo[] directories;
        try
        {
            directories = new DirectoryInfo(cacheRoot).GetDirectories();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new WorkerCachePruneResult(0, 0, 0, 0, 0, 0);
        }

        foreach (var directory in directories)
        {
            if (TemporaryDirectoryPattern().IsMatch(directory.Name))
            {
                var maximumAge = temporaryMaximumAge ?? DefaultTemporaryMaximumAge;
                if (now - directory.LastWriteTimeUtc >= maximumAge && TryDeleteDirectory(directory.FullName))
                    deletedTemporaryDirectories++;
                continue;
            }
            if (!CacheKeyPattern().IsMatch(directory.Name)) continue;
            try
            {
                var marker = Path.Combine(directory.FullName, ".last-used");
                var lastUse = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : directory.LastWriteTimeUtc;
                var logicalBytes = Directory.EnumerateFiles(directory.FullName, "*", SearchOption.AllDirectories)
                    .Sum(static path => new FileInfo(path).Length);
                entries.Add(new CacheEntry(directory.Name, directory.FullName, lastUse, logicalBytes));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unknownDirectories.Add(directory.FullName);
            }
        }

        var beforeBytes = entries.Sum(static entry => entry.LogicalBytes);
        var keep = new HashSet<string>(unknownDirectories, StringComparer.OrdinalIgnoreCase);
        long keptBytes = 0;
        var preserved = entries.FirstOrDefault(entry =>
            preserveKey is not null && entry.Key.Equals(preserveKey, StringComparison.OrdinalIgnoreCase));
        if (preserved is not null)
        {
            keep.Add(preserved.Path);
            keptBytes += preserved.LogicalBytes;
        }

        foreach (var entry in entries
                     .Where(entry => preserved is null || !entry.Path.Equals(preserved.Path, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(static entry => entry.LastUseUtc))
        {
            if (keep.Count >= maximumEntries) break;
            if (keptBytes + entry.LogicalBytes > maximumLogicalBytes) continue;
            keep.Add(entry.Path);
            keptBytes += entry.LogicalBytes;
        }

        var deletedEntries = 0;
        foreach (var entry in entries.Where(entry => !keep.Contains(entry.Path)))
        {
            if (TryDeleteDirectory(entry.Path)) deletedEntries++;
        }

        var remaining = entries.Where(entry => Directory.Exists(entry.Path)).ToArray();
        return new WorkerCachePruneResult(
            entries.Count,
            remaining.Length + unknownDirectories.Count,
            deletedEntries,
            deletedTemporaryDirectories,
            beforeBytes,
            remaining.Sum(static entry => entry.LogicalBytes));
    }

    private static bool TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt < 3) Thread.Sleep(150 * (attempt + 1));
            }
        }
        return false;
    }

    private sealed record CacheEntry(string Key, string Path, DateTime LastUseUtc, long LogicalBytes);

    [GeneratedRegex("^[0-9A-F]{20}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CacheKeyPattern();

    [GeneratedRegex("^[0-9A-F]{20}\\.tmp-[0-9A-F]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TemporaryDirectoryPattern();
}
