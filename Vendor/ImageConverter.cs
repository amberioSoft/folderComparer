using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ImageConverter;

public static class Sizes
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {Units[unit]}";
    }
}

public static class Paths
{
    public static string Clean(string value)
    {
        string trimmed = value.Trim().Trim('"').Trim();
        if (trimmed.Length > 3 && (trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar)))
            trimmed = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed;
    }

    public static string DefaultImagesFolderFor(string inputDirectory)
    {
        string full = Path.GetFullPath(Clean(inputDirectory));
        string name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name)) return Path.Combine(Directory.GetCurrentDirectory(), "images");
        string? parent = Path.GetDirectoryName(full);
        return string.IsNullOrEmpty(parent)
            ? Path.Combine(Directory.GetCurrentDirectory(), name + "-images")
            : Path.Combine(parent, name + "-images");
    }

    public static bool LooksLikeImagesFolder(string path)
    {
        string cleaned = Clean(path);
        if (File.Exists(cleaned)) return cleaned.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        return Directory.Exists(cleaned) && Directory.EnumerateFiles(cleaned, "*.png").Any();
    }

    public static string ResolveKeys(string? explicitPath, string? hint, bool forPacking)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(Clean(explicitPath));

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "keys"));
        if (!string.IsNullOrWhiteSpace(hint))
        {
            string cleaned = Path.GetFullPath(Clean(hint));
            string folder = Directory.Exists(cleaned) ? cleaned : Path.GetDirectoryName(cleaned) ?? cleaned;
            candidates.Add(Path.Combine(folder, "keys"));
            string? parent = Path.GetDirectoryName(folder);
            if (!string.IsNullOrEmpty(parent)) candidates.Add(Path.Combine(parent, "keys"));
        }

        string? executable = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(executable)) candidates.Add(Path.Combine(executable, "keys"));

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (HasKeys(candidate, forPacking))
                return candidate;

        string first = string.IsNullOrWhiteSpace(explicitPath)
            ? Path.Combine(Directory.GetCurrentDirectory(), "keys")
            : Clean(explicitPath);
        string needed = forPacking
            ? $"{KeyStore.RecipientPublicFile} and {KeyStore.SenderPrivateFile}"
            : $"{KeyStore.RecipientPrivateFile} and {KeyStore.SenderPublicFile}";

        throw new FileNotFoundException(
            $"Could not find {needed}. Looked in: {string.Join("; ", candidates.Distinct(StringComparer.OrdinalIgnoreCase))}. "
            + $"Create the four key files once with: imgconv keygen --out \"{first}\"");
    }

    public static bool HasKeys(string directory, bool forPacking)
    {
        if (!Directory.Exists(directory)) return false;
        return forPacking
            ? File.Exists(Path.Combine(directory, KeyStore.RecipientPublicFile))
              && File.Exists(Path.Combine(directory, KeyStore.SenderPrivateFile))
            : File.Exists(Path.Combine(directory, KeyStore.RecipientPrivateFile))
              && File.Exists(Path.Combine(directory, KeyStore.SenderPublicFile));
    }
}

public static class ProcessTuning
{
    public const string Default = "below";

    public static string Apply(string? priority)
    {
        string wanted = string.IsNullOrWhiteSpace(priority) ? Default : priority.Trim().ToLowerInvariant();
        var priorityClass = wanted switch
        {
            "idle" or "low" => ProcessPriorityClass.Idle,
            "below" or "belownormal" => ProcessPriorityClass.BelowNormal,
            "normal" => ProcessPriorityClass.Normal,
            "high" => ProcessPriorityClass.High,
            _ => throw new ArgumentException($"Unknown priority '{priority}'. Use idle, below, normal or high.")
        };

        try
        {
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = priorityClass;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"{wanted} (not applied)";
        }

        return wanted;
    }
}

public sealed class Scratch : IDisposable
{
    public const long BlockLimit = int.MaxValue;
    public const long SafeLimit = 1536L * 1024 * 1024;

    private readonly Dictionary<string, MemoryStream> _buffers = new(StringComparer.Ordinal);

    public static string Describe => "memory only, no temporary files";

    public Stream Create(string name)
    {
        var buffer = new MemoryStream();
        Replace(name, buffer);
        return buffer;
    }

    public Stream Allocate(string name, long length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Guard(length);

        var buffer = new MemoryStream(new byte[length], 0, (int)length, true, true);
        Replace(name, buffer);
        return buffer;
    }

    public Stream Open(string name)
    {
        var buffer = _buffers[name];
        buffer.Position = 0;
        return buffer;
    }

    public void Done(Stream stream)
    {
    }

    public long Length(string name) => _buffers[name].Length;

    public byte[] Buffer(string name) => _buffers[name].GetBuffer();

    public void SaveCopy(string name, string destination)
    {
        var buffer = _buffers[name];
        long position = buffer.Position;
        buffer.Position = 0;
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            buffer.CopyTo(output);
        }
        buffer.Position = position;
    }

    public void Release(string name)
    {
        if (_buffers.Remove(name, out var buffer)) buffer.Dispose();
    }

    public static void Guard(long length)
    {
        if (length <= SafeLimit) return;
        throw new InvalidOperationException(
            $"{Sizes.Format(length)} has to be held in memory at once, and the safe ceiling is {Sizes.Format(SafeLimit)}. "
            + "Send the folder in smaller pieces, or exclude the heavy files.");
    }

    private void Replace(string name, MemoryStream buffer)
    {
        if (_buffers.Remove(name, out var previous)) previous.Dispose();
        _buffers[name] = buffer;
    }

    public void Dispose()
    {
        foreach (var buffer in _buffers.Values) buffer.Dispose();
        _buffers.Clear();
    }
}

[Flags]
public enum EntryFlags : byte
{
    None = 0,
    Directory = 1,
    DedupReference = 2,
    Bcj86Filtered = 4,
    StoredBucket = 8,
    ReadOnly = 16
}

public sealed class ArchiveEntry
{
    public string Path = string.Empty;
    public EntryFlags Flags;
    public long Length;
    public long ModifiedUnixSeconds;
    public int ReferenceIndex = -1;
    public string? SourceFullPath;
    public byte[]? ContentHash;

    public bool IsDirectory => (Flags & EntryFlags.Directory) != 0;
    public bool IsReference => (Flags & EntryFlags.DedupReference) != 0;
    public bool IsStored => (Flags & EntryFlags.StoredBucket) != 0;
    public bool HasBody => !IsDirectory && !IsReference;
}

public sealed class ArchiveExtractor
{
    public ArchiveStats Stats { get; } = new();

    public ArchiveSummary Expected { get; } = new();

    public ArchiveSummary Actual { get; } = new();

    public List<string> FilterPatterns { get; } = [];

    private readonly Dictionary<string, byte[]> _writtenHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _writtenLengths = new(StringComparer.Ordinal);

    public void Extract(Stream archive, string targetDirectory, Action<string>? log = null)
    {
        if (!archive.CanSeek) throw new ArgumentException("Archive stream must be seekable.", nameof(archive));

        long origin = archive.Position;
        var reader = new BinaryReader(archive, Encoding.UTF8, true);
        uint magic = reader.ReadUInt32();
        if (magic != ArchiveWriter.Magic) throw new InvalidDataException("Not a ImageConverter archive.");
        byte version = reader.ReadByte();
        if (version != ArchiveWriter.FormatVersion) throw new InvalidDataException($"Unsupported archive version {version}.");
        reader.ReadByte();
        long compressedLength = reader.ReadInt64();
        long storedLength = reader.ReadInt64();
        Stats.DeduplicatedBytes = reader.ReadInt64();
        Expected.OriginalBytes = reader.ReadInt64();
        Expected.FileCount = reader.ReadInt32();
        Expected.DirectoryCount = reader.ReadInt32();
        Expected.TreeDigest = reader.ReadBytes(32);

        string root = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(root);

        List<ArchiveEntry> entries;
        using (var inflate = FramedBrotli.OpenReader(archive, origin + ArchiveWriter.FixedHeaderSize, compressedLength))
        {
            entries = ReadTable(inflate);
            foreach (var entry in entries)
            {
                if (!entry.HasBody || entry.IsStored) continue;
                WriteBody(inflate, root, entry);
            }
        }

        archive.Position = origin + ArchiveWriter.FixedHeaderSize + compressedLength;
        using (var storedStream = new BoundedStream(archive, storedLength))
        {
            foreach (var entry in entries)
            {
                if (!entry.HasBody || !entry.IsStored) continue;
                WriteBody(storedStream, root, entry);
            }
        }

        foreach (var entry in entries)
        {
            if (!entry.IsReference) continue;
            string source = ResolvePath(root, entries[entry.ReferenceIndex].Path);
            string destination = ResolvePath(root, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
            ApplyMetadata(destination, entry);
            _writtenHashes[entry.Path] = _writtenHashes[entries[entry.ReferenceIndex].Path];
            _writtenLengths[entry.Path] = _writtenLengths[entries[entry.ReferenceIndex].Path];
            Stats.RawBytes += _writtenLengths[entry.Path];
            Stats.DuplicateCount++;
        }

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory) continue;
            string path = ResolvePath(root, entry.Path);
            Directory.CreateDirectory(path);
            Stats.DirectoryCount++;
        }

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory) continue;
            string path = ResolvePath(root, entry.Path);
            Directory.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedUnixSeconds).UtcDateTime);
        }

        Actual.OriginalBytes = _writtenLengths.Values.Sum();
        Actual.FileCount = _writtenLengths.Count;
        Actual.DirectoryCount = Stats.DirectoryCount;
        Actual.TreeDigest = TreeDigest.Compute(entries.Select(e => e.IsDirectory
            ? (e.Path, true, 0L, (byte[]?)null)
            : (e.Path, false, _writtenLengths[e.Path], _writtenHashes[e.Path])));

        log?.Invoke($"restored {Stats.FileCount} files, {Stats.DirectoryCount} dirs, {Stats.DuplicateCount} duplicates");
    }

    private void WriteBody(Stream source, string root, ArchiveEntry entry)
    {
        string destination = ResolvePath(root, entry.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;

        if ((entry.Flags & EntryFlags.Bcj86Filtered) != 0)
        {
            byte[] data = new byte[entry.Length];
            ReadExact(source, data);
            Bcj86.Apply(data, false);
            File.WriteAllBytes(destination, data);
            digest.AppendData(data);
            written = data.Length;
        }
        else
        {
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            byte[] buffer = new byte[1 << 20];
            long remaining = entry.Length;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = source.Read(buffer, 0, want);
                if (read <= 0) throw new EndOfStreamException($"Archive truncated inside {entry.Path}.");
                output.Write(buffer, 0, read);
                digest.AppendData(buffer, 0, read);
                written += read;
                remaining -= read;
            }
        }

        _writtenHashes[entry.Path] = digest.GetHashAndReset();
        _writtenLengths[entry.Path] = written;
        ApplyMetadata(destination, entry);
        Stats.FileCount++;
        Stats.RawBytes += written;
    }

    private static void ApplyMetadata(string path, ArchiveEntry entry)
    {
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedUnixSeconds).UtcDateTime);
        if ((entry.Flags & EntryFlags.ReadOnly) != 0)
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }

    private List<ArchiveEntry> ReadTable(Stream source)
    {
        var header = new byte[4];
        ReadExact(source, header);
        int patternCount = BitConverter.ToInt32(header);
        if (patternCount < 0 || patternCount > 100_000) throw new InvalidDataException("Corrupt archive pattern list.");
        for (int i = 0; i < patternCount; i++) FilterPatterns.Add(ReadString(source));

        ReadExact(source, header);
        int count = BitConverter.ToInt32(header);
        if (count < 0 || count > 50_000_000) throw new InvalidDataException("Corrupt archive entry table.");

        var entries = new List<ArchiveEntry>(count);
        var scratch = new byte[8];
        for (int i = 0; i < count; i++)
        {
            ReadExact(source, scratch.AsSpan(0, 2));
            int pathLength = BitConverter.ToUInt16(scratch);
            byte[] pathBytes = new byte[pathLength];
            ReadExact(source, pathBytes);

            ReadExact(source, scratch.AsSpan(0, 1));
            var flags = (EntryFlags)scratch[0];

            ReadExact(source, scratch.AsSpan(0, 8));
            long modified = BitConverter.ToInt64(scratch);

            var entry = new ArchiveEntry
            {
                Path = Encoding.UTF8.GetString(pathBytes),
                Flags = flags,
                ModifiedUnixSeconds = modified
            };

            if (entry.HasBody)
            {
                ReadExact(source, scratch.AsSpan(0, 8));
                entry.Length = BitConverter.ToInt64(scratch);
            }

            if (entry.IsReference)
            {
                ReadExact(source, scratch.AsSpan(0, 4));
                entry.ReferenceIndex = BitConverter.ToInt32(scratch);
            }

            entries.Add(entry);
        }
        return entries;
    }

    private static string ReadString(Stream source)
    {
        int length = 0;
        int shift = 0;
        Span<byte> one = stackalloc byte[1];
        while (true)
        {
            ReadExact(source, one);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) throw new InvalidDataException("Corrupt string length in the archive.");
        }

        byte[] text = new byte[length];
        ReadExact(source, text);
        return Encoding.UTF8.GetString(text);
    }

    private static void ReadExact(Stream source, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer[total..]);
            if (read <= 0) throw new EndOfStreamException("Archive stream ended unexpectedly.");
            total += read;
        }
    }

    private static string ResolvePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Rejected unsafe archive path: {relative}");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Rejected archive path outside target: {relative}");
        return full;
    }
}

public sealed class ArchiveStats
{
    public int FileCount;
    public int DirectoryCount;
    public int DuplicateCount;
    public int StoredCount;
    public int FilteredCount;
    public long RawBytes;
    public long DeduplicatedBytes;
    public long CompressedBytes;
    public int SkippedFileCount;
    public int SkippedDirectoryCount;
    public long SkippedBytes;
    public List<SkippedPath> Skipped = [];
}

public sealed record SkippedPath(string Path, bool IsDirectory, long Length);

public sealed class ArchiveSummary
{
    public long OriginalBytes;
    public int FileCount;
    public int DirectoryCount;
    public byte[] TreeDigest = [];

    public bool Matches(ArchiveSummary other)
        => OriginalBytes == other.OriginalBytes
           && FileCount == other.FileCount
           && DirectoryCount == other.DirectoryCount
           && TreeDigest.AsSpan().SequenceEqual(other.TreeDigest);
}

public sealed class ArchiveWriter
{
    public const uint Magic = 0x31414349;
    public const byte FormatVersion = 4;
    public const int FixedHeaderSize = 78;
    private const int ProbeSize = 64 * 1024;
    private const int FilterProbeSize = 256 * 1024;
    private const long MaxFilterableLength = 64L * 1024 * 1024;

