using System.Collections.Concurrent;
using System.IO;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed partial class GameDetailsTests
{
    [Fact]
    public async Task ADelayedPreferenceReadCannotOverwriteANewerAlreadySavedEdit()
    {
        var preferences = new ReloadedPreferences { DeferSecond = true };
        var model = Model(new RelatedData(), preferences);
        model.SelectGame(Game(42)); await model.ActivateAsync();
        var reentry = model.ActivateAsync();
        try
        {
            await preferences.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.Notes = "Newer draft";
            await model.FlushAsync();
            preferences.Second.TrySetResult(new("Old disk snapshot"));
            await reentry.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Newer draft", model.Notes);
            Assert.Equal("Newer draft", Assert.Single(preferences.Saved).Notes);
        }
        finally
        {
            preferences.Second.TrySetResult(new("Old disk snapshot"));
            await reentry.WaitAsync(TimeSpan.FromSeconds(5)); await model.DeactivateAsync();
        }
    }

    [Fact]
    public async Task ReopeningAnUneditedGameDoesNotResaveTheLoadedPreferences()
    {
        var preferences = new ReloadedPreferences();
        var model = Model(new RelatedData(), preferences);
        model.SelectGame(Game(42)); await model.ActivateAsync();
        await model.DeactivateAsync(); await model.ActivateAsync();
        Assert.Equal("Updated on disk", model.Notes);
        await model.DeactivateAsync();
        Assert.Empty(preferences.Saved);
    }

    [Fact]
    public async Task AnUnsuccessfulSaveKeepsTheDraftWhenTheSameGameReopens()
    {
        var preferences = new ReloadedPreferences { FailSave = true };
        var model = Model(new RelatedData(), preferences);
        model.SelectGame(Game(42)); await model.ActivateAsync();
        model.Notes = "Unsaved note to retain";
        await model.FlushAsync();
        Assert.Contains("Could not save", model.SaveStatus);
        await model.ActivateAsync();
        Assert.Equal("Unsaved note to retain", model.Notes);
        Assert.Equal(1, preferences.Reads);
        Assert.True(model.CanEditPreferences);
        await model.DeactivateAsync();
    }

    private sealed class ReloadedPreferences : IGamePreferencesStore
    {
        internal bool DeferSecond, FailSave;
        internal int Reads;
        internal readonly TaskCompletionSource SecondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<GamePreferences> Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentBag<GamePreferences> Saved = [];
        public Task<GamePreferences> ReadAsync(int appId, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref Reads) == 1) return Task.FromResult(new GamePreferences("Initial file"));
            SecondStarted.TrySetResult();
            return DeferSecond ? Second.Task : Task.FromResult(new GamePreferences("Updated on disk"));
        }
        public Task SaveAsync(int appId, GamePreferences preferences, CancellationToken token = default)
        {
            if (FailSave) throw new IOException("A controlled unavailable-disk fixture.");
            Saved.Add(preferences); return Task.CompletedTask;
        }
    }
}
