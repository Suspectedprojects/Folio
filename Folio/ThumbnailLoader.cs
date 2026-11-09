using System.Windows.Threading;

namespace Folio;

/// <summary>Background STA workers that render shell thumbnails. Newest requests are served first,
/// so whatever you just scrolled to loads before things you scrolled past.</summary>
public sealed class ThumbnailLoader
{
    readonly record struct Job(FileItem Item, int Px, bool Icon);

    readonly List<Job> _jobs = new();
    readonly object _lock = new();
    readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    readonly Dispatcher _ui;
    public double DpiScale { get; set; } = 1.0;

    public ThumbnailLoader(Dispatcher ui, int threads)
    {
        _ui = ui;
        for (int i = 0; i < threads; i++)
        {
            var t = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"Thumbs{i}" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public void Request(FileItem item, int px, bool icon)
    {
        lock (_lock) _jobs.Add(new Job(item, px, icon));
        _signal.Release();
    }

    /// <summary>Drop queued work (called on the UI thread when leaving a folder).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var j in _jobs) j.Item.CancelRequest(j.Icon);
            _jobs.Clear();
        }
    }

    void Work()
    {
        while (true)
        {
            _signal.Wait();
            Job job;
            lock (_lock)
            {
                if (_jobs.Count == 0) continue;
                job = _jobs[^1];
                _jobs.RemoveAt(_jobs.Count - 1);
            }
            if (!job.Icon && job.Item.PendingPx != job.Px) continue; // no longer wanted
            var img = ShellImages.Get(job.Item.FullPath, job.Px, job.Icon, DpiScale);
            _ui.BeginInvoke(DispatcherPriority.Background, () => job.Item.Deliver(img, job.Px, job.Icon));
        }
    }
}
