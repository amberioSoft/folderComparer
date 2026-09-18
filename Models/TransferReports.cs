namespace FolderDiff.Models;

public sealed record TransferImage(string Path, int Width, int Height, long FileLength, long PayloadLength)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Resolution => $"{Width}x{Height}";

    public string FileLengthText => ByteSize.Format(FileLength);

    public string PayloadLengthText => ByteSize.Format(PayloadLength);
}

public sealed record DeltaSummary(
    int KeptCount,
    int AddedCount,
    int PatchedCount,
    int DeletedFileCount,
    int DeletedDirectoryCount,
    int AddedDirectoryCount,
    long LiteralBytes,
    long CopiedBytes,
    long NewTotalBytes)
{
    public string LiteralBytesText => ByteSize.Format(LiteralBytes);

    public string CopiedBytesText => ByteSize.Format(CopiedBytes);

    public string NewTotalBytesText => ByteSize.Format(NewTotalBytes);

    public string FilesText => $"без змін {KeptCount}, нових {AddedCount}, змінених {PatchedCount}";

    public string RemovedText => $"видалено файлів {DeletedFileCount}, папок {DeletedDirectoryCount}";
}

public sealed record PackReport(
    bool IsDelta,
    IReadOnlyList<TransferImage> Images,
    long ImageBytes,
    long SourceBytes,
    long ArchiveBytes,
    long CipherBytes,
    int FileCount,
    int DirectoryCount,
    long OriginalBytes,
    string TreeDigest,
    TimeSpan Elapsed,
    int ReplacedImages,
    DeltaSummary? Delta)
{
    public string ModeText => IsDelta ? "тільки зміни" : "повна копія";

    public string ImageBytesText => ByteSize.Format(ImageBytes);

    public string SourceBytesText => ByteSize.Format(SourceBytes);

    public string ArchiveBytesText => ByteSize.Format(ArchiveBytes);

    public string CipherBytesText => ByteSize.Format(CipherBytes);

    public string OriginalBytesText => ByteSize.Format(OriginalBytes);

    public string ContentText => $"файлів {FileCount}, папок {DirectoryCount}";

    public string ElapsedText => $"{Elapsed.TotalSeconds:0.00} с";

    public bool HasImages => Images.Count > 0;
}

public sealed record UnpackReport(
    bool SummaryMatches,
    bool ImageSummaryMatches,
    bool SignatureVerified,
    int FileCount,
    int DirectoryCount,
    long OriginalBytes,
    bool WasDelta,
    TimeSpan Elapsed)
{
    public string ModeText => WasDelta ? "застосовано набір змін" : "відновлено повну копію";

    public string ContentText => $"файлів {FileCount}, папок {DirectoryCount}";

    public string OriginalBytesText => ByteSize.Format(OriginalBytes);

    public string ElapsedText => $"{Elapsed.TotalSeconds:0.00} с";

    public bool AllChecksPassed => SummaryMatches && ImageSummaryMatches && SignatureVerified;
}

public sealed record KeyStatus(bool IsValid, bool CanPack, bool CanUnpack, string Description, string? Source);
