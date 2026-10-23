using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Steamy.Models;

/// <summary>
/// An <see cref="ObservableObject"/> whose change notifications are always raised on the WPF UI
/// thread. Models of this kind (download jobs, catalog items, library games) are mutated from
/// background threads — the download process reader, artwork loaders, catalog refresh — while they
/// are data-bound in the UI. Raising <see cref="INotifyPropertyChanged.PropertyChanged"/> directly
/// on those threads makes a bound WPF element update off its own dispatcher, which throws and can
/// crash the app. Marshalling the notification keeps every binding update on the UI thread.
/// </summary>
public abstract class UiObservableObject : ObservableObject
{
    // Most public catalog entries are never bound or changed. Allocate batching
    // state only when an actual cross-dispatcher notification needs it.
    private NotificationBatch? _notifications;
    private sealed class NotificationBatch
    {
        internal readonly HashSet<string?> Properties = new();
        internal bool Queued;
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        var application = Application.Current;
        var dispatcher = application?.Dispatcher;

        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            var batch = System.Threading.LazyInitializer.EnsureInitialized(ref _notifications, static () => new NotificationBatch());
            lock (batch)
            {
                if (string.IsNullOrEmpty(e.PropertyName)) { batch.Properties.Clear(); batch.Properties.Add(null); }
                else if (!batch.Properties.Contains(null)) batch.Properties.Add(e.PropertyName);
                if (batch.Queued) return;
                batch.Queued = true;
            }
            try
            {
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    string?[] properties;
                    lock (batch)
                    {
                        properties = batch.Properties.ToArray();
                        batch.Properties.Clear();
                        batch.Queued = false;
                    }
                    foreach (var property in properties) base.OnPropertyChanged(new PropertyChangedEventArgs(property));
                }));
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No binding callback is safe once the dispatcher is tearing down.
            }
            catch (InvalidOperationException)
            {
                // Same: the dispatcher was shutting down between the check and the post.
            }
            lock (batch) { batch.Properties.Clear(); batch.Queued = false; }
            return;
        }
        if (dispatcher is not null && dispatcher.HasShutdownStarted && !dispatcher.CheckAccess()) return;
        base.OnPropertyChanged(e);
    }
}