    public static bool LooksAlreadyCompressed(string path) => AlreadyCompressed.Contains(Path.GetExtension(path));

    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".heic", ".heif", ".jxl",
        ".mp3", ".mp4", ".m4a", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm",
        ".flac", ".ogg", ".oga", ".opus", ".aac", ".wma",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".xz", ".bz2", ".zst", ".br", ".lz4", ".cab",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".apk", ".jar", ".nupkg",
        ".woff", ".woff2", ".crx", ".appx", ".msix", ".iso"
    };

    public ArchiveStats Stats { get; } = new();

    public ArchiveSummary Summary { get; } = new();

    public PathFilter Filter { get; set; } = PathFilter.Empty;

    public bool MeasureSkipped { get; set; }

    public int Quality { get; set; } = BrotliCodec.MaxQuality;

    public int Threads { get; set; }

    public int ChunkSize { get; set; }

    private string _root = string.Empty;

    public List<ArchiveEntry> Preview(string rootDirectory, Action<string>? log = null)
    {
        _root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Collect(_root, log, false);
    }

    public void Create(string rootDirectory, Stream output, Action<string>? log = null)
    {
        if (!output.CanSeek) throw new ArgumentException("Archive output stream must be seekable.", nameof(output));

        _root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var entries = Collect(_root, log, true);

        long headerStart = output.Position;
        var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write((byte)0);
        writer.Write(0L);
        writer.Write(0L);
        writer.Write(0L);
        writer.Write(Summary.OriginalBytes);
        writer.Write(Summary.FileCount);
        writer.Write(Summary.DirectoryCount);
        writer.Write(Summary.TreeDigest);
        writer.Flush();

        long compressedLength;
        using (var producer = new ProducerStream(entries, Filter.Patterns.ToList()))
        {
            compressedLength = FramedBrotli.Compress(producer, output, Quality, BrotliCodec.MaxWindow, ChunkSize, Threads);
        }

        long storedStart = output.Position;
        byte[] copyBuffer = new byte[1 << 20];
        foreach (var entry in entries)
        {
            if (!entry.HasBody || !entry.IsStored) continue;
            using var fs = File.OpenRead(entry.SourceFullPath!);
            int read;
            while ((read = fs.Read(copyBuffer, 0, copyBuffer.Length)) > 0)
                output.Write(copyBuffer, 0, read);
        }
        long storedLength = output.Position - storedStart;

        long end = output.Position;
        output.Position = headerStart + 6;
        var fix = new BinaryWriter(output, Encoding.UTF8, true);
        fix.Write(compressedLength);
        fix.Write(storedLength);
        fix.Write(Stats.DeduplicatedBytes);
        fix.Flush();
        output.Position = end;
        Stats.CompressedBytes = end - headerStart;
    }

    private List<ArchiveEntry> Collect(string root, Action<string>? log, bool analyze)
    {
        var directories = new List<ArchiveEntry>();
        var files = new List<ArchiveEntry>();
        var byHash = new Dictionary<string, int>(StringComparer.Ordinal);

        var pending = new List<(FileInfo Info, string Rel)>();
        Walk(root, directories, pending);
        pending.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
        directories.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        foreach (var (info, rel) in pending)
        {
            Stats.RawBytes += info.Length;

            var entry = new ArchiveEntry
            {
                Path = rel,
                Length = info.Length,
                ModifiedUnixSeconds = ToUnix(info.LastWriteTimeUtc),
                SourceFullPath = info.FullName
            };
            if (info.IsReadOnly) entry.Flags |= EntryFlags.ReadOnly;

            if (!analyze)
            {
                files.Add(entry);
                continue;
            }

            entry.ContentHash = HashFile(info.FullName);
            string hash = Convert.ToHexString(entry.ContentHash);
            if (info.Length > 0 && byHash.TryGetValue(hash, out int refIndex))
            {
                entry.Flags |= EntryFlags.DedupReference;
                entry.ReferenceIndex = refIndex;
                Stats.DuplicateCount++;
                files.Add(entry);
                continue;
            }

            Classify(entry, info);
            Stats.DeduplicatedBytes += info.Length;
            byHash[hash] = files.Count;
            files.Add(entry);
        }

        var compressible = files.Where(e => e.HasBody && !e.IsStored).ToList();
        var stored = files.Where(e => e.HasBody && e.IsStored).ToList();
        var references = files.Where(e => e.IsReference).ToList();

        compressible.Sort(CompareForLocality);
        stored.Sort(CompareForLocality);

        var entries = new List<ArchiveEntry>(files.Count + directories.Count);
        entries.AddRange(compressible);
        entries.AddRange(stored);
        entries.AddRange(references);
        entries.AddRange(directories);

        var indexMap = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++) indexMap[entries[i].Path] = i;
        foreach (var reference in references)
            reference.ReferenceIndex = indexMap[files[reference.ReferenceIndex].Path];

        Stats.FileCount = files.Count;
        Stats.DirectoryCount = directories.Count;
        Stats.StoredCount = stored.Count;

        Summary.OriginalBytes = Stats.RawBytes;
        Summary.FileCount = files.Count;
        Summary.DirectoryCount = directories.Count;
        Summary.TreeDigest = analyze
            ? TreeDigest.Compute(entries.Select(e => (e.Path, e.IsDirectory, e.Length, e.ContentHash)))
            : new byte[32];

        log?.Invoke($"files {files.Count}, dirs {directories.Count}, duplicates {Stats.DuplicateCount}, stored as-is {stored.Count}, x86-filtered {Stats.FilteredCount}, excluded {Stats.SkippedFileCount} files and {Stats.SkippedDirectoryCount} folders");
        return entries;
    }

    private void Walk(string root, List<ArchiveEntry> directories, List<(FileInfo Info, string Rel)> files)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current).EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                string relative = Relative(root, child.FullName);
                bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;

                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Note(relative, isDirectory, 0);
                    continue;
                }

                if (Filter.IsExcluded(relative, isDirectory))
                {
                    Note(relative, isDirectory, isDirectory ? 0 : ((FileInfo)child).Length);
                    continue;
                }

                if (isDirectory)
                {
                    directories.Add(new ArchiveEntry
                    {
                        Path = relative,
                        Flags = EntryFlags.Directory,
                        ModifiedUnixSeconds = ToUnix(child.LastWriteTimeUtc)
                    });
                    queue.Enqueue(child.FullName);
                }
                else
                {
                    files.Add(((FileInfo)child, relative));
                }
            }
        }
    }

    private void Note(string relative, bool isDirectory, long length)
    {
        if (isDirectory)
        {
            Stats.SkippedDirectoryCount++;
            if (MeasureSkipped) length = DirectorySize(relative);
        }
        else
        {
            Stats.SkippedFileCount++;
        }

        Stats.SkippedBytes += length;
        if (Stats.Skipped.Count < 10_000) Stats.Skipped.Add(new SkippedPath(relative, isDirectory, length));
    }

    private long DirectorySize(string relative)
    {
        try
        {
            long total = 0;
            var directory = new DirectoryInfo(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories)) total += file.Length;
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static int CompareForLocality(ArchiveEntry a, ArchiveEntry b)
    {
        int byExt = string.CompareOrdinal(
            Path.GetExtension(a.Path).ToLowerInvariant(),
            Path.GetExtension(b.Path).ToLowerInvariant());
        if (byExt != 0) return byExt;
        int bySize = a.Length.CompareTo(b.Length);
        return bySize != 0 ? bySize : string.CompareOrdinal(a.Path, b.Path);
    }

    private void Classify(ArchiveEntry entry, FileInfo info)
    {
        if (info.Length == 0) return;

        if (AlreadyCompressed.Contains(Path.GetExtension(entry.Path)))
        {
            entry.Flags |= EntryFlags.StoredBucket;
            return;
        }

        byte[] sample = ReadHead(info.FullName, ProbeSize);
        if (sample.Length >= 4096)
        {
            byte[] test = BrotliCodec.CompressBuffer(sample, 4, 22);
            if (test.Length > sample.Length * 0.97)
            {
                entry.Flags |= EntryFlags.StoredBucket;
                return;
            }
        }

        if (info.Length <= MaxFilterableLength && Bcj86.LooksLikeExecutable(sample))
        {
            byte[] plainSample = ReadHead(info.FullName, FilterProbeSize);
            byte[] filteredSample = plainSample.ToArray();
            Bcj86.Apply(filteredSample, true);
            int plain = BrotliCodec.CompressBuffer(plainSample, 4, 22).Length;
            int filtered = BrotliCodec.CompressBuffer(filteredSample, 4, 22).Length;
            if (filtered < plain * 0.99)
            {
                entry.Flags |= EntryFlags.Bcj86Filtered;
                Stats.FilteredCount++;
            }
        }
    }

    private static byte[] ReadHead(string path, int count)
    {
        using var fs = File.OpenRead(path);
        byte[] buffer = new byte[(int)Math.Min(count, fs.Length)];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = fs.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total == buffer.Length ? buffer : buffer[..total];
    }

    private static byte[] HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return sha.ComputeHash(fs);
    }

    private static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static long ToUnix(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesWritten { get; private set; }
        public CountingStream(Stream inner) => _inner = inner;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            BytesWritten += buffer.Length;
        }
    }

    private sealed class ProducerStream : Stream
    {
        private readonly List<ArchiveEntry> _entries;
        private MemoryStream? _table;
        private int _index;
        private Stream? _current;
        private byte[]? _buffered;
        private int _bufferedOffset;

        public ProducerStream(List<ArchiveEntry> entries, List<string> patterns)
        {
            _entries = entries;
            _table = BuildTable(entries, patterns);
        }

        private static MemoryStream BuildTable(List<ArchiveEntry> entries, List<string> patterns)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write(patterns.Count);
            foreach (string pattern in patterns) w.Write(pattern);
            w.Write(entries.Count);
            foreach (var e in entries)
            {
                byte[] path = Encoding.UTF8.GetBytes(e.Path);
                w.Write((ushort)path.Length);
                w.Write(path);
                w.Write((byte)e.Flags);
                w.Write(e.ModifiedUnixSeconds);
                if (e.HasBody) w.Write(e.Length);
                if (e.IsReference) w.Write(e.ReferenceIndex);
            }
            w.Flush();
            ms.Position = 0;
            return ms;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;

            if (_table != null)
            {
                int read = _table.Read(buffer);
                if (read > 0) return read;
                _table.Dispose();
                _table = null;
            }

            while (true)
            {
                if (_buffered != null)
                {
                    int take = Math.Min(buffer.Length, _buffered.Length - _bufferedOffset);
                    _buffered.AsSpan(_bufferedOffset, take).CopyTo(buffer);
                    _bufferedOffset += take;
                    if (_bufferedOffset >= _buffered.Length) _buffered = null;
                    if (take > 0) return take;
                    continue;
                }

                if (_current != null)
                {
                    int read = _current.Read(buffer);
                    if (read > 0) return read;
                    _current.Dispose();
                    _current = null;
                }

                if (_index >= _entries.Count) return 0;
                var entry = _entries[_index++];
                if (!entry.HasBody || entry.IsStored) continue;

                if ((entry.Flags & EntryFlags.Bcj86Filtered) != 0)
                {
                    byte[] data = File.ReadAllBytes(entry.SourceFullPath!);
                    Bcj86.Apply(data, true);
                    _buffered = data;
                    _bufferedOffset = 0;
                }
                else
                {
                    _current = File.OpenRead(entry.SourceFullPath!);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _table?.Dispose();
                _current?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

public static class Bcj86
{
    public static bool LooksLikeExecutable(ReadOnlySpan<byte> head)
        => head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';

    public static void Apply(Span<byte> data, bool encoding)
    {
        int i = 0;
        while (i + 5 <= data.Length)
        {
            byte op = data[i];
            if (op != 0xE8 && op != 0xE9)
            {
                i++;
                continue;
            }

            int rel = data[i + 1] | (data[i + 2] << 8) | (data[i + 3] << 16) | (data[i + 4] << 24);
            int delta = i + 5;
            int value = encoding ? rel + delta : rel - delta;
            data[i + 1] = (byte)value;
            data[i + 2] = (byte)(value >> 8);
            data[i + 3] = (byte)(value >> 16);
            data[i + 4] = (byte)(value >> 24);
            i += 5;
        }
    }
}

public sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _remaining;

    public BoundedStream(Stream inner, long length, bool leaveOpen = true)
    {
        _inner = inner;
        _remaining = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0) return 0;
        if (buffer.Length > _remaining) buffer = buffer[..(int)_remaining];
        int read = _inner.Read(buffer);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
}

public static class BrotliCodec
{
    public const int MaxQuality = 11;
    public const int MaxWindow = 24;

    public static void Compress(Stream input, Stream output, int quality = MaxQuality, int window = MaxWindow)
    {
        var encoder = new BrotliEncoder(quality, window);
        try
        {
            byte[] inBuf = new byte[1 << 20];
            byte[] outBuf = new byte[1 << 20];
            int read;
            while ((read = input.Read(inBuf, 0, inBuf.Length)) > 0)
            {
                var src = new ReadOnlySpan<byte>(inBuf, 0, read);
                while (!src.IsEmpty)
                {
                    var status = encoder.Compress(src, outBuf, out int consumed, out int written, false);
                    if (status == OperationStatus.InvalidData)
                        throw new InvalidDataException("Brotli encoder rejected the input.");
                    if (written > 0) output.Write(outBuf, 0, written);
                    src = src[consumed..];
                    if (consumed == 0 && written == 0) break;
                }
            }
            while (true)
            {
                var status = encoder.Compress(ReadOnlySpan<byte>.Empty, outBuf, out _, out int written, true);
                if (status == OperationStatus.InvalidData)
                    throw new InvalidDataException("Brotli encoder failed to finalize the stream.");
                if (written > 0) output.Write(outBuf, 0, written);
                if (status == OperationStatus.Done) break;
                if (written == 0) break;
            }
        }
        finally
        {
            encoder.Dispose();
        }
    }

    public static byte[] CompressBuffer(ReadOnlySpan<byte> data, int quality, int window = MaxWindow)
        => CompressBuffer(data.ToArray(), 0, data.Length, quality, window);

    public static byte[] CompressBuffer(byte[] data, int offset, int count, int quality, int window = MaxWindow)
    {
        using var src = new MemoryStream(data, offset, count, false);
        using var dst = new MemoryStream();
        Compress(src, dst, quality, window);
        return dst.ToArray();
    }

    public static Stream OpenDecompressor(Stream compressed, bool leaveOpen = true)
        => new BrotliStream(compressed, CompressionMode.Decompress, leaveOpen);
}

public static class FramedBrotli
{
    public const int DefaultChunkSize = 16 * 1024 * 1024;
    private const int TableEntrySize = 12;
    private const long InFlightBudget = 320L * 1024 * 1024;

    public static int DefaultDegree => Math.Clamp(Environment.ProcessorCount / 3, 1, 6);

    public static int MaximumDegree => Math.Clamp(Environment.ProcessorCount, 1, 32);

    public static long Compress(
        Stream input,
        Stream output,
        int quality,
        int window = BrotliCodec.MaxWindow,
        int chunkSize = DefaultChunkSize,
        int degree = 0)
    {
        if (chunkSize <= 0) chunkSize = DefaultChunkSize;
        if (degree <= 0) degree = DefaultDegree;
        degree = (int)Math.Max(1, Math.Min(degree, InFlightBudget / chunkSize));

        var table = new List<(long Compressed, int Raw)>();
        var inFlight = new Queue<(Task<byte[]> Task, int Raw)>();
        long written = 0;
        bool finished = false;

        while (!finished || inFlight.Count > 0)
        {
            while (!finished && inFlight.Count < degree)
            {
                byte[] buffer = new byte[chunkSize];
                int read = ReadChunk(input, buffer);
                if (read == 0)
                {
                    finished = true;
                    break;
                }

                int length = read;
                inFlight.Enqueue((RunOnWorker(() => BrotliCodec.CompressBuffer(buffer, 0, length, quality, window)), length));
                if (read < chunkSize) finished = true;
            }

            if (inFlight.Count == 0) continue;

            var (task, raw) = inFlight.Dequeue();
            byte[] compressed = task.GetAwaiter().GetResult();
            output.Write(compressed, 0, compressed.Length);
            table.Add((compressed.Length, raw));
            written += compressed.Length;
        }

        Span<byte> entry = stackalloc byte[TableEntrySize];
        foreach (var (compressed, raw) in table)
        {
            BinaryPrimitives.WriteInt64LittleEndian(entry, compressed);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], raw);
            output.Write(entry);
            written += TableEntrySize;
        }

        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, table.Count);
        output.Write(count);
        return written + 4;
    }

    public static Stream OpenReader(Stream archive, long sectionStart, long sectionLength)
        => new FramedReader(archive, sectionStart, sectionLength);

    private static Task<byte[]> RunOnWorker(Func<byte[]> work)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        worker.Start();
        return completion.Task;
    }

    private static int ReadChunk(Stream input, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private sealed class FramedReader : Stream
    {
        private readonly Stream _archive;
        private readonly long _sectionStart;
        private readonly List<(long Offset, long Compressed, int Raw)> _frames = [];
        private byte[] _current = [];
        private int _currentLength;
        private int _position;
        private int _frameIndex;

        public FramedReader(Stream archive, long sectionStart, long sectionLength)
        {
            _archive = archive;
            _sectionStart = sectionStart;

            if (sectionLength < 4) throw new InvalidDataException("Compressed section is truncated.");
            archive.Position = sectionStart + sectionLength - 4;
            Span<byte> countBytes = stackalloc byte[4];
            ReadExact(archive, countBytes);
            int frameCount = BinaryPrimitives.ReadInt32LittleEndian(countBytes);
            if (frameCount < 0 || (long)frameCount * TableEntrySize + 4 > sectionLength)
                throw new InvalidDataException("Compressed section has a corrupt frame table.");

            byte[] table = new byte[frameCount * TableEntrySize];
            archive.Position = sectionStart + sectionLength - 4 - table.Length;
            ReadExact(archive, table);

            long offset = sectionStart;
            for (int i = 0; i < frameCount; i++)
            {
                long compressed = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(i * TableEntrySize, 8));
                int raw = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(i * TableEntrySize + 8, 4));
                if (compressed < 0 || raw < 0) throw new InvalidDataException("Compressed section has a corrupt frame table.");
                _frames.Add((offset, compressed, raw));
                offset += compressed;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;

            while (_position >= _currentLength)
            {
                if (_frameIndex >= _frames.Count) return 0;
                LoadFrame(_frames[_frameIndex++]);
            }

            int take = Math.Min(buffer.Length, _currentLength - _position);
            _current.AsSpan(_position, take).CopyTo(buffer);
            _position += take;
            return take;
        }

        private void LoadFrame((long Offset, long Compressed, int Raw) frame)
        {
            byte[] compressed = new byte[frame.Compressed];
            _archive.Position = frame.Offset;
            ReadExact(_archive, compressed);

            if (_current.Length < frame.Raw) _current = new byte[frame.Raw];
            if (!BrotliDecoder.TryDecompress(compressed, _current.AsSpan(0, frame.Raw), out int decoded) || decoded != frame.Raw)
                throw new InvalidDataException("A compressed frame could not be decoded.");

            _currentLength = frame.Raw;
            _position = 0;
        }

        private static void ReadExact(Stream source, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = source.Read(buffer[total..]);
                if (read <= 0) throw new EndOfStreamException("Archive stream ended unexpectedly.");
                total += read;
            }
        }
    }
}

public sealed class PathFilter
{
    private readonly List<Rule> _rules;

    public static PathFilter Empty { get; } = new([]);

    public IReadOnlyList<string> Patterns { get; }

    private PathFilter(List<Rule> rules)
    {
        _rules = rules;
        Patterns = rules.Select(r => r.Source).ToList();
    }

    public bool IsEmpty => _rules.Count == 0;

