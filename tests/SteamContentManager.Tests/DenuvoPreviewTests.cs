using SteamContentManager.Services;
using SteamContentManager.ViewModels;
using Xunit;

namespace SteamContentManager.Tests;

/// <summary>
/// The Denuvo Generation page had a view model with no members at all, so every control on it was
/// dead. These tests keep the page honest: it produces local text, never a network call, and it
/// creates the preview before trying to export one.
/// </summary>
public sealed class DenuvoPreviewTests
{
    private static DenuvoGenerationViewModel CreateViewModel()
    {
        var store = new AppDataStore();
        return new DenuvoGenerationViewModel(store, new Nav(), new InMemoryLoggingService(store, new NullLocalDatabase()));
    }

    [Fact]
    public void ThePageOffersTemplatesAndSaysNothingWasDownloaded()
    {
        var viewModel = CreateViewModel();

        Assert.NotEmpty(viewModel.Templates);
        Assert.False(viewModel.HasNoTemplates);

        viewModel.RefreshCommand.Execute(null);
        Assert.Contains("nothing was downloaded", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectingATemplateGeneratesALocalPreview()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedTemplate = viewModel.Templates.Last();

        Assert.True(viewModel.HasPreview);
        Assert.Contains(viewModel.SelectedTemplate!.Name, viewModel.Preview, StringComparison.Ordinal);
        Assert.Contains("\"local\": true", viewModel.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportingWritesExactlyWhatIsOnScreen()
    {
        var viewModel = CreateViewModel();
        var path = Path.Combine(Path.GetTempPath(), $"denuvo-preview-{Guid.NewGuid():N}.json");

        try
        {
            await viewModel.ExportAsync(path);

            Assert.True(File.Exists(path));
            Assert.Equal(viewModel.Preview, await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportingWithoutAPathIsIgnoredInsteadOfThrowing()
    {
        var viewModel = CreateViewModel();

        await viewModel.ExportAsync(null);
        await viewModel.ExportAsync("   ");
    }

    private sealed class Nav : INavigationService
    {
        public void Attach(Action<Type> navigate) { }
        public void Detach() { }
        public void Navigate<TPage>() { }
    }
}
