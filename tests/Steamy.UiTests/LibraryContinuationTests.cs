using System.Collections.Specialized;

namespace Steamy.UiTests;

public sealed partial class LibrarySearchLifetimeTests
{
    [Fact]
    public async Task ShowMoreAppendsFortyEightWithoutResettingExistingCards()
    {
        var model = CreateModel(); Seed(model);
        try
        {
            await Search(model, "Fixture");
            var first = model.PagedCatalogItems.ToArray();
            Assert.Equal(48, first.Length);
            var changes = new List<NotifyCollectionChangedAction>();
            var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.PagedCatalogItems.CollectionChanged += (_, change) =>
            {
                changes.Add(change.Action);
                if (model.PagedCatalogItems.Count == 96) appended.TrySetResult();
            };
            model.LoadMoreCommand.Execute(null);
            await appended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(first, model.PagedCatalogItems.Take(48));
            Assert.Equal(48, changes.Count);
            Assert.All(changes, change => Assert.Equal(NotifyCollectionChangedAction.Add, change));
            Assert.True(model.CanLoadMore);
            Assert.Equal("Show 48 more", model.LoadMoreLabel);
            await Search(model, "Fixture"); // A new filter starts with one bounded batch again.
            Assert.Equal(48, model.PagedCatalogItems.Count);
        }
        finally { await model.OnNavigatedFromAsync(); }
    }

    [Fact]
    public async Task LastBatchAndInactivePageDoNotRequestAdditionalCards()
    {
        var model = CreateModel(); Seed(model);
        try
        {
            await Search(model, "999");
            Assert.False(model.CanLoadMore);
            model.LoadMoreCommand.Execute(null);
            Assert.Single(model.PagedCatalogItems);
            await Search(model, "Fixture");
            await model.OnNavigatedFromAsync();
            Assert.False(model.CanLoadMore);
            var count = model.PagedCatalogItems.Count;
            model.LoadMoreCommand.Execute(null);
            Assert.Equal(count, model.PagedCatalogItems.Count);
        }
        finally { await model.OnNavigatedFromAsync(); }
    }
}