    public static readonly IReadOnlyDictionary<string, string[]> Presets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet"] = ["bin/", "obj/", ".vs/", "artifacts/", "TestResults/", "packages/", "*.user", "*.suo", "*.userprefs"],
        ["node"] = ["node_modules/", "dist/", "build/", ".next/", ".nuxt/", ".turbo/", "coverage/"],
        ["vcs"] = [".git/", ".svn/", ".hg/"],
        ["temp"] = ["*.tmp", "*.temp", "*.log", "*.bak", "~$*", "Thumbs.db", ".DS_Store"]
    };

    public static PathFilter Build(IEnumerable<string> patterns)
    {
        var rules = new List<Rule>();
        foreach (string raw in patterns)
        {
            var rule = Compile(raw);
            if (rule != null) rules.Add(rule);
        }
        return rules.Count == 0 ? Empty : new PathFilter(rules);
    }

    public static List<string> ReadPatternFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Exclude list not found: {path}");
        var patterns = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            patterns.Add(trimmed);
        }
        return patterns;
    }

    public static List<string> ExpandPresets(string names)
    {
        var patterns = new List<string>();
        foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Presets.TryGetValue(name, out string[]? preset))
                throw new ArgumentException($"Unknown preset '{name}'. Available: {string.Join(", ", Presets.Keys)}.");
            patterns.AddRange(preset);
        }
        return patterns;
    }

    public bool IsExcluded(string relativePath, bool isDirectory)
    {
        if (_rules.Count == 0) return false;

        string path = relativePath.Replace('\\', '/').TrimStart('/');
        string name = path.Length == 0 ? path : path[(path.LastIndexOf('/') + 1)..];

        bool excluded = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory) continue;
            string candidate = rule.BasenameOnly ? name : path;
            if (rule.Matcher.IsMatch(candidate)) excluded = !rule.Negated;
        }
        return excluded;
    }

    private static Rule? Compile(string raw)
    {
        string pattern = raw.Trim().Replace('\\', '/');
        if (pattern.Length == 0 || pattern.StartsWith('#')) return null;

        bool negated = pattern.StartsWith('!');
        if (negated) pattern = pattern[1..];

        bool directoryOnly = pattern.EndsWith('/');
        if (directoryOnly) pattern = pattern[..^1];

        bool anchored = pattern.StartsWith('/');
        if (anchored) pattern = pattern[1..];

        if (pattern.Length == 0) return null;

        bool basenameOnly = !anchored && !pattern.Contains('/');
        var matcher = new Regex(Translate(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new Rule(matcher, negated, directoryOnly, basenameOnly, raw.Trim());
    }

    private static string Translate(string pattern)
    {
        var builder = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*')
            {
                bool doubled = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (doubled && i + 2 < pattern.Length && pattern[i + 2] == '/')
                {
                    builder.Append("(?:.*/)?");
                    i += 2;
                }
                else if (doubled)
                {
                    builder.Append(".*");
                    i++;
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else if (c == '/')
            {
                builder.Append('/');
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }
        builder.Append("(?:/.*)?$");
        return builder.ToString();
    }

    private sealed record Rule(Regex Matcher, bool Negated, bool DirectoryOnly, bool BasenameOnly, string Source);
}

public static class TreeDigest
{
    public static byte[] Compute(IEnumerable<(string Path, bool IsDirectory, long Length, byte[]? ContentHash)> entries)
    {
        var ordered = entries.OrderBy(e => e.Path, StringComparer.Ordinal);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var entry in ordered)
        {
            var line = new StringBuilder();
            if (entry.IsDirectory)
            {
                line.Append("D\n").Append(entry.Path).Append('\n');
            }
            else
            {
                line.Append("F\n").Append(entry.Path).Append('\n')
                    .Append(entry.Length).Append('\n')
                    .Append(entry.ContentHash == null ? string.Empty : Convert.ToHexString(entry.ContentHash))
                    .Append('\n');
            }
            digest.AppendData(Encoding.UTF8.GetBytes(line.ToString()));
        }

        return digest.GetHashAndReset();
    }

    public static string Short(byte[] digest)
        => digest.Length == 0 ? "none" : Convert.ToHexString(digest)[..16].ToLowerInvariant();
}

public static class EmbeddedKeys
{
    public const string FileName = "imgkeys.txt";

    public const string Code = "";

    public static KeySet? Resolve(string? fromCommandLine, out string source)
    {
        if (!string.IsNullOrWhiteSpace(fromCommandLine))
        {
            source = "--key-code";
            return KeyCode.Decode(fromCommandLine);
        }

        foreach (string candidate in FileCandidates())
        {
            if (!File.Exists(candidate)) continue;
            string text = File.ReadAllText(candidate);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var fromFile = KeyCode.Decode(text);
            source = fromFile.Label.Length > 0 ? $"{candidate}, {fromFile.Label}" : candidate;
            return fromFile;
        }

        if (Code.Length > 0)
        {
            var built = KeyCode.Decode(Code);
            source = built.Label.Length > 0 ? $"built into this exe, {built.Label}" : "built into this exe";
            return built;
        }

        source = string.Empty;
        return null;
    }

    private static IEnumerable<string> FileCandidates()
    {
        string? beside = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(beside)) yield return Path.Combine(beside, FileName);
        yield return Path.Combine(Directory.GetCurrentDirectory(), FileName);
    }
}

public static class FrameCipher
{
    public const int FrameSize = 1 << 20;
    public const int TagSize = 16;
    public const int NonceSize = 12;

    public static long CipherLength(long plainLength)
    {
        long frames = (plainLength + FrameSize - 1) / FrameSize;
        if (frames == 0) frames = 1;
        return plainLength + frames * TagSize;
    }

    public static void Encrypt(Stream plain, long plainLength, Stream cipher, SessionKeys keys, IncrementalHash? digest)
    {
        using var gcm = new AesGcm(keys.ContentKey, TagSize);
        byte[] buffer = new byte[FrameSize];
        byte[] output = new byte[FrameSize + TagSize];
        long frames = Math.Max(1, (plainLength + FrameSize - 1) / FrameSize);
        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> associated = stackalloc byte[12];

        for (long index = 0; index < frames; index++)
        {
            int want = (int)Math.Min(FrameSize, plainLength - index * FrameSize);
            if (want < 0) want = 0;
            ReadExact(plain, buffer.AsSpan(0, want));

            BuildNonce(keys.NonceBase, index, nonce);
            BinaryPrimitives.WriteUInt32BigEndian(associated, (uint)index);
            BinaryPrimitives.WriteInt64BigEndian(associated[4..], plainLength);

            gcm.Encrypt(nonce, buffer.AsSpan(0, want), output.AsSpan(0, want), output.AsSpan(want, TagSize), associated);
            digest?.AppendData(output, 0, want + TagSize);
            cipher.Write(output, 0, want + TagSize);
        }
    }

    public static void Decrypt(Stream cipher, long plainLength, Stream plain, SessionKeys keys)
    {
        using var gcm = new AesGcm(keys.ContentKey, TagSize);
        byte[] input = new byte[FrameSize + TagSize];
        byte[] output = new byte[FrameSize];
        long frames = Math.Max(1, (plainLength + FrameSize - 1) / FrameSize);
        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> associated = stackalloc byte[12];

        for (long index = 0; index < frames; index++)
        {
            int want = (int)Math.Min(FrameSize, plainLength - index * FrameSize);
            if (want < 0) want = 0;
            ReadExact(cipher, input.AsSpan(0, want + TagSize));

            BuildNonce(keys.NonceBase, index, nonce);
            BinaryPrimitives.WriteUInt32BigEndian(associated, (uint)index);
            BinaryPrimitives.WriteInt64BigEndian(associated[4..], plainLength);

            gcm.Decrypt(nonce, input.AsSpan(0, want), input.AsSpan(want, TagSize), output.AsSpan(0, want), associated);
            plain.Write(output, 0, want);
        }
    }

    private static void BuildNonce(ReadOnlySpan<byte> nonceBase, long index, Span<byte> nonce)
    {
        nonceBase[..SessionKeys.NonceBaseSize].CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[SessionKeys.NonceBaseSize..], (uint)index);
    }

    private static void ReadExact(Stream source, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer[total..]);
            if (read <= 0) throw new EndOfStreamException("Encrypted stream ended unexpectedly.");
            total += read;
        }
    }
}

public sealed class IndexPermutation
{
    private const int Rounds = 4;
    private readonly ulong[] _roundKeys = new ulong[Rounds];
    private readonly int _halfBits;
    private readonly ulong _halfMask;
    private readonly long _size;

    public IndexPermutation(ReadOnlySpan<byte> key, long size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        _size = size;

        int bits = 2;
        while (bits < 62 && (1L << bits) < size) bits++;
        if ((bits & 1) != 0) bits++;
        _halfBits = bits / 2;
        _halfMask = (1UL << _halfBits) - 1;

        byte[] material = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            key.ToArray(),
            Rounds * 8,
            null,
            Encoding.ASCII.GetBytes("ImageConverter/v1/permutation"));

        for (int i = 0; i < Rounds; i++)
            _roundKeys[i] = BinaryPrimitives.ReadUInt64LittleEndian(material.AsSpan(i * 8, 8));
        CryptographicOperations.ZeroMemory(material);
    }

    public long Size => _size;

    public long Map(long index)
    {
        if ((ulong)index >= (ulong)_size) throw new ArgumentOutOfRangeException(nameof(index));
        long value = index;
        for (int guard = 0; guard < 4096; guard++)
        {
            value = Round(value);
            if (value < _size) return value;
        }
        throw new InvalidOperationException("Permutation cycle walk did not converge.");
    }

    private long Round(long value)
    {
        ulong left = (ulong)value >> _halfBits;
        ulong right = (ulong)value & _halfMask;
        for (int i = 0; i < Rounds; i++)
        {
            ulong next = left ^ (Mix(right ^ _roundKeys[i]) & _halfMask);
            left = right;
            right = next;
        }
        return (long)((left << _halfBits) | right);
    }

    private static ulong Mix(ulong x)
    {
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }
}

public sealed class KeySet
{
    public byte[] RecipientPublic = [];
    public byte[] SenderPrivate = [];
    public byte[] RecipientPrivate = [];
    public byte[] SenderPublic = [];
    public string Label = string.Empty;

    public bool CanPack => RecipientPublic.Length > 0 && SenderPrivate.Length > 0;

    public bool CanUnpack => RecipientPrivate.Length > 0 && SenderPublic.Length > 0;
}

public static class KeyCode
{
    private const uint Magic = 0x314B4349;
    private const byte Version = 1;

    public static string Encode(KeySet set)
    {
        using var buffer = new MemoryStream();
        var writer = new BinaryWriter(buffer, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(set.Label);
        WriteBlob(writer, set.RecipientPublic);
        WriteBlob(writer, set.SenderPrivate);
        WriteBlob(writer, set.RecipientPrivate);
        WriteBlob(writer, set.SenderPublic);
        writer.Flush();

        byte[] body = buffer.ToArray();
        byte[] checksum = SHA256.HashData(body);
        byte[] framed = new byte[body.Length + 4];
        body.CopyTo(framed, 0);
        checksum.AsSpan(0, 4).CopyTo(framed.AsSpan(body.Length));
        return Convert.ToBase64String(framed);
    }

    public static KeySet Decode(string code)
    {
        string trimmed = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        byte[] framed;
        try
        {
            framed = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            throw new FormatException("The key code is not valid base64. Copy the whole line without breaking it.");
        }

        if (framed.Length < 12) throw new FormatException("The key code is too short to be valid.");
        byte[] body = framed[..^4];
        byte[] checksum = SHA256.HashData(body);
        if (!checksum.AsSpan(0, 4).SequenceEqual(framed.AsSpan(body.Length)))
            throw new FormatException("The key code is damaged, its checksum does not match.");

        using var buffer = new MemoryStream(body, false);
        var reader = new BinaryReader(buffer, Encoding.UTF8);
        if (reader.ReadUInt32() != Magic) throw new FormatException("That string is not an ImageConverter key code.");
        byte version = reader.ReadByte();
        if (version != Version) throw new FormatException($"Unsupported key code version {version}.");

        return new KeySet
        {
            Label = reader.ReadString(),
            RecipientPublic = ReadBlob(reader),
            SenderPrivate = ReadBlob(reader),
            RecipientPrivate = ReadBlob(reader),
            SenderPublic = ReadBlob(reader)
        };
    }

    public static (string First, string Second) CreatePair(string firstLabel, string secondLabel)
    {
        using var forward = ECDiffieHellman.Create(KeyStore.Curve);
        using var backward = ECDiffieHellman.Create(KeyStore.Curve);
        using var signerFirst = ECDsa.Create(KeyStore.Curve);
        using var signerSecond = ECDsa.Create(KeyStore.Curve);

        var first = new KeySet
        {
            Label = firstLabel,
            RecipientPublic = forward.ExportSubjectPublicKeyInfo(),
            SenderPrivate = signerFirst.ExportPkcs8PrivateKey(),
            RecipientPrivate = backward.ExportPkcs8PrivateKey(),
            SenderPublic = signerSecond.ExportSubjectPublicKeyInfo()
        };

        var second = new KeySet
        {
            Label = secondLabel,
            RecipientPublic = backward.ExportSubjectPublicKeyInfo(),
            SenderPrivate = signerSecond.ExportPkcs8PrivateKey(),
            RecipientPrivate = forward.ExportPkcs8PrivateKey(),
            SenderPublic = signerFirst.ExportSubjectPublicKeyInfo()
        };

        return (Encode(first), Encode(second));
    }

    private static void WriteBlob(BinaryWriter writer, byte[] blob)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, blob.Length);
        writer.Write(length);
        writer.Write(blob);
    }

    private static byte[] ReadBlob(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length is < 0 or > 8192) throw new FormatException("The key code holds an implausible key length.");
        return reader.ReadBytes(length);
    }
}

public static class KeyStore
{
    public const string RecipientPublicFile = "recipient-public.pem";
    public const string RecipientPrivateFile = "recipient-private.pem";
    public const string SenderPublicFile = "sender-public.pem";
    public const string SenderPrivateFile = "sender-private.pem";

    private const string RecipientPublicLabel = "IMAGECONVERTER RECIPIENT PUBLIC KEY";
    private const string RecipientPrivateLabel = "IMAGECONVERTER RECIPIENT PRIVATE KEY";
    private const string RecipientPrivateEncryptedLabel = "IMAGECONVERTER ENCRYPTED RECIPIENT PRIVATE KEY";
    private const string SenderPublicLabel = "IMAGECONVERTER SENDER PUBLIC KEY";
    private const string SenderPrivateLabel = "IMAGECONVERTER SENDER PRIVATE KEY";
    private const string SenderPrivateEncryptedLabel = "IMAGECONVERTER ENCRYPTED SENDER PRIVATE KEY";

    private const int KdfIterations = 600_000;

    public static ECCurve Curve => ECCurve.NamedCurves.nistP521;

    public static void GenerateKeyPairs(string outputDirectory, string? password)
    {
        Directory.CreateDirectory(outputDirectory);

        using var agreement = ECDiffieHellman.Create(Curve);
        using var signing = ECDsa.Create(Curve);

        WritePem(Path.Combine(outputDirectory, RecipientPublicFile), RecipientPublicLabel, agreement.ExportSubjectPublicKeyInfo());
        WritePem(Path.Combine(outputDirectory, SenderPublicFile), SenderPublicLabel, signing.ExportSubjectPublicKeyInfo());

        if (string.IsNullOrEmpty(password))
        {
            WritePem(Path.Combine(outputDirectory, RecipientPrivateFile), RecipientPrivateLabel, agreement.ExportPkcs8PrivateKey());
            WritePem(Path.Combine(outputDirectory, SenderPrivateFile), SenderPrivateLabel, signing.ExportPkcs8PrivateKey());
        }
        else
        {
            var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, KdfIterations);
            WritePem(Path.Combine(outputDirectory, RecipientPrivateFile), RecipientPrivateEncryptedLabel,
                agreement.ExportEncryptedPkcs8PrivateKey(password, pbe));
            WritePem(Path.Combine(outputDirectory, SenderPrivateFile), SenderPrivateEncryptedLabel,
                signing.ExportEncryptedPkcs8PrivateKey(password, pbe));
        }
    }

    public static ECDiffieHellman LoadRecipientPublic(string path)
    {
        var (label, data) = ReadPem(path);
        Expect(label, RecipientPublicLabel, path);
        var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(data, out _);
        return key;
    }

    public static ECDiffieHellman LoadRecipientPrivate(string path, string? password)
    {
        var (label, data) = ReadPem(path);
        var key = ECDiffieHellman.Create();
        if (label == RecipientPrivateEncryptedLabel)
        {
            RequirePassword(password, path);
            key.ImportEncryptedPkcs8PrivateKey(password, data, out _);
        }
        else
        {
            Expect(label, RecipientPrivateLabel, path);
            key.ImportPkcs8PrivateKey(data, out _);
        }
        return key;
    }

    public static ECDsa LoadSenderPublic(string path)
    {
        var (label, data) = ReadPem(path);
        Expect(label, SenderPublicLabel, path);
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(data, out _);
        return key;
    }

    public static ECDsa LoadSenderPrivate(string path, string? password)
    {
        var (label, data) = ReadPem(path);
        var key = ECDsa.Create();
        if (label == SenderPrivateEncryptedLabel)
        {
            RequirePassword(password, path);
            key.ImportEncryptedPkcs8PrivateKey(password, data, out _);
        }
        else
        {
            Expect(label, SenderPrivateLabel, path);
            key.ImportPkcs8PrivateKey(data, out _);
        }
        return key;
    }

    public static ECDiffieHellman RecipientPublicFromDer(byte[] material)
    {
        var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(material, out _);
        return key;
    }

    public static ECDiffieHellman RecipientPrivateFromDer(byte[] material)
    {
        var key = ECDiffieHellman.Create();
        key.ImportPkcs8PrivateKey(material, out _);
        return key;
    }

    public static ECDsa SenderPublicFromDer(byte[] material)
    {
        var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(material, out _);
        return key;
    }

    public static ECDsa SenderPrivateFromDer(byte[] material)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(material, out _);
        return key;
    }

    public static bool IsEncrypted(string path)
    {
        var (label, _) = ReadPem(path);
        return label == RecipientPrivateEncryptedLabel || label == SenderPrivateEncryptedLabel;
    }

    private static void WritePem(string path, string label, byte[] data)
    {
        File.WriteAllText(path, PemEncoding.WriteString(label, data) + Environment.NewLine);
        CryptographicOperations.ZeroMemory(data);
    }

    private static (string Label, byte[] Data) ReadPem(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Key file not found: {path}");
        string text = File.ReadAllText(path);
        var fields = PemEncoding.Find(text);
        string label = text[fields.Label];
        byte[] data = Convert.FromBase64String(text[fields.Base64Data]);
        return (label, data);
    }

    private static void Expect(string actual, string expected, string path)
    {
        if (actual != expected)
            throw new InvalidDataException($"{path} holds '{actual}' but '{expected}' was expected.");
    }

    private static void RequirePassword(string? password, string path)
    {
        if (string.IsNullOrEmpty(password))
            throw new CryptographicException($"{path} is password protected. Pass --key-password.");
    }
}

