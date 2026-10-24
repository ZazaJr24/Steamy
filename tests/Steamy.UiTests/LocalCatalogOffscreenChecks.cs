using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Pages;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Models;

namespace Steamy.UiTests;

public sealed partial class PageSmokeTests
{
    private static void CheckLocalCatalogOffscreen(IServiceProvider services)
    {
        Assert.Empty(Application.Current.Windows.Cast<Window>());
        var folder = Environment.GetEnvironmentVariable("STEAMY_UI_ARTIFACTS")!;
        Directory.CreateDirectory(folder);
        var catalog = services.GetRequiredService<LocalGameCatalog>();
        var archive = Path.Combine(folder, "fixture.zip");
        File.WriteAllBytes(archive, []);
        var save = catalog.UpsertAsync(new LocalGamePackage { Id = "local-fix", Kind = "Fix", Name = "Local fix game", AppId = "1808500", ArchivePath = archive, Steps = ["Back up original files."] });
        PumpUntil(() => save.IsCompleted); save.GetAwaiter().GetResult();
        var fixes = new GameFixesViewModel(services.GetRequiredService<IAppDataStore>(), services.GetRequiredService<INavigationService>(),
            services.GetRequiredService<ILoggingService>(), new LocalMergeFixFixture(), services.GetRequiredService<IArtworkService>(), catalog);
        var fetch = fixes.OnNavigatedToAsync(); PumpUntil(() => fetch.IsCompleted); fetch.GetAwaiter().GetResult();
        var merged = Assert.Single(fixes.PagedGames);
        Assert.Equal(2, merged.Game!.Fixes.Count);
        Assert.Contains(merged.Game.Fixes, fix => fix.LocalArchivePath == archive && fix.Instructions.Contains("Back up"));
        foreach (var theme in new[] { "Dark", "Light" })
        {
            UiThemeService.Apply(theme);
            var editor = new LocalCatalogEditorWindow(catalog);
            try
            {
                var load = editor.ReloadAsync(); PumpUntil(() => load.IsCompleted); load.GetAwaiter().GetResult();
                var content = editor.Content; editor.Content = null;
                var page = new Page { Content = content };
                LayoutFlow(page, 1050); SaveFlow(page, folder, "fix-editor-" + theme, 1050);
                Assert.Single(((ComboBox)editor.FindName("Kind")).Items);
            }
            finally { editor.Close(); }
        }
        Assert.Empty(Application.Current.Windows.Cast<Window>());
    }
    private sealed class LocalMergeFixFixture : IFixCatalogService
    {
        public Task<FixFeedSnapshot> GetFixesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FixFeedSnapshot(true, [new FixGame { AppId = "1808500", Name = "Remote fixture", Fixes = [new FixEntry { Filename = "remote.zip" }] }], DateTimeOffset.UtcNow, false, "Fixture source"));
    }
}
