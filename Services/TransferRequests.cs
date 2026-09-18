namespace FolderDiff.Services;

public enum PackBaseMode
{
    Full,
    AgainstFolder,
    SnapshotFile,
    RememberedVersion
}

public enum TransferKeyMode
{
    Code,
    PemFolder
}

public sealed record PackRequest(
    string SourceFolder,
    string OutputFolder,
    PackBaseMode BaseMode,
    string? BaseFolder,
    string? SnapshotPath,
    IReadOnlyList<string> ExcludePatterns,
    int Parts,
    int Threads,
    int StepBits,
    int CompressionLevel,
    int ChunkMiB,
    string NamePrefix,
    bool KeepArchive,
    bool RememberVersion,
    IReadOnlyList<string> Covers,
    int CoverBits,
    string Priority);

public sealed record UnpackRequest(
    string ImagesFolder,
    string TargetFolder,
    bool SkipSignature,
    bool RememberVersion,
    string Priority);

public sealed record SnapshotRequest(
    string SourceFolder,
    string SnapshotPath,
    IReadOnlyList<string> ExcludePatterns);