public sealed class SessionKeys : IDisposable
{
    public const int ContentKeySize = 32;
    public const int HeaderKeySize = 32;
    public const int PermutationKeySize = 32;
    public const int NonceBaseSize = 8;

    public byte[] ContentKey { get; }
    public byte[] HeaderKey { get; }
    public byte[] PermutationKey { get; }
    public byte[] NonceBase { get; }

    private SessionKeys(byte[] okm)
    {
        ContentKey = okm[..ContentKeySize];
        HeaderKey = okm[ContentKeySize..(ContentKeySize + HeaderKeySize)];
        PermutationKey = okm[(ContentKeySize + HeaderKeySize)..(ContentKeySize + HeaderKeySize + PermutationKeySize)];
        NonceBase = okm[(ContentKeySize + HeaderKeySize + PermutationKeySize)..];
        CryptographicOperations.ZeroMemory(okm);
    }

    public static SessionKeys Derive(byte[] sharedSecret, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> ephemeralPublicKey)
    {
        int total = ContentKeySize + HeaderKeySize + PermutationKeySize + NonceBaseSize;
        byte[] label = Encoding.ASCII.GetBytes("ImageConverter/v1/session");
        byte[] info = new byte[label.Length + ephemeralPublicKey.Length];
        label.CopyTo(info, 0);
        ephemeralPublicKey.CopyTo(info.AsSpan(label.Length));

        byte[] okm = new byte[total];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, okm, salt, info);
        CryptographicOperations.ZeroMemory(sharedSecret);
        return new SessionKeys(okm);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(ContentKey);
        CryptographicOperations.ZeroMemory(HeaderKey);
        CryptographicOperations.ZeroMemory(PermutationKey);
        CryptographicOperations.ZeroMemory(NonceBase);
    }
}

public static class HybridAgreement
{
    public const int SaltSize = 16;

    public static (byte[] EphemeralPublicKey, byte[] Salt, SessionKeys Keys) Seal(ECDiffieHellman recipientPublic)
    {
        using var ephemeral = ECDiffieHellman.Create(KeyStore.Curve);
        byte[] ephemeralSpki = ephemeral.ExportSubjectPublicKeyInfo();
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] shared = ephemeral.DeriveKeyFromHash(recipientPublic.PublicKey, HashAlgorithmName.SHA512);
        return (ephemeralSpki, salt, SessionKeys.Derive(shared, salt, ephemeralSpki));
    }

    public static SessionKeys Open(ECDiffieHellman recipientPrivate, byte[] ephemeralPublicKey, byte[] salt)
    {
        using var ephemeral = ECDiffieHellman.Create();
        ephemeral.ImportSubjectPublicKeyInfo(ephemeralPublicKey, out _);
        byte[] shared = recipientPrivate.DeriveKeyFromHash(ephemeral.PublicKey, HashAlgorithmName.SHA512);
        return SessionKeys.Derive(shared, salt, ephemeralPublicKey);
    }
}

public sealed class DeltaApplier
{
    public ArchiveSummary Expected { get; } = new();
    public ArchiveSummary Actual { get; } = new();
    public ArchiveSummary Base { get; } = new();
    public DeltaStats Stats { get; } = new();
    public Snapshot? ResultSnapshot { get; private set; }
    public List<string> FilterPatterns { get; private set; } = [];
    public byte[] ExpectedBaseDigest { get; private set; } = [];
    public byte[] ActualBaseDigest { get; private set; } = [];

    private sealed class Entry
    {
        public string Path = string.Empty;
        public DeltaEntryKind Kind;
        public long Length;
        public long ModifiedUnixSeconds;
        public bool Stored;
        public byte[] ContentHash = [];
        public List<(bool IsCopy, int Length, int FirstBlock, int BlockCount)> Ops = [];
    }

    public void Apply(Stream delta, string targetDirectory, Action<string>? log = null)
    {
        if (!delta.CanSeek) throw new ArgumentException("Delta stream must be seekable.", nameof(delta));
        if (!Directory.Exists(targetDirectory))
            throw new DirectoryNotFoundException(
                $"A delta needs the base folder to already exist at {targetDirectory}. Restore the full version first.");

        long origin = delta.Position;
        var reader = new BinaryReader(delta, Encoding.UTF8, true);
        if (reader.ReadUInt32() != DeltaWriter.Magic) throw new InvalidDataException("Not a ImageConverter delta.");
        byte version = reader.ReadByte();
        if (version != DeltaWriter.FormatVersion) throw new InvalidDataException($"Unsupported delta version {version}.");
        reader.ReadByte();
        long compressedLength = reader.ReadInt64();
        long storedLength = reader.ReadInt64();
        Expected.OriginalBytes = reader.ReadInt64();
        Expected.FileCount = reader.ReadInt32();
        Expected.DirectoryCount = reader.ReadInt32();
        Expected.TreeDigest = reader.ReadBytes(32);
        ExpectedBaseDigest = reader.ReadBytes(32);
        int blockSize = reader.ReadInt32();

        string root = Path.GetFullPath(targetDirectory);
        List<Entry> entries;
        List<string> patterns;
        List<string> deletedFiles;
        List<string> deletedDirectories;
        List<string> addedDirectories;
        int baseFileCount;
        int baseDirectoryCount;

        using var compressed = FramedBrotli.OpenReader(delta, origin + DeltaWriter.FixedHeaderSize, compressedLength);
        var body = new BinaryReader(compressed, Encoding.UTF8, true);

        patterns = ReadStrings(body);
        baseFileCount = body.ReadInt32();
        baseDirectoryCount = body.ReadInt32();
        deletedFiles = ReadStrings(body);
        deletedDirectories = ReadStrings(body);
        addedDirectories = ReadStrings(body);
        ReadStrings(body);

        int entryCount = body.ReadInt32();
        entries = new List<Entry>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            var entry = new Entry
            {
                Path = body.ReadString(),
                Kind = (DeltaEntryKind)body.ReadByte(),
                Length = body.ReadInt64(),
                ModifiedUnixSeconds = body.ReadInt64(),
                Stored = body.ReadBoolean(),
                ContentHash = body.ReadBytes(32)
            };

            if (entry.Kind == DeltaEntryKind.Patch)
            {
                int opCount = body.ReadInt32();
                for (int o = 0; o < opCount; o++)
                {
                    bool isCopy = body.ReadBoolean();
                    if (isCopy) entry.Ops.Add((true, 0, body.ReadInt32(), body.ReadInt32()));
                    else entry.Ops.Add((false, body.ReadInt32(), 0, 0));
                }
            }

            entries.Add(entry);
        }

        var filter = PathFilter.Build(patterns);
        var baseSnapshot = Snapshot.Build(root, filter, patterns, blockSize);
        ActualBaseDigest = baseSnapshot.TreeDigestValue;
        Base.OriginalBytes = baseSnapshot.TotalBytes;
        Base.FileCount = baseSnapshot.Files.Count;
        Base.DirectoryCount = baseSnapshot.Directories.Count;
        Base.TreeDigest = baseSnapshot.TreeDigestValue;

        if (!ExpectedBaseDigest.AsSpan().SequenceEqual(ActualBaseDigest.AsSpan()))
            throw new InvalidDataException(
                "The folder does not match the base this delta was built against. "
                + $"Expected {baseFileCount} files and {baseDirectoryCount} folders with digest {TreeDigest.Short(ExpectedBaseDigest)}, "
                + $"found {baseSnapshot.Files.Count} files and {baseSnapshot.Directories.Count} folders with digest {TreeDigest.Short(ActualBaseDigest)}. "
                + "Ask the sender to run imgconv pack --full for a complete copy, or to build the delta against "
                + $"{StateStore.FolderName}/{StateStore.PreviousFileName} with --base.");

        log?.Invoke($"base verified: {baseSnapshot.Files.Count} files, {Sizes.Format(baseSnapshot.TotalBytes)}, digest {TreeDigest.Short(ActualBaseDigest)}");

        foreach (string relative in addedDirectories)
            Directory.CreateDirectory(Resolve(root, relative));
        Stats.AddedDirectoryCount = addedDirectories.Count;

        var written = new Dictionary<string, (long Length, byte[] Hash)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Kind != DeltaEntryKind.Keep) continue;
            written[entry.Path] = (entry.Length, entry.ContentHash);
            Stats.KeptCount++;
        }

        foreach (var entry in entries)
        {
            if (entry.Kind == DeltaEntryKind.Keep || entry.Stored) continue;
            WriteEntry(compressed, root, entry, blockSize, written);
        }

        delta.Position = origin + DeltaWriter.FixedHeaderSize + compressedLength;
        using (var stored = new BoundedStream(delta, storedLength))
        {
            foreach (var entry in entries)
            {
                if (entry.Kind == DeltaEntryKind.Keep || !entry.Stored) continue;
                WriteEntry(stored, root, entry, blockSize, written);
            }
        }

        foreach (string relative in deletedFiles)
        {
            string path = Resolve(root, relative);
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                Stats.DeletedFileCount++;
            }
        }

        foreach (string relative in deletedDirectories)
        {
            string path = Resolve(root, relative);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
                Stats.DeletedDirectoryCount++;
            }
        }

        var resultSnapshot = Snapshot.Build(root, filter, patterns, blockSize);
        ResultSnapshot = resultSnapshot;
        FilterPatterns = patterns;

        Actual.OriginalBytes = resultSnapshot.TotalBytes;
        Actual.FileCount = resultSnapshot.Files.Count;
        Actual.DirectoryCount = resultSnapshot.Directories.Count;
        Actual.TreeDigest = resultSnapshot.TreeDigestValue;
        Stats.NewTotalBytes = resultSnapshot.TotalBytes;

        log?.Invoke($"delta applied: {Stats.KeptCount} kept, {Stats.AddedCount} added, {Stats.PatchedCount} patched, "
            + $"{Stats.DeletedFileCount} deleted, reused {Sizes.Format(Stats.CopiedBytes)}");
    }

    private void WriteEntry(Stream source, string root, Entry entry, int blockSize, Dictionary<string, (long, byte[])> written)
    {
        string destination = Resolve(root, entry.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".pvnew";

        using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                if (entry.Kind == DeltaEntryKind.Add)
                {
                    Copy(source, output, entry.Length, digest);
                    Stats.AddedCount++;
                }
                else
                {
                    using var old = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    byte[] block = new byte[blockSize];
                    foreach (var op in entry.Ops)
                    {
                        if (op.IsCopy)
                        {
                            for (int i = 0; i < op.BlockCount; i++)
                            {
                                long offset = (long)(op.FirstBlock + i) * blockSize;
                                old.Position = offset;
                                int want = (int)Math.Min(blockSize, old.Length - offset);
                                ReadExact(old, block.AsSpan(0, want));
                                output.Write(block, 0, want);
                                digest.AppendData(block, 0, want);
                                Stats.CopiedBytes += want;
                            }
                        }
                        else
                        {
                            Copy(source, output, op.Length, digest);
                        }
                    }
                    Stats.PatchedCount++;
                }
            }

            byte[] hash = digest.GetHashAndReset();
            if (!hash.AsSpan().SequenceEqual(entry.ContentHash.AsSpan()))
            {
                File.Delete(temporary);
                throw new InvalidDataException($"Rebuilt {entry.Path} does not match the hash recorded by the sender.");
            }
            written[entry.Path] = (entry.Length, hash);
        }

        if (File.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
            File.Delete(destination);
        }
        File.Move(temporary, destination);
        File.SetLastWriteTimeUtc(destination, DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedUnixSeconds).UtcDateTime);
    }

    private static void Copy(Stream source, Stream destination, long count, IncrementalHash digest)
    {
        byte[] buffer = new byte[1 << 20];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, want);
            if (read <= 0) throw new EndOfStreamException("Delta stream ended unexpectedly.");
            destination.Write(buffer, 0, read);
            digest.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }

    private static List<string> ReadStrings(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > 50_000_000) throw new InvalidDataException("Corrupt delta metadata.");
        var values = new List<string>(Math.Min(count, 1024));
        for (int i = 0; i < count; i++) values.Add(reader.ReadString());
        return values;
    }

    private static void ReadExact(Stream source, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer[total..]);
            if (read <= 0) throw new EndOfStreamException("The base file is shorter than the delta expects.");
            total += read;
        }
    }

    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Rejected unsafe delta path: {relative}");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Rejected delta path outside target: {relative}");
        return full;
    }
}

public sealed class DeltaStats
{
    public int KeptCount;
    public int AddedCount;
    public int PatchedCount;
    public int DeletedFileCount;
    public int DeletedDirectoryCount;
    public int AddedDirectoryCount;
    public long LiteralBytes;
    public long CopiedBytes;
    public long NewTotalBytes;
}

public enum DeltaEntryKind : byte
{
    Keep = 0,
    Add = 1,
    Patch = 2
}

public sealed class DeltaWriter
{
    public const uint Magic = 0x31444349;
    public const byte FormatVersion = 1;
    public const int FixedHeaderSize = 106;
    public const long MaxPatchableLength = 512L * 1024 * 1024;

    public int Quality { get; set; } = BrotliCodec.MaxQuality;
    public int Threads { get; set; }
    public int ChunkSize { get; set; }

    public ArchiveSummary Summary { get; } = new();
    public DeltaStats Stats { get; } = new();

    private sealed class Op
    {
        public bool IsCopy;
        public long Offset;
        public int Length;
        public int FirstBlock;
        public int BlockCount;
    }

    private sealed class Entry
    {
        public string Path = string.Empty;
        public string FullPath = string.Empty;
        public DeltaEntryKind Kind;
        public long Length;
        public long ModifiedUnixSeconds;
        public bool Stored;
        public byte[] ContentHash = [];
        public List<Op> Ops = [];
        public long LiteralLength;
    }

