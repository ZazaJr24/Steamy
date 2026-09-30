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
    private readonly object _notificationGate = new();
    private readonly HashSet<string?> _pendingProperties = new();
    private bool _notificationQueued;
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        var application = Application.Current;
        var dispatcher = application?.Dispatcher;

        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            lock (_notificationGate)
            {
                if (string.IsNullOrEmpty(e.PropertyName)) { _pendingProperties.Clear(); _pendingProperties.Add(null); }
                else if (!_pendingProperties.Contains(null)) _pendingProperties.Add(e.PropertyName);
                if (_notificationQueued) return;
                _notificationQueued = true;
            }
            try
            {
                dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    string?[] properties;
                    lock (_notificationGate)
                    {
                        properties = _pendingProperties.ToArray();
                        _pendingProperties.Clear();
                        _notificationQueued = false;
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
            lock (_notificationGate) { _pendingProperties.Clear(); _notificationQueued = false; }
            return;
        }
        if (dispatcher is not null && dispatcher.HasShutdownStarted && !dispatcher.CheckAccess()) return;
        base.OnPropertyChanged(e);
    }
}
