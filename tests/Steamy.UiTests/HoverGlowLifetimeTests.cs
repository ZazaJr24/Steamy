using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed class HoverGlowLifetimeTests
{
    [Fact]
    public void DetachedHoverOverlayDoesNotKeepItsCardAliveWithoutAnUnloadEvent()
    {
        OnSta(() =>
        {
            var references = AbandonOverlay();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(references.Owner.IsAlive, "A global preference event must not own a detached hover card.");
            Assert.False(references.Overlay.IsAlive);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Overlay) AbandonOverlay()
    {
        var (owner, overlay) = CreateOverlay();
        overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return (new(owner), new(overlay));
    }

    [Fact]
    public void RepeatedLoadsHaveOneObserverAndUnloadOrDisableStopsUpdates()
    {
        OnSta(() =>
        {
            var previous = MotionPreferences.ReduceEffects;
            var (owner, overlay) = CreateOverlay();
            try
            {
                overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                overlay.Opacity = .75;
                MotionPreferences.Configure(!previous);
                Assert.Equal(0, overlay.Opacity);
                overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                overlay.Opacity = .5;
                MotionPreferences.Configure(previous);
                Assert.Equal(.5, overlay.Opacity);
                overlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                overlay.Opacity = .75;
                MotionPreferences.Configure(!previous);
                Assert.Equal(0, overlay.Opacity);
                DownloadHoverGlow.SetIsEnabled(overlay, false);
                overlay.Opacity = .5;
                MotionPreferences.Configure(previous);
                Assert.Equal(.5, overlay.Opacity);
                GC.KeepAlive(owner);
            }
            finally
            {
                DownloadHoverGlow.SetIsEnabled(overlay, false);
                MotionPreferences.Configure(previous);
            }
        });
    }

    private static (ListBoxItem Owner, Border Overlay) CreateOverlay()
    {
        var owner = new ListBoxItem { Template = new ControlTemplate(typeof(ListBoxItem))
            { VisualTree = new FrameworkElementFactory(typeof(Border), "HoverOverlay") } };
        owner.ApplyTemplate();
        var overlay = (Border)owner.Template.FindName("HoverOverlay", owner);
        Assert.NotNull(System.Windows.Media.VisualTreeHelper.GetParent(overlay));
        DownloadHoverGlow.SetIsEnabled(overlay, true);
        return (owner, overlay);
    }

    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { check(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
