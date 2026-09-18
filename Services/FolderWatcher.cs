namespace FolderDiff.Services;

public sealed class FolderWatcher : IDisposable
{
    private const int DebounceMilliseconds = 800;

    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _debounce;

    public FolderWatcher() => _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

    public event EventHandler? Changed;

    public void Start(IEnumerable<string> roots)
    {
        Stop();

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.Size
            };

            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;

            _watchers.Add(watcher);
        }
    }

    public void Stop()
    {
        _debounce.Change(Timeout.Infinite, Timeout.Infinite);

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileSystemEvent;
            watcher.Created -= OnFileSystemEvent;
            watcher.Deleted -= OnFileSystemEvent;
            watcher.Renamed -= OnFileSystemEvent;
            watcher.Error -= OnError;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        Stop();
        _debounce.Dispose();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => Schedule();

    private void OnError(object sender, ErrorEventArgs e) => Schedule();

    private void Schedule() => _debounce.Change(DebounceMilliseconds, Timeout.Infinite);

    private void Fire()
    {
        var handler = Changed;
        if (handler is null)
            return;

        if (_context is null)
            handler(this, EventArgs.Empty);
        else
            _context.Post(_ => handler(this, EventArgs.Empty), null);
    }
}