    public void Create(
        string newRootDirectory,
        Snapshot baseSnapshot,
        PathFilter filter,
        IEnumerable<string> filterPatterns,
        Stream output,
        Action<string>? log = null)
    {
        if (!output.CanSeek) throw new ArgumentException("Delta output stream must be seekable.", nameof(output));

        string root = Path.GetFullPath(newRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var files = new List<(FileInfo Info, string Rel)>();
        var directories = new List<string>();
        Walk(root, filter, files, directories);
        files.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
        directories.Sort(StringComparer.Ordinal);

        var baseFiles = baseSnapshot.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var baseDirectories = new HashSet<string>(baseSnapshot.Directories, StringComparer.Ordinal);

        var entries = new List<Entry>();
        foreach (var (info, rel) in files)
        {
            var entry = new Entry
            {
                Path = rel,
                FullPath = info.FullName,
                Length = info.Length,
                ModifiedUnixSeconds = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                Stored = ArchiveWriter.LooksAlreadyCompressed(rel)
            };
            entry.ContentHash = HashFile(info.FullName);
            Stats.NewTotalBytes += info.Length;

            if (baseFiles.TryGetValue(rel, out var previous)
                && previous.Length == info.Length
                && previous.ContentHash.AsSpan().SequenceEqual(entry.ContentHash.AsSpan()))
            {
                entry.Kind = DeltaEntryKind.Keep;
                Stats.KeptCount++;
            }
            else if (baseFiles.TryGetValue(rel, out var previous2)
                     && previous2.BlockCount > 0
                     && info.Length >= Snapshot.BlockHashThreshold
                     && info.Length <= MaxPatchableLength)
            {
                entry.Kind = DeltaEntryKind.Patch;
                BuildOps(entry, previous2, baseSnapshot.BlockSize);
                Stats.PatchedCount++;
            }
            else
            {
                entry.Kind = DeltaEntryKind.Add;
                entry.LiteralLength = info.Length;
                Stats.AddedCount++;
                Stats.LiteralBytes += info.Length;
            }

            entries.Add(entry);
        }

        var presentFiles = new HashSet<string>(files.Select(f => f.Rel), StringComparer.Ordinal);
        var presentDirectories = new HashSet<string>(directories, StringComparer.Ordinal);
        var deletedFiles = baseSnapshot.Files.Select(f => f.Path).Where(p => !presentFiles.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var deletedDirectories = baseSnapshot.Directories.Where(d => !presentDirectories.Contains(d)).OrderByDescending(d => d.Length).ToList();
        var addedDirectories = directories.Where(d => !baseDirectories.Contains(d)).ToList();

        Stats.DeletedFileCount = deletedFiles.Count;
        Stats.DeletedDirectoryCount = deletedDirectories.Count;
        Stats.AddedDirectoryCount = addedDirectories.Count;

        Summary.OriginalBytes = Stats.NewTotalBytes;
        Summary.FileCount = entries.Count;
        Summary.DirectoryCount = directories.Count;
        Summary.TreeDigest = TreeDigest.Compute(
            entries.Select(e => (e.Path, false, e.Length, (byte[]?)e.ContentHash))
                .Concat(directories.Select(d => (d, true, 0L, (byte[]?)null))));

        byte[] metadata = BuildMetadata(entries, directories, addedDirectories, deletedFiles, deletedDirectories, filterPatterns, baseSnapshot);

        long headerStart = output.Position;
        var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write((byte)0);
        writer.Write(0L);
        writer.Write(0L);
        writer.Write(Summary.OriginalBytes);
        writer.Write(Summary.FileCount);
        writer.Write(Summary.DirectoryCount);
        writer.Write(Summary.TreeDigest);
        writer.Write(baseSnapshot.TreeDigestValue);
        writer.Write(baseSnapshot.BlockSize);
        writer.Flush();

        long compressedLength;
        using (var producer = new LiteralProducer(metadata, entries.Where(e => !e.Stored).ToList()))
        {
            compressedLength = FramedBrotli.Compress(producer, output, Quality, BrotliCodec.MaxWindow, ChunkSize, Threads);
        }

        long storedStart = output.Position;
        using (var producer = new LiteralProducer(null, entries.Where(e => e.Stored).ToList()))
        {
            producer.CopyTo(output, 1 << 20);
        }
        long storedLength = output.Position - storedStart;

        long end = output.Position;
        output.Position = headerStart + 6;
        var fix = new BinaryWriter(output, Encoding.UTF8, true);
        fix.Write(compressedLength);
        fix.Write(storedLength);
        fix.Flush();
        output.Position = end;

        log?.Invoke($"delta: {Stats.KeptCount} kept, {Stats.AddedCount} added, {Stats.PatchedCount} patched, "
            + $"{Stats.DeletedFileCount} deleted, literals {Sizes.Format(Stats.LiteralBytes)}, reused {Sizes.Format(Stats.CopiedBytes)}");
    }

    public static DeltaStats Preview(string newRootDirectory, Snapshot baseSnapshot, PathFilter filter)
    {
        string root = Path.GetFullPath(newRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var files = new List<(FileInfo Info, string Rel)>();
        var directories = new List<string>();
        Walk(root, filter, files, directories);

        var baseFiles = baseSnapshot.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var baseDirectories = new HashSet<string>(baseSnapshot.Directories, StringComparer.Ordinal);
        var stats = new DeltaStats();

        foreach (var (info, rel) in files)
        {
            stats.NewTotalBytes += info.Length;
            byte[] hash = HashFile(info.FullName);

            if (baseFiles.TryGetValue(rel, out var previous)
                && previous.Length == info.Length
                && previous.ContentHash.AsSpan().SequenceEqual(hash.AsSpan()))
            {
                stats.KeptCount++;
                continue;
            }

            bool patchable = previous != null
                             && previous.BlockCount > 0
                             && info.Length >= Snapshot.BlockHashThreshold
                             && info.Length <= MaxPatchableLength;

            if (patchable) stats.PatchedCount++;
            else stats.AddedCount++;
            stats.LiteralBytes += info.Length;
        }

        var presentFiles = new HashSet<string>(files.Select(f => f.Rel), StringComparer.Ordinal);
        var presentDirectories = new HashSet<string>(directories, StringComparer.Ordinal);
        stats.DeletedFileCount = baseSnapshot.Files.Count(f => !presentFiles.Contains(f.Path));
        stats.DeletedDirectoryCount = baseSnapshot.Directories.Count(d => !presentDirectories.Contains(d));
        stats.AddedDirectoryCount = directories.Count(d => !baseDirectories.Contains(d));
        return stats;
    }

    private void BuildOps(Entry entry, SnapshotFile previous, int blockSize)
    {
        var byWeak = new Dictionary<uint, List<int>>();
        for (int i = 0; i < previous.BlockCount; i++)
        {
            if (!byWeak.TryGetValue(previous.WeakHashes[i], out var list))
            {
                list = [];
                byWeak[previous.WeakHashes[i]] = list;
            }
            list.Add(i);
        }

        byte[] data = File.ReadAllBytes(entry.FullPath);
        int position = 0;
        long literalStart = 0;
        int literalLength = 0;
        var rolling = new RollingHash();
        bool primed = false;

        while (position < data.Length)
        {
            int window = Math.Min(blockSize, data.Length - position);
            if (window < blockSize)
            {
                literalLength += data.Length - position;
                position = data.Length;
                break;
            }

            if (!primed)
            {
                rolling.Reset(data.AsSpan(position, window));
                primed = true;
            }

            int match = -1;
            if (byWeak.TryGetValue(rolling.Value, out var candidates))
            {
                Span<byte> strong = stackalloc byte[32];
                SHA256.HashData(data.AsSpan(position, window), strong);
                foreach (int candidate in candidates)
                {
                    int expectedLength = candidate == previous.BlockCount - 1
                        ? (int)(previous.Length - (long)candidate * blockSize)
                        : blockSize;
                    if (expectedLength != window) continue;
                    if (!strong[..Snapshot.StrongHashSize].SequenceEqual(
                            previous.StrongHashes.AsSpan(candidate * Snapshot.StrongHashSize, Snapshot.StrongHashSize)))
                        continue;
                    match = candidate;
                    break;
                }
            }

            if (match >= 0)
            {
                FlushLiteral(entry, literalStart, ref literalLength);
                AppendCopy(entry, match, window);
                position += window;
                literalStart = position;
                primed = false;
            }
            else
            {
                literalLength++;
                position++;
                if (position + blockSize <= data.Length)
                    rolling.Roll(data[position - 1], data[position + blockSize - 1]);
                else
                    primed = false;
            }
        }

        FlushLiteral(entry, literalStart, ref literalLength);
    }

    private void FlushLiteral(Entry entry, long start, ref int length)
    {
        if (length == 0) return;
        entry.Ops.Add(new Op { IsCopy = false, Offset = start, Length = length });
        entry.LiteralLength += length;
        Stats.LiteralBytes += length;
        length = 0;
    }

    private void AppendCopy(Entry entry, int block, int bytes)
    {
        Stats.CopiedBytes += bytes;
        var last = entry.Ops.Count > 0 ? entry.Ops[^1] : null;
        if (last is { IsCopy: true } && last.FirstBlock + last.BlockCount == block)
        {
            last.BlockCount++;
            return;
        }
        entry.Ops.Add(new Op { IsCopy = true, FirstBlock = block, BlockCount = 1 });
    }

    private static byte[] BuildMetadata(
        List<Entry> entries,
        List<string> directories,
        List<string> addedDirectories,
        List<string> deletedFiles,
        List<string> deletedDirectories,
        IEnumerable<string> filterPatterns,
        Snapshot baseSnapshot)
    {
        using var buffer = new MemoryStream();
        var writer = new BinaryWriter(buffer, Encoding.UTF8, true);

        var patterns = filterPatterns.ToList();
        writer.Write(patterns.Count);
        foreach (string pattern in patterns) writer.Write(pattern);

        writer.Write(baseSnapshot.Files.Count);
        writer.Write(baseSnapshot.Directories.Count);

        writer.Write(deletedFiles.Count);
        foreach (string path in deletedFiles) writer.Write(path);

        writer.Write(deletedDirectories.Count);
        foreach (string path in deletedDirectories) writer.Write(path);

        writer.Write(addedDirectories.Count);
        foreach (string path in addedDirectories) writer.Write(path);

        writer.Write(directories.Count);
        foreach (string path in directories) writer.Write(path);

        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            writer.Write(entry.Path);
            writer.Write((byte)entry.Kind);
            writer.Write(entry.Length);
            writer.Write(entry.ModifiedUnixSeconds);
            writer.Write(entry.Stored);
            writer.Write(entry.ContentHash);

            if (entry.Kind != DeltaEntryKind.Patch) continue;

            writer.Write(entry.Ops.Count);
            foreach (var op in entry.Ops)
            {
                writer.Write(op.IsCopy);
                if (op.IsCopy)
                {
                    writer.Write(op.FirstBlock);
                    writer.Write(op.BlockCount);
                }
                else
                {
                    writer.Write(op.Length);
                }
            }
        }

        writer.Flush();
        return buffer.ToArray();
    }

    private static void Walk(string root, PathFilter filter, List<(FileInfo Info, string Rel)> files, List<string> directories)
    {
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current).EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string relative = Path.GetRelativePath(root, child.FullName).Replace('\\', '/');
                bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                if (filter.IsExcluded(relative, isDirectory)) continue;

                if (isDirectory)
                {
                    directories.Add(relative);
                    queue.Enqueue(child.FullName);
                }
                else
                {
                    files.Add(((FileInfo)child, relative));
                }
            }
        }
    }

    private static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }

    private sealed class LiteralProducer : Stream
    {
        private readonly List<Entry> _entries;
        private MemoryStream? _metadata;
        private int _entryIndex;
        private int _opIndex;
        private FileStream? _file;
        private long _remaining;

        public LiteralProducer(byte[]? metadata, List<Entry> entries)
        {
            _metadata = metadata == null ? null : new MemoryStream(metadata, false);
            _entries = entries;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;

            if (_metadata != null)
            {
                int read = _metadata.Read(buffer);
                if (read > 0) return read;
                _metadata.Dispose();
                _metadata = null;
            }

            while (true)
            {
                if (_file != null && _remaining > 0)
                {
                    int want = (int)Math.Min(buffer.Length, _remaining);
                    int read = _file.Read(buffer[..want]);
                    if (read <= 0) throw new EndOfStreamException("A source file shrank while packing.");
                    _remaining -= read;
                    return read;
                }

                if (!Advance()) return 0;
            }
        }

        private bool Advance()
        {
            while (true)
            {
                if (_entryIndex >= _entries.Count)
                {
                    _file?.Dispose();
                    _file = null;
                    return false;
                }

                var entry = _entries[_entryIndex];

                if (entry.Kind == DeltaEntryKind.Add)
                {
                    if (_opIndex == 0)
                    {
                        _opIndex = 1;
                        if (entry.Length > 0)
                        {
                            _file = File.OpenRead(entry.FullPath);
                            _remaining = entry.Length;
                            return true;
                        }
                    }
                    _file?.Dispose();
                    _file = null;
                    _entryIndex++;
                    _opIndex = 0;
                    continue;
                }

                if (entry.Kind == DeltaEntryKind.Keep || _opIndex >= entry.Ops.Count)
                {
                    _file?.Dispose();
                    _file = null;
                    _entryIndex++;
                    _opIndex = 0;
                    continue;
                }

                var op = entry.Ops[_opIndex++];
                if (op.IsCopy) continue;

                _file ??= File.OpenRead(entry.FullPath);
                _file.Position = op.Offset;
                _remaining = op.Length;
                if (_remaining > 0) return true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _metadata?.Dispose();
                _file?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

public struct RollingHash
{
    private const uint Modulus = 65521;
    private uint _a;
    private uint _b;
    private int _length;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % Modulus;
            b = (b + a) % Modulus;
        }
        return (b << 16) | a;
    }

    public void Reset(ReadOnlySpan<byte> window)
    {
        _a = 1;
        _b = 0;
        foreach (byte value in window)
        {
            _a = (_a + value) % Modulus;
            _b = (_b + _a) % Modulus;
        }
        _length = window.Length;
    }

    public void Roll(byte leaving, byte entering)
    {
        _a = (_a + Modulus - leaving) % Modulus;
        _a = (_a + entering) % Modulus;
        uint remove = ((uint)_length * leaving) % Modulus;
        _b = (_b + Modulus - remove) % Modulus;
        _b = (_b + _a + Modulus - 1) % Modulus;
    }

    public uint Value => (_b << 16) | _a;
}

public sealed class SnapshotFile
{
    public string Path = string.Empty;
    public long Length;
    public long ModifiedUnixSeconds;
    public byte[] ContentHash = [];
    public uint[] WeakHashes = [];
    public byte[] StrongHashes = [];

    public int BlockCount => WeakHashes.Length;
}

public sealed class Snapshot
{
    public const uint Magic = 0x31534349;
    public const byte FormatVersion = 1;
    public const int DefaultBlockSize = 16 * 1024;
    public const int StrongHashSize = 16;
    public const long BlockHashThreshold = 256 * 1024;

    public int BlockSize = DefaultBlockSize;
    public List<SnapshotFile> Files = [];
    public List<string> Directories = [];
    public List<string> FilterPatterns = [];
    public byte[] TreeDigestValue = [];
    public long TotalBytes;

    public static Snapshot Build(string rootDirectory, PathFilter filter, IEnumerable<string> patterns, int blockSize = DefaultBlockSize, Action<string>? log = null)
    {
        string root = System.IO.Path.GetFullPath(rootDirectory)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

        var snapshot = new Snapshot { BlockSize = blockSize, FilterPatterns = patterns.ToList() };
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current).EnumerateFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string relative = System.IO.Path.GetRelativePath(root, child.FullName).Replace('\\', '/');
                bool isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                if (filter.IsExcluded(relative, isDirectory)) continue;

                if (isDirectory)
                {
                    snapshot.Directories.Add(relative);
                    queue.Enqueue(child.FullName);
                }
                else
                {
                    var info = (FileInfo)child;
                    snapshot.Files.Add(Describe(info, relative, blockSize));
                    snapshot.TotalBytes += info.Length;
                }
            }
        }

        snapshot.Files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        snapshot.Directories.Sort(StringComparer.Ordinal);
        snapshot.TreeDigestValue = snapshot.ComputeTreeDigest();
        log?.Invoke($"snapshot: {snapshot.Files.Count} files, {snapshot.Directories.Count} dirs, {Sizes.Format(snapshot.TotalBytes)}, digest {TreeDigest.Short(snapshot.TreeDigestValue)}");
        return snapshot;
    }

    private static SnapshotFile Describe(FileInfo info, string relative, int blockSize)
    {
        var entry = new SnapshotFile
        {
            Path = relative,
            Length = info.Length,
            ModifiedUnixSeconds = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()
        };

        using var stream = info.OpenRead();
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        bool wantBlocks = info.Length >= BlockHashThreshold;
        var weak = new List<uint>();
        var strong = new List<byte>();
        byte[] buffer = new byte[blockSize];

        while (true)
        {
            int read = ReadBlock(stream, buffer);
            if (read == 0) break;
            whole.AppendData(buffer, 0, read);
            if (wantBlocks)
            {
                weak.Add(RollingHash.Compute(buffer.AsSpan(0, read)));
                strong.AddRange(SHA256.HashData(buffer.AsSpan(0, read)).AsSpan(0, StrongHashSize));
            }
            if (read < blockSize) break;
        }

        entry.ContentHash = whole.GetHashAndReset();
        entry.WeakHashes = weak.ToArray();
        entry.StrongHashes = strong.ToArray();
        return entry;
    }

    private static int ReadBlock(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    public byte[] ComputeTreeDigest()
    {
        var entries = new List<(string Path, bool IsDirectory, long Length, byte[]? ContentHash)>();
        foreach (var file in Files) entries.Add((file.Path, false, file.Length, file.ContentHash));
        foreach (string directory in Directories) entries.Add((directory, true, 0L, null));
        return TreeDigest.Compute(entries);
    }

    public void Save(string path)
    {
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(BlockSize);
        writer.Write(TotalBytes);
        writer.Write(TreeDigestValue.Length);
        writer.Write(TreeDigestValue);

        writer.Write(FilterPatterns.Count);
        foreach (string pattern in FilterPatterns) writer.Write(pattern);

        writer.Write(Directories.Count);
        foreach (string directory in Directories) writer.Write(directory);

        writer.Write(Files.Count);
        foreach (var file in Files)
        {
            writer.Write(file.Path);
            writer.Write(file.Length);
            writer.Write(file.ModifiedUnixSeconds);
            writer.Write(file.ContentHash);
            writer.Write(file.WeakHashes.Length);
            foreach (uint weak in file.WeakHashes) writer.Write(weak);
            writer.Write(file.StrongHashes);
        }
    }

    public static Snapshot Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Snapshot not found: {path}");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new BinaryReader(input, Encoding.UTF8);

        if (reader.ReadUInt32() != Magic) throw new InvalidDataException($"{path} is not a ImageConverter snapshot.");
        byte version = reader.ReadByte();
        if (version != FormatVersion) throw new InvalidDataException($"Unsupported snapshot version {version}.");

        var snapshot = new Snapshot
        {
            BlockSize = reader.ReadInt32(),
            TotalBytes = reader.ReadInt64()
        };
        int digestLength = reader.ReadInt32();
        snapshot.TreeDigestValue = reader.ReadBytes(digestLength);

        int patternCount = reader.ReadInt32();
        for (int i = 0; i < patternCount; i++) snapshot.FilterPatterns.Add(reader.ReadString());

        int directoryCount = reader.ReadInt32();
        for (int i = 0; i < directoryCount; i++) snapshot.Directories.Add(reader.ReadString());

        int fileCount = reader.ReadInt32();
        for (int i = 0; i < fileCount; i++)
        {
            var file = new SnapshotFile
            {
                Path = reader.ReadString(),
                Length = reader.ReadInt64(),
                ModifiedUnixSeconds = reader.ReadInt64(),
                ContentHash = reader.ReadBytes(32)
            };
            int blocks = reader.ReadInt32();
            var weak = new uint[blocks];
            for (int b = 0; b < blocks; b++) weak[b] = reader.ReadUInt32();
            file.WeakHashes = weak;
            file.StrongHashes = reader.ReadBytes(blocks * StrongHashSize);
            snapshot.Files.Add(file);
        }

        return snapshot;
    }
}

public static class StateStore
{
    public const string FolderName = ".imageconverter";
    public const string FileName = "state.icv";
    public const string PreviousFileName = "state.prev.icv";
    public const string ExcludePattern = ".imageconverter/";

    public static string PathFor(string rootDirectory)
        => Path.Combine(Path.GetFullPath(rootDirectory), FolderName, FileName);

    public static string PreviousPathFor(string rootDirectory)
        => Path.Combine(Path.GetFullPath(rootDirectory), FolderName, PreviousFileName);

    public static bool Exists(string rootDirectory) => File.Exists(PathFor(rootDirectory));

    public static Snapshot? TryLoad(string rootDirectory)
    {
        string path = PathFor(rootDirectory);
        return File.Exists(path) ? Snapshot.Load(path) : null;
    }

    public static void Save(string rootDirectory, Snapshot snapshot)
    {
        string path = PathFor(rootDirectory);
        string previous = PreviousPathFor(rootDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path)) File.Copy(path, previous, true);

        string temporary = path + ".writing";
        snapshot.Save(temporary);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);

        var attributes = File.GetAttributes(Path.GetDirectoryName(path)!);
        File.SetAttributes(Path.GetDirectoryName(path)!, attributes | FileAttributes.Hidden);
    }

    public static List<string> WithStateExcluded(IEnumerable<string> patterns)
    {
        var combined = new List<string> { ExcludePattern };
        combined.AddRange(patterns);
        return combined;
    }

    public static Snapshot Capture(string rootDirectory, IEnumerable<string> patterns)
    {
        var effective = WithStateExcluded(patterns);
        return Snapshot.Build(rootDirectory, PathFilter.Build(effective), effective);
    }
}

