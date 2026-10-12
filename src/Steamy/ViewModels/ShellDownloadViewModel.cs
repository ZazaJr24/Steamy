using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

/// <summary>The shell follows the queue independently of the Downloads page's filters.</summary>
public sealed class ShellDownloadViewModel : ObservableObject, IDisposable
{
    private readonly IAppDataStore _store;
    private readonly IArtworkService _artwork;
    private readonly HashSet<DownloadJob> _watched = [];
    private CancellationTokenSource? _artworkCancellation;
    private DownloadJob? _job;
    private BitmapImage? _image;
    private bool _disposed;

    public ShellDownloadViewModel(IAppDataStore store, IArtworkService artwork)
    {
        _store = store; _artwork = artwork;
        store.Downloads.CollectionChanged += JobsChanged;
        WatchJobs(); Refresh();
    }

    public DownloadJob? Job => _job;
    public BitmapImage? Artwork => _image;
    public bool HasArtwork => _image is not null;
    public int QueuedCount => _store.Downloads.Count(job => job.State == DownloadJobState.Queued);
    public string Summary => _job is null ? "Downloads · queue is empty"
        : $"{_job.GameName} · {_job.StateLabel}" + (QueuedCount > 0 ? $" · {QueuedCount} queued" : "");

    private void WatchJobs()
    {
        var present = _store.Downloads.ToHashSet();
        foreach (var removed in _watched.Where(job => !present.Contains(job)).ToArray())
        { removed.PropertyChanged -= JobChanged; _watched.Remove(removed); }
        foreach (var job in _store.Downloads)
            if (_watched.Add(job)) job.PropertyChanged += JobChanged;
    }

    private void JobsChanged(object? sender, NotifyCollectionChangedEventArgs args) { WatchJobs(); Refresh(); }
    private void JobChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DownloadJob.State) or nameof(DownloadJob.Priority) or nameof(DownloadJob.QueuePosition)) Refresh();
    }

    private void Refresh()
    {
        if (_disposed) return;
        var selected = _store.Downloads.FirstOrDefault(job => job.IsActive)
            ?? _store.Downloads.FirstOrDefault(job => job.State == DownloadJobState.Paused)
            ?? _store.Downloads.Where(job => job.State == DownloadJobState.Queued).OrderByDescending(job => job.Priority).ThenBy(job => job.QueuePosition).FirstOrDefault()
            ?? _store.Downloads.FirstOrDefault(job => job.State == DownloadJobState.Failed);
        if (!ReferenceEquals(_job, selected))
        {
            _job = selected; OnPropertyChanged(nameof(Job));
            _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose();
            _artworkCancellation = null;
            SetArtwork(_store.Games.FirstOrDefault(game => game.AppId == selected?.AppId)?.ArtworkImage);
            if (_image is null && selected is { AppId: > 0 })
            {
                _artworkCancellation = new();
                _ = LoadArtworkAsync(selected, _artworkCancellation.Token);
            }
        }
        OnPropertyChanged(nameof(QueuedCount)); OnPropertyChanged(nameof(Summary));
    }

    private async Task LoadArtworkAsync(DownloadJob job, CancellationToken token)
    {
        try
        {
            var game = new Game { AppId = job.AppId, Name = job.GameName };
            await _artwork.LoadAsync(game, token);
            if (!token.IsCancellationRequested && !_disposed && ReferenceEquals(job, _job)) SetArtwork(game.ArtworkImage);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Artwork is optional; the real queue and controls remain available. */ }
    }

    private void SetArtwork(BitmapImage? value)
    {
        if (SetProperty(ref _image, value, nameof(Artwork))) OnPropertyChanged(nameof(HasArtwork));
    }

    public void Dispose()
    {
        _disposed = true;
        _store.Downloads.CollectionChanged -= JobsChanged;
        foreach (var job in _watched) job.PropertyChanged -= JobChanged;
        _watched.Clear(); _artworkCancellation?.Cancel(); _artworkCancellation?.Dispose();
    }
}
