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
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        var application = Application.Current;
        var dispatcher = application?.Dispatcher;

        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.BeginInvoke(() => base.OnPropertyChanged(e));
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Dispatcher is tearing down; fall through and notify inline.
            }
            catch (InvalidOperationException)
            {
                // Same: the dispatcher was shutting down between the check and the post.
            }
        }

        base.OnPropertyChanged(e);
    }
}