public sealed class ImageHeader
{
    public const int Size = 640;
    public const byte CurrentVersion = 2;

    private const int TweakOffset = 0;
    private const int TweakSize = 16;
    private const int CheckOffset = 16;
    private const int CheckSize = 4;
    private const int VersionOffset = 20;
    private const int StepBitsOffset = 21;
    private const int PartIndexOffset = 22;
    private const int PartCountOffset = 23;
    private const int SaltOffset = 24;
    private const int EphemeralLengthOffset = 40;
    private const int EphemeralOffset = 42;
    private const int EphemeralCapacity = 200;
    private const int SignatureLengthOffset = 242;
    private const int SignatureOffset = 244;
    private const int SignatureCapacity = 152;
    private const int MetaNonceOffset = 396;
    private const int MetaCipherOffset = 408;
    private const int MetaPlainSize = 64;
    private const int MetaTagOffset = 472;

    private static readonly byte[] Pepper = Encoding.ASCII.GetBytes("ImageConverter/v1/pepper/9f2a4c17d83be560");
    private static readonly byte[] CheckLabel = Encoding.ASCII.GetBytes("ic-chk");

    public byte Version = CurrentVersion;
    public byte StepBits;
    public byte PartIndex;
    public byte PartCount;
    public byte[] Salt = [];
    public byte[] EphemeralPublicKey = [];
    public byte[] Signature = [];
    public long TotalCipherLength;
    public long PartOffset;
    public int PartLength;
    public long ArchiveLength;
    public long OriginalBytes;
    public int OriginalFileCount;
    public int OriginalDirectoryCount;
    public byte[] TreeDigestPrefix = new byte[DigestPrefixSize];

    public const int DigestPrefixSize = 16;

    public byte[] Serialize(SessionKeys keys, int width, int height)
    {
        if (EphemeralPublicKey.Length > EphemeralCapacity)
            throw new InvalidOperationException("Ephemeral public key does not fit the header.");
        if (Signature.Length > SignatureCapacity)
            throw new InvalidOperationException("Signature does not fit the header.");

        byte[] buffer = RandomNumberGenerator.GetBytes(Size);
        byte[] tweak = buffer.AsSpan(TweakOffset, TweakSize).ToArray();

        ComputeCheck(tweak, width, height).CopyTo(buffer.AsSpan(CheckOffset, CheckSize));
        buffer[VersionOffset] = Version;
        buffer[StepBitsOffset] = StepBits;
        buffer[PartIndexOffset] = PartIndex;
        buffer[PartCountOffset] = PartCount;
        Salt.CopyTo(buffer.AsSpan(SaltOffset, HybridAgreement.SaltSize));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(EphemeralLengthOffset), (ushort)EphemeralPublicKey.Length);
        EphemeralPublicKey.CopyTo(buffer.AsSpan(EphemeralOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(SignatureLengthOffset), (ushort)Signature.Length);
        Signature.CopyTo(buffer.AsSpan(SignatureOffset));

        Span<byte> meta = stackalloc byte[MetaPlainSize];
        meta[0] = PartIndex;
        meta[1] = PartCount;
        meta[2] = StepBits;
        meta[3] = Version;
        BinaryPrimitives.WriteInt64LittleEndian(meta[4..], TotalCipherLength);
        BinaryPrimitives.WriteInt64LittleEndian(meta[12..], PartOffset);
        BinaryPrimitives.WriteInt32LittleEndian(meta[20..], PartLength);
        BinaryPrimitives.WriteInt64LittleEndian(meta[24..], ArchiveLength);
        BinaryPrimitives.WriteInt64LittleEndian(meta[32..], OriginalBytes);
        BinaryPrimitives.WriteInt32LittleEndian(meta[40..], OriginalFileCount);
        BinaryPrimitives.WriteInt32LittleEndian(meta[44..], OriginalDirectoryCount);
        TreeDigestPrefix.AsSpan(0, DigestPrefixSize).CopyTo(meta[48..]);

        byte[] nonce = RandomNumberGenerator.GetBytes(FrameCipher.NonceSize);
        nonce.CopyTo(buffer.AsSpan(MetaNonceOffset, FrameCipher.NonceSize));

        using var gcm = new AesGcm(keys.HeaderKey, FrameCipher.TagSize);
        gcm.Encrypt(nonce, meta,
            buffer.AsSpan(MetaCipherOffset, MetaPlainSize),
            buffer.AsSpan(MetaTagOffset, FrameCipher.TagSize),
            AssociatedData(tweak, width, height));

        Mask(buffer, tweak, width, height);
        return buffer;
    }

    public static bool TryOpenEnvelope(ReadOnlySpan<byte> masked, int width, int height, out HeaderEnvelope envelope)
    {
        envelope = default!;
        if (masked.Length < Size) return false;

        byte[] buffer = masked[..Size].ToArray();
        byte[] tweak = buffer.AsSpan(TweakOffset, TweakSize).ToArray();
        Mask(buffer, tweak, width, height);

        Span<byte> expected = ComputeCheck(tweak, width, height);
        if (!CryptographicOperations.FixedTimeEquals(expected, buffer.AsSpan(CheckOffset, CheckSize)))
            return false;

        int ephemeralLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(EphemeralLengthOffset));
        int signatureLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(SignatureLengthOffset));
        if (ephemeralLength is < 1 or > EphemeralCapacity) return false;
        if (signatureLength is < 1 or > SignatureCapacity) return false;

        envelope = new HeaderEnvelope
        {
            Tweak = tweak,
            Width = width,
            Height = height,
            Version = buffer[VersionOffset],
            StepBits = buffer[StepBitsOffset],
            PartIndex = buffer[PartIndexOffset],
            PartCount = buffer[PartCountOffset],
            Salt = buffer.AsSpan(SaltOffset, HybridAgreement.SaltSize).ToArray(),
            EphemeralPublicKey = buffer.AsSpan(EphemeralOffset, ephemeralLength).ToArray(),
            Signature = buffer.AsSpan(SignatureOffset, signatureLength).ToArray(),
            MetaNonce = buffer.AsSpan(MetaNonceOffset, FrameCipher.NonceSize).ToArray(),
            MetaCipher = buffer.AsSpan(MetaCipherOffset, MetaPlainSize).ToArray(),
            MetaTag = buffer.AsSpan(MetaTagOffset, FrameCipher.TagSize).ToArray()
        };
        return true;
    }

    public static ImageHeader OpenMeta(HeaderEnvelope envelope, SessionKeys keys)
    {
        Span<byte> meta = stackalloc byte[MetaPlainSize];
        using var gcm = new AesGcm(keys.HeaderKey, FrameCipher.TagSize);
        gcm.Decrypt(envelope.MetaNonce, envelope.MetaCipher, envelope.MetaTag, meta,
            AssociatedData(envelope.Tweak, envelope.Width, envelope.Height));

        return new ImageHeader
        {
            PartIndex = meta[0],
            PartCount = meta[1],
            StepBits = meta[2],
            Version = meta[3],
            TotalCipherLength = BinaryPrimitives.ReadInt64LittleEndian(meta[4..]),
            PartOffset = BinaryPrimitives.ReadInt64LittleEndian(meta[12..]),
            PartLength = BinaryPrimitives.ReadInt32LittleEndian(meta[20..]),
            ArchiveLength = BinaryPrimitives.ReadInt64LittleEndian(meta[24..]),
            OriginalBytes = BinaryPrimitives.ReadInt64LittleEndian(meta[32..]),
            OriginalFileCount = BinaryPrimitives.ReadInt32LittleEndian(meta[40..]),
            OriginalDirectoryCount = BinaryPrimitives.ReadInt32LittleEndian(meta[44..]),
            TreeDigestPrefix = meta[48..(48 + DigestPrefixSize)].ToArray(),
            Salt = envelope.Salt,
            EphemeralPublicKey = envelope.EphemeralPublicKey,
            Signature = envelope.Signature
        };
    }

    private static byte[] AssociatedData(ReadOnlySpan<byte> tweak, int width, int height)
    {
        byte[] data = new byte[TweakSize + 8];
        tweak.CopyTo(data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(TweakSize), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(TweakSize + 4), height);
        return data;
    }

    private static byte[] ComputeCheck(ReadOnlySpan<byte> tweak, int width, int height)
    {
        Span<byte> input = stackalloc byte[Pepper.Length + TweakSize + 8 + 6];
        int position = 0;
        Pepper.CopyTo(input[position..]);
        position += Pepper.Length;
        tweak.CopyTo(input[position..]);
        position += TweakSize;
        BinaryPrimitives.WriteInt32BigEndian(input[position..], width);
        position += 4;
        BinaryPrimitives.WriteInt32BigEndian(input[position..], height);
        position += 4;
        CheckLabel.CopyTo(input[position..]);

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return digest[..CheckSize].ToArray();
    }

    private static void Mask(Span<byte> buffer, ReadOnlySpan<byte> tweak, int width, int height)
    {
        Span<byte> input = stackalloc byte[Pepper.Length + TweakSize + 12];
        int prefix = 0;
        Pepper.CopyTo(input[prefix..]);
        prefix += Pepper.Length;
        tweak.CopyTo(input[prefix..]);
        prefix += TweakSize;
        BinaryPrimitives.WriteInt32BigEndian(input[prefix..], width);
        BinaryPrimitives.WriteInt32BigEndian(input[(prefix + 4)..], height);
        int counterOffset = prefix + 8;

        Span<byte> block = stackalloc byte[32];
        int position = CheckOffset;
        uint counter = 0;
        while (position < buffer.Length)
        {
            BinaryPrimitives.WriteUInt32BigEndian(input[counterOffset..], counter++);
            SHA256.HashData(input, block);
            int take = Math.Min(block.Length, buffer.Length - position);
            for (int i = 0; i < take; i++) buffer[position + i] ^= block[i];
            position += take;
        }
    }
}

public sealed class HeaderEnvelope
{
    public byte[] Tweak = [];
    public int Width;
    public int Height;
    public byte Version;
    public byte StepBits;
    public byte PartIndex;
    public byte PartCount;
    public byte[] Salt = [];
    public byte[] EphemeralPublicKey = [];
    public byte[] Signature = [];
    public byte[] MetaNonce = [];
    public byte[] MetaCipher = [];
    public byte[] MetaTag = [];
}

public static class ImageProbe
{
    private static readonly int[] StepBitCandidates = [4, 9, 8, 10, 11, 12, 6, 5, 3, 2, 7, 1];
    private const int HeaderProbeRows = 8;

    public static HeaderEnvelope Read(string file)
    {
        var envelope = TryRead(file, HeaderProbeRows) ?? TryRead(file, int.MaxValue);
        return envelope ?? throw new InvalidDataException(
            $"{Path.GetFileName(file)} is not an ImageConverter image, or its pixels were altered.");
    }

    public static bool IsOwnImage(string file)
    {
        try
        {
            return TryRead(file, HeaderProbeRows) != null || TryRead(file, int.MaxValue) != null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static HeaderEnvelope? TryRead(string file, int maxRows)
    {
        PngImage image;
        try
        {
            image = PngReader.Read(file, maxRows);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            if (maxRows == int.MaxValue)
                throw new InvalidDataException($"{Path.GetFileName(file)} is damaged and cannot be decoded: {ex.Message}");
            return null;
        }

        foreach (int candidate in StepBitCandidates)
        {
            if (PixelCodec.Capacity(image.Width, image.Height, candidate) < ImageHeader.Size) continue;
            byte[] probe = PixelCodec.Extract(image, candidate, ImageHeader.Size);
            if (!ImageHeader.TryOpenEnvelope(probe, image.Width, image.FullHeight, out var envelope)) continue;
            if (envelope.StepBits != candidate) continue;
            return envelope;
        }
        return null;
    }
}

public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint value = crc;
        foreach (byte b in data)
            value = Table[(value ^ b) & 0xFF] ^ (value >> 8);
        return value;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
}

public static class PixelCodec
{
    public const int MinStepBits = 1;
    public const int MaxStepBits = 8;
    public const int CoverBase = 8;
    public const int MaxCoverBits = 4;
    public const int MaxMode = CoverBase + MaxCoverBits;

    private static readonly double[] Aspects = [1.0, 4.0 / 3.0, 3.0 / 2.0, 16.0 / 9.0, 5.0 / 4.0];

    public static bool IsCover(int mode) => mode > CoverBase;

    public static int BitsOf(int mode) => mode > CoverBase ? mode - CoverBase : mode;

    public static int CoverMode(int coverBits) => CoverBase + coverBits;

    public static long Capacity(int width, int height, int mode)
        => (long)width * height * 3 * BitsOf(mode) / 8;

    public static (int Width, int Height) ChooseGeometry(long byteCount, int stepBits, int aspectIndex)
    {
        Validate(stepBits);
        RejectCover(stepBits);
        if (byteCount <= 0) throw new ArgumentOutOfRangeException(nameof(byteCount));

        long channels = (byteCount * 8 + stepBits - 1) / stepBits;
        long pixels = (channels + 2) / 3;

        double aspect = Aspects[((aspectIndex % Aspects.Length) + Aspects.Length) % Aspects.Length];
        long width = (long)Math.Round(Math.Sqrt(pixels * aspect));
        if (width < 2) width = 2;
        if ((width & 1) != 0) width++;
        if (width > int.MaxValue / 4) throw new InvalidOperationException("Part is too large for a single image.");

        long height = (pixels + width - 1) / width;
        if (height < 1) height = 1;
        while (Capacity((int)width, (int)height, stepBits) < byteCount) height++;
        return ((int)width, (int)height);
    }

    public static void Write(string path, byte[] data, int stepBits, int width, int height)
    {
        Validate(stepBits);
        RejectCover(stepBits);
        long capacity = Capacity(width, height, stepBits);
        if (data.Length != capacity)
            throw new ArgumentException($"Payload must be exactly {capacity} bytes for {width}x{height}.", nameof(data));

        byte filterType = stepBits == MaxStepBits ? (byte)0 : (byte)4;
        var options = stepBits == MaxStepBits
            ? new ZLibCompressionOptions { CompressionLevel = 0, CompressionStrategy = ZLibCompressionStrategy.Default }
            : new ZLibCompressionOptions { CompressionLevel = 9, CompressionStrategy = ZLibCompressionStrategy.HuffmanOnly };
        int half = 1 << (stepBits - 1);
        int stride = width * 3;

        var reader = new BitStreamReader(data);
        byte[] filtered = new byte[stride];
        byte[] current = new byte[stride];
        byte[] previous = new byte[stride];

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var png = new PngWriter(output, width, height, options);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < stride; x++)
            {
                int value = (int)reader.Read(stepBits);
                if (stepBits == MaxStepBits)
                {
                    filtered[x] = (byte)value;
                    continue;
                }

                int left = x >= 3 ? current[x - 3] : 0;
                int up = y > 0 ? previous[x] : 0;
                int upLeft = x >= 3 && y > 0 ? previous[x - 3] : 0;
                int predicted = PngReader.Paeth(left, up, upLeft);
                int raw = ValueToPixel(predicted, value, stepBits);
                current[x] = (byte)raw;
                filtered[x] = (byte)((raw - predicted) & 0xFF);
            }
            png.WriteRow(filterType, filtered);
            if (stepBits != MaxStepBits) (previous, current) = (current, previous);
        }
        png.Finish();
    }

    public static void WriteCover(string path, byte[] data, int mode, PngImage cover)
    {
        Validate(mode);
        if (!IsCover(mode)) throw new ArgumentException("That mode does not use a cover image.", nameof(mode));

        int bits = BitsOf(mode);
        int width = cover.Width;
        int height = cover.Height;
        long capacity = Capacity(width, height, mode);
        if (data.Length > capacity)
            throw new ArgumentException($"A {width}x{height} cover holds {capacity} bytes, not {data.Length}.", nameof(data));

        int stride = width * 3;
        if (cover.Pixels.LongLength < (long)stride * height)
            throw new ArgumentException("The cover image was not read in full.", nameof(cover));

        long channelsUsed = ((long)data.Length * 8 + bits - 1) / bits;
        int mask = (1 << bits) - 1;
        var reader = new BitStreamReader(data);
        byte[] raw = new byte[stride];
        byte[] previous = new byte[stride];
        byte[] filtered = new byte[stride];

        var options = new ZLibCompressionOptions
        {
            CompressionLevel = 9,
            CompressionStrategy = ZLibCompressionStrategy.Default
        };

        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var png = new PngWriter(output, width, height, options);
        for (int y = 0; y < height; y++)
        {
            long rowStart = (long)y * stride;
            for (int x = 0; x < stride; x++)
            {
                byte original = cover.Pixels[rowStart + x];
                if (rowStart + x >= channelsUsed)
                {
                    raw[x] = original;
                    continue;
                }

                int value = (int)reader.Read(bits) & mask;
                raw[x] = (byte)((original & ~mask) | value);
            }

            for (int x = 0; x < stride; x++)
            {
                int left = x >= 3 ? raw[x - 3] : 0;
                int up = y > 0 ? previous[x] : 0;
                int upLeft = x >= 3 && y > 0 ? previous[x - 3] : 0;
                filtered[x] = (byte)((raw[x] - PngReader.Paeth(left, up, upLeft)) & 0xFF);
            }

            png.WriteRow(4, filtered);
            (previous, raw) = (raw, previous);
        }
        png.Finish();
    }

    internal static int ValueToPixel(int predicted, int value, int stepBits)
    {
        if (value == 0) return predicted;

        int room = Math.Min(predicted, 255 - predicted);
        bool upFirst = (predicted & 1) == 0;

        if (value <= 2 * room)
        {
            int magnitude = (value + 1) / 2;
            bool up = (value & 1) == 1 ? upFirst : !upFirst;
            return up ? predicted + magnitude : predicted - magnitude;
        }

        int overflow = room + (value - 2 * room);
        return 255 - predicted > predicted ? predicted + overflow : predicted - overflow;
    }

    internal static int PixelToValue(int predicted, int pixel, int stepBits)
    {
        int delta = pixel - predicted;
        if (delta == 0) return 0;

        int room = Math.Min(predicted, 255 - predicted);
        bool upFirst = (predicted & 1) == 0;
        int magnitude = Math.Abs(delta);
        bool up = delta > 0;

        if (magnitude <= room)
            return up == upFirst ? 2 * magnitude - 1 : 2 * magnitude;

        return 2 * room + (magnitude - room);
    }

    public static byte[] Extract(PngImage image, int stepBits, long count)
    {
        Validate(stepBits);
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > Capacity(image.Width, image.Height, stepBits))
            throw new ArgumentOutOfRangeException(nameof(count), "Image cannot hold that many bytes.");

        byte[] output = new byte[count];
        var writer = new BitStreamWriter(output);
        int stride = image.Stride;

        if (IsCover(stepBits))
        {
            int coverBits = BitsOf(stepBits);
            int coverMask = (1 << coverBits) - 1;
            for (int y = 0; y < image.Height; y++)
            {
                long rowStart = (long)y * stride;
                for (int x = 0; x < stride; x++)
                {
                    uint value = (uint)(image.Pixels[rowStart + x] & coverMask);
                    if (!writer.Write(value, coverBits)) return output;
                }
            }
            return output;
        }

        int mask = (1 << stepBits) - 1;

        for (int y = 0; y < image.Height; y++)
        {
            long rowStart = (long)y * stride;
            long previousStart = rowStart - stride;
            for (int x = 0; x < stride; x++)
            {
                byte raw = image.Pixels[rowStart + x];
                int value;
                if (stepBits == MaxStepBits)
                {
                    value = raw;
                }
                else
                {
                    int left = x >= 3 ? image.Pixels[rowStart + x - 3] : 0;
                    int up = y > 0 ? image.Pixels[previousStart + x] : 0;
                    int upLeft = x >= 3 && y > 0 ? image.Pixels[previousStart + x - 3] : 0;
                    int predicted = PngReader.Paeth(left, up, upLeft);
                    value = PixelToValue(predicted, raw, stepBits) & mask;
                }

                if (!writer.Write((uint)value, stepBits)) return output;
            }
        }
        return output;
    }

    private static void Validate(int mode)
    {
        if (mode is < MinStepBits or > MaxMode)
            throw new ArgumentOutOfRangeException(nameof(mode), $"Pixel mode must be {MinStepBits}..{MaxMode}.");
    }

    private static void RejectCover(int mode)
    {
        if (IsCover(mode))
            throw new ArgumentException("That mode needs a cover image.", nameof(mode));
    }

    private struct BitStreamReader
    {
        private readonly byte[] _data;
        private readonly byte _pad;
        private long _position;

        public BitStreamReader(byte[] data)
        {
            _data = data;
            _pad = RandomNumberGenerator.GetBytes(1)[0];
            _position = 0;
        }

        public uint Read(int count)
        {
            uint result = 0;
            int filled = 0;
            while (filled < count)
            {
                long index = _position >> 3;
                int offset = (int)(_position & 7);
                int take = Math.Min(8 - offset, count - filled);
                byte source = index < _data.Length ? _data[index] : _pad;
                uint chunk = (uint)((source >> offset) & ((1 << take) - 1));
                result |= chunk << filled;
                filled += take;
                _position += take;
            }
            return result;
        }
    }

    private struct BitStreamWriter
    {
        private readonly byte[] _data;
        private long _position;

        public BitStreamWriter(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        public bool Write(uint value, int count)
        {
            int written = 0;
            while (written < count)
            {
                long index = _position >> 3;
                if (index >= _data.Length) return false;
                int offset = (int)(_position & 7);
                int take = Math.Min(8 - offset, count - written);
                uint chunk = (value >> written) & (uint)((1 << take) - 1);
                _data[index] |= (byte)(chunk << offset);
                written += take;
                _position += take;
            }
            return _position < (long)_data.Length * 8;
        }
    }
}

