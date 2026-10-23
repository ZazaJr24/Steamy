using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class NavigationReuseTests
{
    [Fact]
    public void RevisitedPagesKeepTheirViewAndDraftWithOnlyThreeRecentViewsRetained()
    {
        OnSta(() =>
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var evicted = new List<FrameworkElement>();
            var provider = new NavigationViewPageProvider(services).CreateWindowScope(evicted.Add);
            var first = (First)provider.GetPage(typeof(First))!;
            first.Content = new TextBox { Text = "unsaved input" };
            var second = provider.GetPage(typeof(Second));
            var third = provider.GetPage(typeof(Third));
            Assert.Same(first, provider.GetPage(typeof(First)));
            Assert.Equal("unsaved input", ((TextBox)first.Content).Text);
            provider.GetPage(typeof(Fourth));
            Assert.Equal(3, provider.CachedPageCount);
            Assert.Equal(new[] { second }, evicted);
            Assert.Same(first, provider.GetPage(typeof(First)));
            Assert.Same(third, provider.GetPage(typeof(Third)));
            Assert.NotSame(second, provider.GetPage(typeof(Second)));
            Assert.Equal(3, provider.CachedPageCount);
        });
    }

    [Fact]
    public void WindowScopesNeverShareAViewAndTransientRoutesRemainWithinTheLimit()
    {
        OnSta(() =>
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var root = new NavigationViewPageProvider(services);
            var first = root.CreateWindowScope();
            var releases = new List<FrameworkElement>();
            var second = root.CreateWindowScope(releases.Add);
            Assert.NotSame(first.GetPage(typeof(First)), second.GetPage(typeof(First)));
            second.GetPage(typeof(Second)); second.GetPage(typeof(Third));
            var transient = second.GetPage(typeof(Border));
            Assert.Equal(3, second.CachedPageCount);
            second.GetPage(typeof(Second));
            Assert.Contains(transient, releases);
            Assert.Equal(2, second.CachedPageCount);
        });
    }

    public sealed class First : Page { }
    public sealed class Second : Page { }
    public sealed class Third : Page { }
    public sealed class Fourth : Page { }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
