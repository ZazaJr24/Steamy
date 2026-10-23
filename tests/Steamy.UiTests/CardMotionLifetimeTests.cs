using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed class CardMotionLifetimeTests
{
    [Fact]
    public void ADetachedCardCanBeCollectedEvenIfItsUnloadNotificationIsMissing()
    {
        OnSta(() =>
        {
            var reference = CreateDetachedCard();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(reference.IsAlive, "A global motion preference event must not own a detached card or its page.");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDetachedCard()
    {
        var card = new Button();
        CardMotion.SetIsEnabled(card, true);
        card.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        // Recycled template parts can lose their visual parent before WPF sends
        // Unloaded. Preference notifications must not keep that orphan alive.
        return new WeakReference(card);
    }

    [Fact]
    public void ALoadedCardStillReceivesPreferencesAndCanUnloadAndReload()
    {
        OnSta(() =>
        {
            var previous = MotionPreferences.ReduceEffects;
            var card = new Button();
            try
            {
                CardMotion.SetIsEnabled(card, true);
                CardMotion.SetExpandedWidth(card, 200);
                card.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                MotionPreferences.Configure(false);
                card.Width = 160;
                MotionPreferences.Configure(true);
                Assert.Equal(CardMotion.GetCollapsedWidth(card), card.Width);
                card.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.True(double.IsNaN(card.Width));
                card.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                card.Width = 160;
                MotionPreferences.Configure(false); MotionPreferences.Configure(true);
                Assert.Equal(CardMotion.GetCollapsedWidth(card), card.Width);
            }
            finally
            {
                card.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                MotionPreferences.Configure(previous);
            }
        });
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