public sealed class PngImage
{
    public int Width;
    public int Height;
    public int FullHeight;
    public byte[] Pixels = [];
    public int Stride => Width * 3;
}

public static class PngReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static PngImage Read(string path, int maxRows = int.MaxValue)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Read(file, maxRows);
    }

    public static PngImage Read(Stream input, int maxRows = int.MaxValue)
    {
        Span<byte> signature = stackalloc byte[8];
        ReadExact(input, signature);
        if (!signature.SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, channels = 3;
        bool headerSeen = false;
        using var compressed = new MemoryStream();
        Span<byte> lengthBytes = stackalloc byte[4];
        Span<byte> tag = stackalloc byte[4];

        while (true)
        {
            int read = input.Read(lengthBytes);
            if (read == 0) break;
            if (read != 4) throw new InvalidDataException("Truncated PNG chunk length.");
            int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length < 0) throw new InvalidDataException("Invalid PNG chunk length.");

            ReadExact(input, tag);
            string type = Encoding.ASCII.GetString(tag);

            if (type == "IHDR")
            {
                byte[] ihdr = new byte[length];
                ReadExact(input, ihdr);
                width = BinaryPrimitives.ReadInt32BigEndian(ihdr);
                height = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4));
                if (ihdr[9] != 2 && ihdr[9] != 6)
                    throw new NotSupportedException(
                        "This PNG is no longer true colour, it was turned into a palette or greyscale image somewhere on the way. "
                        + "The data in the pixels is already gone. Send the images again without letting anything convert them.");
                if (ihdr[8] != 8)
                    throw new NotSupportedException(
                        $"This PNG keeps {ihdr[8]} bits per channel instead of 8, so it was re-encoded on the way. "
                        + "Send the images again without letting anything convert them.");
                if (ihdr[12] != 0)
                    throw new NotSupportedException(
                        "This PNG is interlaced, so it was re-encoded on the way. "
                        + "Send the images again without letting anything convert them.");
                channels = ihdr[9] == 6 ? 4 : 3;
                headerSeen = true;
            }
            else if (type == "IDAT")
            {
                byte[] payload = new byte[length];
                ReadExact(input, payload);
                compressed.Write(payload);
            }
            else if (type == "IEND")
            {
                Skip(input, length);
                Skip(input, 4);
                break;
            }
            else
            {
                Skip(input, length);
            }

            Skip(input, 4);
        }

        if (!headerSeen) throw new InvalidDataException("PNG is missing IHDR.");

        int rows = (int)Math.Min(height, (long)maxRows);
        var image = new PngImage { Width = width, Height = rows, FullHeight = height, Pixels = new byte[(long)width * rows * 3] };
        compressed.Position = 0;
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);

        int stride = width * 3;
        int rowStride = width * channels;
        byte[] current = new byte[rowStride];
        byte[] previous = new byte[rowStride];
        Span<byte> filterByte = stackalloc byte[1];

        for (int y = 0; y < rows; y++)
        {
            ReadExact(inflate, filterByte);
            ReadExact(inflate, current);
            Unfilter(filterByte[0], current, previous, channels);
            if (channels == 3)
            {
                current.CopyTo(image.Pixels, (long)y * stride);
            }
            else
            {
                long target = (long)y * stride;
                for (int x = 0; x < width; x++)
                {
                    int source = x * channels;
                    image.Pixels[target] = current[source];
                    image.Pixels[target + 1] = current[source + 1];
                    image.Pixels[target + 2] = current[source + 2];
                    target += 3;
                }
            }
            (previous, current) = (current, previous);
        }

        return image;
    }

    private static void Unfilter(byte filterType, Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        switch (filterType)
        {
            case 0:
                break;
            case 1:
                for (int i = bpp; i < row.Length; i++)
                    row[i] = (byte)(row[i] + row[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < row.Length; i++)
                    row[i] = (byte)(row[i] + previous[i]);
                break;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                }
                break;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    int upLeft = i >= bpp ? previous[i - bpp] : 0;
                    row[i] = (byte)(row[i] + Paeth(left, previous[i], upLeft));
                }
                break;
            default:
                throw new InvalidDataException($"Unknown PNG filter type {filterType}.");
        }
    }

    public static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void ReadExact(Stream source, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = source.Read(buffer[total..]);
            if (read <= 0) throw new EndOfStreamException("PNG stream ended unexpectedly.");
            total += read;
        }
    }

    private static void Skip(Stream source, int count)
    {
        if (count == 0) return;
        byte[] scratch = new byte[Math.Min(count, 8192)];
        int remaining = count;
        while (remaining > 0)
        {
            int read = source.Read(scratch, 0, Math.Min(scratch.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("PNG stream ended unexpectedly.");
            remaining -= read;
        }
    }
}

public sealed class PngWriter : IDisposable
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Stream _output;
    private readonly IdatSink _sink;
    private readonly ZLibStream _deflate;
    private readonly int _width;
    private readonly int _height;
    private int _rowsWritten;

    public PngWriter(Stream output, int width, int height, ZLibCompressionOptions options)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _output = output;
        _width = width;
        _height = height;

        _output.Write(Signature);
        WriteHeaderChunk();

        _sink = new IdatSink(output);
        _deflate = new ZLibStream(_sink, options);
    }

    public void WriteRow(byte filterType, ReadOnlySpan<byte> filtered)
    {
        if (filtered.Length != _width * 3)
            throw new ArgumentException($"Row must hold {_width * 3} bytes.", nameof(filtered));
        if (_rowsWritten >= _height)
            throw new InvalidOperationException("All rows already written.");

        Span<byte> prefix = [filterType];
        _deflate.Write(prefix);
        _deflate.Write(filtered);
        _rowsWritten++;
    }

    public void Finish()
    {
        if (_rowsWritten != _height)
            throw new InvalidOperationException($"Expected {_height} rows, got {_rowsWritten}.");
        _deflate.Dispose();
        WriteChunk("IEND", ReadOnlySpan<byte>.Empty);
        _output.Flush();
    }

    public void Dispose() => _deflate.Dispose();

    private void WriteHeaderChunk()
    {
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, _width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], _height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk("IHDR", ihdr);
    }

    private void WriteChunk(string type, ReadOnlySpan<byte> data)
        => WriteChunk(_output, type, data);

    internal static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        Span<byte> tag = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, tag);
        output.Write(tag);
        output.Write(data);

        uint crc = Crc32.Update(0xFFFFFFFFu, tag);
        crc = Crc32.Update(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private sealed class IdatSink : Stream
    {
        private const int ChunkLimit = 512 * 1024;
        private readonly Stream _output;
        private readonly byte[] _buffer = new byte[ChunkLimit];
        private int _count;

        public IdatSink(Stream output) => _output = output;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                int take = Math.Min(buffer.Length, ChunkLimit - _count);
                buffer[..take].CopyTo(_buffer.AsSpan(_count));
                _count += take;
                buffer = buffer[take..];
                if (_count == ChunkLimit) Emit();
            }
        }

        private void Emit()
        {
            WriteChunk(_output, "IDAT", _buffer.AsSpan(0, _count));
            _count = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _count > 0) Emit();
            base.Dispose(disposing);
        }
    }
}

public sealed class PackOptions
{
    public string InputDirectory = string.Empty;
    public string OutputDirectory = string.Empty;
    public string RecipientPublicKeyPath = string.Empty;
    public string SenderPrivateKeyPath = string.Empty;
    public byte[]? RecipientPublicMaterial;
    public byte[]? SenderPrivateMaterial;
    public string? KeyPassword;
    public int Parts;
    public int StepBits = 4;
    public string NamePrefix = "IMG";
    public bool KeepArchive;
    public List<string> ExcludePatterns = [];
    public int CompressionLevel = 11;
    public int Threads;
    public int ChunkMiB;
    public string? BaseSnapshotPath;
    public string? SnapshotOutPath;
    public bool UseDiff;
    public string? AgainstDirectory;
    public bool RememberVersion;
    public List<string> Covers = [];
    public int CoverBits = 1;
}

public sealed class PackedImage
{
    public string Path = string.Empty;
    public int Width;
    public int Height;
    public long FileLength;
    public long PayloadLength;
}

public sealed class PackResult
{
    public long SourceBytes;
    public long ArchiveBytes;
    public long CipherBytes;
    public long ImageBytes;
    public ArchiveStats ArchiveStats = new();
    public ArchiveSummary Summary = new();
    public DeltaStats? DeltaStats;
    public bool IsDelta;
    public string? BaseSnapshotUsed;
    public bool StateSaved;
    public byte[] StateDigest = [];
    public int ReplacedImages;
    public List<PackedImage> Images = [];
    public List<ArchiveEntry> Included = [];
    public TimeSpan Elapsed;
}

public static class Packer
{
    public static PackResult Preview(PackOptions options, Action<string>? log = null)
    {
        if (!Directory.Exists(options.InputDirectory))
            throw new DirectoryNotFoundException($"Input folder not found: {options.InputDirectory}");

        var stopwatch = Stopwatch.StartNew();
        var writer = new ArchiveWriter
        {
            Filter = PathFilter.Build(StateStore.WithStateExcluded(options.ExcludePatterns)),
            MeasureSkipped = true
        };
        var entries = writer.Preview(options.InputDirectory, log);

        var result = new PackResult
        {
            ArchiveStats = writer.Stats,
            Summary = writer.Summary,
            SourceBytes = writer.Stats.RawBytes,
            Included = entries.Where(e => e.HasBody || e.IsReference).ToList(),
            Elapsed = stopwatch.Elapsed
        };

        var (baseSnapshot, baseDescription) = ResolveBase(options, log);
        if (baseSnapshot != null)
        {
            var patterns = options.ExcludePatterns.Count > 0
                ? StateStore.WithStateExcluded(options.ExcludePatterns)
                : baseSnapshot.FilterPatterns;
            result.BaseSnapshotUsed = baseDescription;
            result.IsDelta = true;
            result.DeltaStats = DeltaWriter.Preview(options.InputDirectory, baseSnapshot, PathFilter.Build(patterns));
        }

        result.Elapsed = stopwatch.Elapsed;
        return result;
    }

    public static (Snapshot? Base, string Description) ResolveBase(PackOptions options, Action<string>? log = null)
    {
        var patterns = StateStore.WithStateExcluded(options.ExcludePatterns);

        if (options.AgainstDirectory != null)
        {
            string reference = options.AgainstDirectory;
            if (!Directory.Exists(reference))
                throw new DirectoryNotFoundException($"The folder to compare against was not found: {reference}");

            log?.Invoke($"reading the folder to compare against: {Path.GetFullPath(reference)}");
            var built = Snapshot.Build(reference, PathFilter.Build(patterns), patterns, Snapshot.DefaultBlockSize, log);
            return (built, Path.GetFullPath(reference));
        }

        if (options.BaseSnapshotPath != null)
        {
            if (!File.Exists(options.BaseSnapshotPath))
                throw new FileNotFoundException($"Base snapshot not found: {options.BaseSnapshotPath}");
            return (Snapshot.Load(options.BaseSnapshotPath), Path.GetFullPath(options.BaseSnapshotPath));
        }

        if (!options.UseDiff) return (null, string.Empty);

        string state = StateStore.PathFor(options.InputDirectory);
        if (File.Exists(state)) return (Snapshot.Load(state), state);

        throw new InvalidOperationException(
            "--diff compares against a version remembered inside the folder, and there is none. "
            + "Say what to compare with instead: --against \"<folder that holds the old version>\", "
            + "or --base \"<file.icv>\". Without any of these the whole folder is sent.");
    }

    public static Snapshot WriteSnapshot(PackOptions options, Action<string>? log = null)
    {
        if (!Directory.Exists(options.InputDirectory))
            throw new DirectoryNotFoundException($"Input folder not found: {options.InputDirectory}");

        var effective = StateStore.WithStateExcluded(options.ExcludePatterns);
        var snapshot = Snapshot.Build(
            options.InputDirectory,
            PathFilter.Build(effective),
            effective,
            Snapshot.DefaultBlockSize,
            log);

        string target = options.SnapshotOutPath ?? "base.icv";
        string? directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        snapshot.Save(target);
        return snapshot;
    }

