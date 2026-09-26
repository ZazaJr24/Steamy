using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

/// <summary>One local preview template. It only describes text this app writes itself.</summary>
public sealed record PreviewTemplate(string Name, string Type, string Description, string Status, string OutputFormat);

/// <summary>
/// Drives the Denuvo Generation page. The page is honest about what it is: it produces local
/// metadata previews so a format can be inspected without touching Steam, a game installation or
/// any protection layer. Nothing is downloaded, patched or unlocked, which is why the templates
/// below only describe plain text output.
/// </summary>
public sealed class DenuvoGenerationViewModel : ViewModelBase
{
    private readonly ObservableCollection<PreviewTemplate> _templates = new()
    {
        new PreviewTemplate(
            "Local metadata preview",
            "JSON · app id, depot ids, branch",
            "Shows which identifying fields a local metadata file would contain, so the format can be reviewed before anything is written.",
            "Ready",
            "json · written locally"),
        new PreviewTemplate(
            "Manifest header preview",
            "JSON · depot id, manifest id, size",
            "A header-only sample with placeholders. Useful to check field names and ordering against a real manifest you already own.",
            "Ready",
            "json · written locally"),
        new PreviewTemplate(
            "Empty test stub",
            "JSON · structure only",
            "An empty skeleton with every field set to null. Nothing is filled in and nothing is looked up.",
            "Ready",
            "json · written locally")
    };

    private PreviewTemplate? _selectedTemplate;
    private string _preview = string.Empty;
    private string _status = "No preview generated yet.";

    public DenuvoGenerationViewModel(IAppDataStore store, INavigationService navigation, ILoggingService logging)
        : base(store, navigation, logging)
    {
        _selectedTemplate = _templates.FirstOrDefault();

        RefreshCommand = new RelayCommand(Refresh);
        GenerateCommand = new RelayCommand(Generate);
        ExportCommand = new AsyncRelayCommand<string>(ExportAsync);
    }

    public ObservableCollection<PreviewTemplate> Templates => _templates;

    public PreviewTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetProperty(ref _selectedTemplate, value)) Generate();
        }
    }

    public string Preview
    {
        get => _preview;
        private set
        {
            if (SetProperty(ref _preview, value)) OnPropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);

    public bool HasNoTemplates => _templates.Count == 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SafetySummary =>
        "Only local mock/test metadata previews are produced here. No DRM, licensing, authentication or access-control operation is performed or available.";

    public ICommand RefreshCommand { get; }
    public ICommand GenerateCommand { get; }
    public IAsyncRelayCommand<string> ExportCommand { get; }

    /// <summary>Rebuilds the template list and says plainly that nothing was fetched.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(Templates));
        OnPropertyChanged(nameof(HasNoTemplates));

        if (SelectedTemplate is null) SelectedTemplate = _templates.FirstOrDefault();
        Status = _templates.Count == 0
            ? "No local templates are configured."
            : $"{_templates.Count} local templates available — nothing was downloaded.";
    }

    private void Generate()
    {
        if (SelectedTemplate is null)
        {
            Preview = string.Empty;
            Status = "Select a template first.";
            return;
        }

        Preview = BuildPreview(SelectedTemplate);
        Status = $"Preview generated locally from “{SelectedTemplate.Name}”.";
    }

    /// <summary>Writes a preview that needs no game, no account and no network.</summary>
    private static string BuildPreview(PreviewTemplate template)
    {
        var payload = new
        {
            generatedBy = "Steamy",
            generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            template = template.Name,
            outputFormat = template.OutputFormat,
            local = true,
            fields = new
            {
                appId = (int?)null,
                depotId = (int?)null,
                manifestId = (string?)null,
                branch = "public",
                sizeBytes = (long?)null
            },
            note = "Local preview only. No DRM, licensing or authentication data is produced, downloaded or modified."
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Exports exactly what is on screen; the preview is generated first if it is empty.</summary>
    public async Task ExportAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!HasPreview) Generate();

        try
        {
            await File.WriteAllTextAsync(path, Preview, Encoding.UTF8);
            Status = $"Preview written to {Path.GetFileName(path)}.";
        }
        catch (Exception exception)
        {
            Status = $"The preview could not be written: {exception.Message}";
        }
    }
}