    public static PackResult Run(PackOptions options, Action<string>? log = null)
    {
        if (!Directory.Exists(options.InputDirectory))
            throw new DirectoryNotFoundException($"Input folder not found: {options.InputDirectory}");
        if (options.Parts is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(options.Parts), "Image count must be 1..255.");

        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(options.OutputDirectory);
        using var scratch = new Scratch();
        var result = new PackResult();

        {
            var (snapshot, baseDescription) = ResolveBase(options, log);
            if (snapshot != null)
            {
                var patterns = options.ExcludePatterns.Count > 0
                    ? StateStore.WithStateExcluded(options.ExcludePatterns)
                    : snapshot.FilterPatterns;
                result.BaseSnapshotUsed = baseDescription;
                var deltaWriter = new DeltaWriter
                {
                    Quality = options.CompressionLevel,
                    Threads = options.Threads,
                    ChunkSize = options.ChunkMiB > 0 ? options.ChunkMiB * 1024 * 1024 : 0
                };
                var deltaStream = scratch.Create("archive");
                try
                {
                    deltaWriter.Create(options.InputDirectory, snapshot, PathFilter.Build(patterns), patterns, deltaStream, log);
                }
                finally
                {
                    scratch.Done(deltaStream);
                }
                result.IsDelta = true;
                result.DeltaStats = deltaWriter.Stats;
                result.Summary = deltaWriter.Summary;
                result.SourceBytes = deltaWriter.Stats.NewTotalBytes;
                result.ArchiveBytes = scratch.Length("archive");
                log?.Invoke($"delta payload: {Sizes.Format(result.ArchiveBytes)} for {Sizes.Format(result.SourceBytes)} of new version");
            }
            else
            {
                var writer = new ArchiveWriter
                {
                    Filter = PathFilter.Build(StateStore.WithStateExcluded(options.ExcludePatterns)),
                    Quality = options.CompressionLevel,
                    Threads = options.Threads,
                    ChunkSize = options.ChunkMiB > 0 ? options.ChunkMiB * 1024 * 1024 : 0
                };
                var archiveStream = scratch.Create("archive");
                try
                {
                    writer.Create(options.InputDirectory, archiveStream, log);
                }
                finally
                {
                    scratch.Done(archiveStream);
                }
                result.ArchiveStats = writer.Stats;
                result.Summary = writer.Summary;
                result.SourceBytes = writer.Stats.RawBytes;
                result.ArchiveBytes = scratch.Length("archive");
                log?.Invoke($"archive: {Sizes.Format(result.SourceBytes)} raw, {Sizes.Format(result.ArchiveBytes)} compressed");
            }

            using var recipientPublic = options.RecipientPublicMaterial != null
                ? KeyStore.RecipientPublicFromDer(options.RecipientPublicMaterial)
                : KeyStore.LoadRecipientPublic(options.RecipientPublicKeyPath);
            using var senderPrivate = options.SenderPrivateMaterial != null
                ? KeyStore.SenderPrivateFromDer(options.SenderPrivateMaterial)
                : KeyStore.LoadSenderPrivate(options.SenderPrivateKeyPath, options.KeyPassword);

            Scratch.Guard(result.ArchiveBytes);

            if (options.KeepArchive)
            {
                string kept = Path.Combine(options.OutputDirectory, "archive.pva");
                scratch.SaveCopy("archive", kept);
                log?.Invoke($"kept intermediate archive at {kept}");
            }

            var (ephemeralPublicKey, salt, keys) = HybridAgreement.Seal(recipientPublic);
            byte[] cipherDigest;
            using (keys)
            {
                var plain = scratch.Open("archive");
                var cipher = scratch.Create("cipher");
                try
                {
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    FrameCipher.Encrypt(plain, result.ArchiveBytes, cipher, keys, digest);
                    cipherDigest = digest.GetHashAndReset();
                }
                finally
                {
                    scratch.Done(plain);
                    scratch.Done(cipher);
                }

                result.CipherBytes = scratch.Length("cipher");
                scratch.Release("archive");
                byte[] signature = senderPrivate.SignHash(cipherDigest);
                log?.Invoke($"encrypted: {Sizes.Format(result.CipherBytes)}, signed with {senderPrivate.KeySize}-bit key");

                WriteImages(options, result, scratch.Buffer("cipher"), ephemeralPublicKey, salt, signature, keys, log);
            }

            if (options.RememberVersion)
            {
                var state = StateStore.Capture(options.InputDirectory, options.ExcludePatterns);
                StateStore.Save(options.InputDirectory, state);
                result.StateSaved = true;
                result.StateDigest = state.TreeDigestValue;
                log?.Invoke($"state saved: {TreeDigest.Short(state.TreeDigestValue)} in {StateStore.FolderName}, next run sends only the changes");
            }
        }

        result.Elapsed = stopwatch.Elapsed;
        return result;
    }

    private static void WriteImages(
        PackOptions options,
        PackResult result,
        byte[] cipher,
        byte[] ephemeralPublicKey,
        byte[] salt,
        byte[] signature,
        SessionKeys keys,
        Action<string>? log)
    {
        long total = result.CipherBytes;
        bool useCovers = options.Covers.Count > 0;
        int requested = useCovers
            ? options.Covers.Count
            : options.Parts > 0 ? options.Parts : AutoParts(result.IsDelta, total);
        int parts = (int)Math.Min(requested, Math.Max(1, total));
        if (useCovers && parts != options.Covers.Count)
            throw new InvalidOperationException(
                $"There is only {total} bytes to send, too little to spread over {options.Covers.Count} cover images. Pass fewer covers.");

        int mode = useCovers ? PixelCodec.CoverMode(options.CoverBits) : options.StepBits;
        var permutation = new IndexPermutation(keys.PermutationKey, total);

        long baseLength = total / parts;
        int remainder = (int)(total % parts);
        int nameStart = RandomNumberGenerator.GetInt32(1000, 8000);

        int replaced = ClearPreviousImages(options.OutputDirectory, log);
        if (replaced > 0) result.ReplacedImages = replaced;

        long offset = 0;
        for (int index = 0; index < parts; index++)
        {
            int partLength = (int)(baseLength + (index < remainder ? 1 : 0));
            long needed = ImageHeader.Size + partLength;

            PngImage? cover = null;
            int width, height;
            if (useCovers)
            {
                cover = LoadCover(options.Covers[index], mode, needed);
                width = cover.Width;
                height = cover.Height;
            }
            else
            {
                (width, height) = PixelCodec.ChooseGeometry(needed, mode, index);
            }

            long capacity = PixelCodec.Capacity(width, height, mode);

            byte[] buffer = new byte[useCovers ? needed : capacity];
            if (!useCovers) RandomNumberGenerator.Fill(buffer.AsSpan((int)needed));

            var header = new ImageHeader
            {
                StepBits = (byte)mode,
                PartIndex = (byte)index,
                PartCount = (byte)parts,
                Salt = salt,
                EphemeralPublicKey = ephemeralPublicKey,
                Signature = signature,
                TotalCipherLength = total,
                PartOffset = offset,
                PartLength = partLength,
                ArchiveLength = result.ArchiveBytes,
                OriginalBytes = result.Summary.OriginalBytes,
                OriginalFileCount = result.Summary.FileCount,
                OriginalDirectoryCount = result.Summary.DirectoryCount,
                TreeDigestPrefix = result.Summary.TreeDigest.AsSpan(0, ImageHeader.DigestPrefixSize).ToArray()
            };
            header.Serialize(keys, width, height).CopyTo(buffer, 0);

            for (int i = 0; i < partLength; i++)
                buffer[ImageHeader.Size + i] = cipher[permutation.Map(offset + i)];

            string path = useCovers
                ? UniquePath(options.OutputDirectory, Path.GetFileNameWithoutExtension(options.Covers[index]))
                : Path.Combine(options.OutputDirectory, $"{options.NamePrefix}_{nameStart + index:0000}.png");

            if (useCovers) PixelCodec.WriteCover(path, buffer, mode, cover!);
            else PixelCodec.Write(path, buffer, mode, width, height);

            long fileLength = new FileInfo(path).Length;
            result.ImageBytes += fileLength;
            result.Images.Add(new PackedImage
            {
                Path = path,
                Width = width,
                Height = height,
                FileLength = fileLength,
                PayloadLength = partLength
            });
            log?.Invoke($"image {index + 1}/{parts}: {width}x{height}, {Sizes.Format(fileLength)} on disk, {Sizes.Format(partLength)} of payload");
            offset += partLength;
        }
    }

    public const int FullCopyParts = 5;
    private const long SingleImageBudget = 8L * 1024 * 1024;

    public static int AutoParts(bool isDelta, long cipherBytes)
    {
        if (!isDelta) return FullCopyParts;
        long parts = (cipherBytes + SingleImageBudget - 1) / SingleImageBudget;
        return (int)Math.Clamp(parts, 1, FullCopyParts);
    }

    private static PngImage LoadCover(string file, int mode, long needed)
    {
        string path = Paths.Clean(file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The cover image was not found: {path}");

        PngImage cover;
        try
        {
            cover = PngReader.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} cannot be used as a cover: {ex.Message} Save the picture as an ordinary PNG and try again.");
        }

        long capacity = PixelCodec.Capacity(cover.Width, cover.Height, mode);
        if (capacity < needed)
        {
            long pixelsNeeded = (needed * 8 + PixelCodec.BitsOf(mode) * 3 - 1) / (PixelCodec.BitsOf(mode) * 3);
            long side = (long)Math.Ceiling(Math.Sqrt(pixelsNeeded));
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} is {cover.Width}x{cover.Height} and holds {Sizes.Format(capacity)}, "
                + $"but {Sizes.Format(needed)} has to go in. Use a picture of at least {side}x{side}, "
                + "or raise the depth with --cover-bits.");
        }

        return cover;
    }

    private static string UniquePath(string directory, string baseName)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? "image" : baseName;
        string candidate = Path.Combine(directory, name + ".png");
        int suffix = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(directory, $"{name}-{suffix++}.png");
        return candidate;
    }

    private static int ClearPreviousImages(string directory, Action<string>? log)
    {
        int removed = 0;
        int foreign = 0;

        foreach (string file in Directory.EnumerateFiles(directory, "*.png"))
        {
            if (!ImageProbe.IsOwnImage(file))
            {
                foreign++;
                continue;
            }

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
                throw new IOException($"Cannot replace {Path.GetFileName(file)} from a previous run. Close whatever holds it open.");
            }
        }

        if (removed > 0) log?.Invoke($"removed {removed} image(s) from a previous run");
        if (foreign > 0) log?.Invoke($"left {foreign} PNG file(s) alone, they are not ImageConverter images");
        return removed;
    }

}

public sealed class UnpackOptions
{
    public List<string> Inputs = [];
    public string OutputDirectory = string.Empty;
    public string RecipientPrivateKeyPath = string.Empty;
    public string SenderPublicKeyPath = string.Empty;
    public byte[]? RecipientPrivateMaterial;
    public byte[]? SenderPublicMaterial;
    public string? KeyPassword;
    public bool SkipSignature;
    public bool RememberVersion;
}

public sealed class UnpackResult
{
    public int ImageCount;
    public long CipherBytes;
    public long ArchiveBytes;
    public long RestoredBytes;
    public int FileCount;
    public int DirectoryCount;
    public bool SignatureVerified;
    public ArchiveSummary Expected = new();
    public ArchiveSummary Actual = new();
    public bool SummaryMatches;
    public bool ImageSummaryMatches;
    public bool IsDelta;
    public DeltaStats? DeltaStats;
    public ArchiveSummary BaseSummary = new();
    public bool StateSaved;
    public byte[] StateDigest = [];
    public TimeSpan Elapsed;
}

public static class Unpacker
{
    public static UnpackResult Run(UnpackOptions options, Action<string>? log = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = ResolveInputs(options.Inputs);
        if (files.Count == 0) throw new FileNotFoundException("No PNG files found in the given input.");

        using var scratch = new Scratch();
        var result = new UnpackResult { ImageCount = files.Count };

        {
            using var recipientPrivate = options.RecipientPrivateMaterial != null
                ? KeyStore.RecipientPrivateFromDer(options.RecipientPrivateMaterial)
                : KeyStore.LoadRecipientPrivate(options.RecipientPrivateKeyPath, options.KeyPassword);

            var envelopes = new List<(string Path, HeaderEnvelope Envelope)>();
            foreach (string file in files)
            {
                var envelope = ImageProbe.Read(file);
                envelopes.Add((file, envelope));
            }

            var first = envelopes[0].Envelope;
            using var keys = HybridAgreement.Open(recipientPrivate, first.EphemeralPublicKey, first.Salt);

            var headers = new List<(string Path, ImageHeader Header)>();
            foreach (var (path, envelope) in envelopes)
            {
                ImageHeader header;
                try
                {
                    header = ImageHeader.OpenMeta(envelope, keys);
                }
                catch (CryptographicException)
                {
                    throw new CryptographicException($"{Path.GetFileName(path)} does not belong to this key pair.");
                }
                headers.Add((path, header));
            }

            var reference = headers[0].Header;
            if (headers.Count != reference.PartCount)
                throw new InvalidDataException($"Expected {reference.PartCount} images, got {headers.Count}.");

            var seen = new HashSet<int>();
            foreach (var (_, header) in headers)
            {
                if (header.TotalCipherLength != reference.TotalCipherLength || header.ArchiveLength != reference.ArchiveLength)
                    throw new InvalidDataException("Images come from different archives.");
                if (!seen.Add(header.PartIndex))
                    throw new InvalidDataException($"Part {header.PartIndex} appears twice.");
            }
            for (int i = 0; i < reference.PartCount; i++)
                if (!seen.Contains(i))
                    throw new InvalidDataException($"Part {i} of {reference.PartCount} is missing.");

            result.CipherBytes = reference.TotalCipherLength;
            result.ArchiveBytes = reference.ArchiveLength;
            var permutation = new IndexPermutation(keys.PermutationKey, reference.TotalCipherLength);

            Scratch.Guard(reference.TotalCipherLength);
            Scratch.Guard(reference.ArchiveLength);
            scratch.Done(scratch.Allocate("cipher", reference.TotalCipherLength));
            byte[] cipherBuffer = scratch.Buffer("cipher");

            foreach (var (path, header) in headers)
            {
                var image = PngReader.Read(path);
                byte[] bytes = PixelCodec.Extract(image, header.StepBits, ImageHeader.Size + header.PartLength);
                for (int i = 0; i < header.PartLength; i++)
                    cipherBuffer[permutation.Map(header.PartOffset + i)] = bytes[ImageHeader.Size + i];
                log?.Invoke($"part {header.PartIndex + 1}/{header.PartCount}: {Sizes.Format(header.PartLength)} recovered from {image.Width}x{image.Height}");
            }

            if (!options.SkipSignature)
            {
                using var senderPublic = options.SenderPublicMaterial != null
                    ? KeyStore.SenderPublicFromDer(options.SenderPublicMaterial)
                    : KeyStore.LoadSenderPublic(options.SenderPublicKeyPath);
                byte[] digest;
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    hash.AppendData(cipherBuffer, 0, (int)reference.TotalCipherLength);
                    digest = hash.GetHashAndReset();
                }

                result.SignatureVerified = senderPublic.VerifyHash(digest, reference.Signature);
                if (!result.SignatureVerified)
                    throw new CryptographicException("Sender signature does not match. The images were altered or came from another sender.");
                log?.Invoke("sender signature verified");
            }

            var cipherStream = scratch.Open("cipher");
            var plainStream = scratch.Create("archive");
            try
            {
                FrameCipher.Decrypt(cipherStream, reference.ArchiveLength, plainStream, keys);
            }
            finally
            {
                scratch.Done(cipherStream);
                scratch.Done(plainStream);
            }
            scratch.Release("cipher");

            uint payloadMagic;
            {
                var peek = scratch.Open("archive");
                Span<byte> marker = stackalloc byte[4];
                int read = peek.Read(marker);
                scratch.Done(peek);
                if (read != 4) throw new InvalidDataException("The decrypted payload is too short.");
                payloadMagic = BinaryPrimitives.ReadUInt32LittleEndian(marker);
            }

            Snapshot? resultState = null;
            var statePatterns = new List<string>();

            if (payloadMagic == DeltaWriter.Magic)
            {
                var applier = new DeltaApplier();
                var stream = scratch.Open("archive");
                try
                {
                    applier.Apply(stream, options.OutputDirectory, log);
                }
                finally
                {
                    scratch.Done(stream);
                }

                resultState = applier.ResultSnapshot;
                statePatterns = applier.FilterPatterns;
                result.IsDelta = true;
                result.DeltaStats = applier.Stats;
                result.BaseSummary = applier.Base;
                result.Expected = applier.Expected;
                result.Actual = applier.Actual;
                result.FileCount = applier.Actual.FileCount;
                result.DirectoryCount = applier.Actual.DirectoryCount;
                result.RestoredBytes = applier.Actual.OriginalBytes;
            }
            else
            {
                var extractor = new ArchiveExtractor();
                var archive = scratch.Open("archive");
                try
                {
                    extractor.Extract(archive, options.OutputDirectory, log);
                }
                finally
                {
                    scratch.Done(archive);
                }

                statePatterns = extractor.FilterPatterns;
                result.FileCount = extractor.Stats.FileCount + extractor.Stats.DuplicateCount;
                result.DirectoryCount = extractor.Stats.DirectoryCount;
                result.RestoredBytes = extractor.Stats.RawBytes;
                result.Expected = extractor.Expected;
                result.Actual = extractor.Actual;
            }

            result.SummaryMatches = result.Expected.Matches(result.Actual);
            result.ImageSummaryMatches =
                reference.OriginalBytes == result.Expected.OriginalBytes
                && reference.OriginalFileCount == result.Expected.FileCount
                && reference.OriginalDirectoryCount == result.Expected.DirectoryCount
                && reference.TreeDigestPrefix.AsSpan()
                    .SequenceEqual(result.Expected.TreeDigest.AsSpan(0, ImageHeader.DigestPrefixSize));

            if (!result.ImageSummaryMatches)
                throw new InvalidDataException(
                    "The summary stored in the images does not match the summary stored in the payload.");

            if (!result.SummaryMatches)
                throw new InvalidDataException(
                    $"Result does not match what the sender packed. Expected {result.Expected.FileCount} files, "
                    + $"{result.Expected.DirectoryCount} folders and {Sizes.Format(result.Expected.OriginalBytes)} "
                    + $"with digest {TreeDigest.Short(result.Expected.TreeDigest)}; got {result.Actual.FileCount} files, "
                    + $"{result.Actual.DirectoryCount} folders and {Sizes.Format(result.Actual.OriginalBytes)} "
                    + $"with digest {TreeDigest.Short(result.Actual.TreeDigest)}.");

            log?.Invoke($"summary verified: {result.Actual.FileCount} files, {Sizes.Format(result.Actual.OriginalBytes)}, digest {TreeDigest.Short(result.Actual.TreeDigest)}");

            if (options.RememberVersion)
            {
                resultState ??= StateStore.Capture(options.OutputDirectory, statePatterns);
                StateStore.Save(options.OutputDirectory, resultState);
                result.StateSaved = true;
                result.StateDigest = resultState.TreeDigestValue;
                log?.Invoke($"state saved: {TreeDigest.Short(resultState.TreeDigestValue)} in {StateStore.FolderName}, this folder can now send changes back");
            }
        }

        result.Elapsed = stopwatch.Elapsed;
        return result;
    }

    private static List<string> ResolveInputs(List<string> inputs)
    {
        var files = new List<string>();
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
                files.AddRange(Directory.EnumerateFiles(input, "*.png", SearchOption.TopDirectoryOnly));
            else if (File.Exists(input))
                files.Add(input);
            else
                throw new FileNotFoundException($"Input not found: {input}");
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

}
