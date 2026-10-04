using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace Steamy.Services;

/// <summary>Translates static interface labels without a key and caches results locally.</summary>
public sealed class UiTranslationService
{
    private static readonly HttpClient Http = CreateHttpClient(useProxy: true);
    private static readonly HttpClient DirectHttp = CreateHttpClient(useProxy: false);
    private static readonly SemaphoreSlim AnonymousTranslationSlots = new(3, 3);
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, TextState>> Originals = new();
    private static readonly ConditionalWeakTable<DependencyObject, ApplyState> ApplyStates = new();
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly string _cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "ui-translations.json");
    private bool _cacheLoaded;
    private bool _pageHandlerRegistered;

    public UiTranslationService(ISettingsService settings)
    {
        _settings = settings;
    }

    public void Observe(Window window)
    {
        if (!_pageHandlerRegistered)
        {
            EventManager.RegisterClassHandler(typeof(Page), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(PageLoaded), true);
            _pageHandlerRegistered = true;
        }

        window.Loaded += (_, _) => ScheduleApply(window);
    }

    public async Task ApplyToAsync(DependencyObject root, string? language = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var selected = language ?? _settings.Load().Language;
        var cultureName = UiLanguageCatalog.ResolveCultureName(selected);
        var culture = System.Globalization.CultureInfo.InstalledUICulture;
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            try { culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName); }
            catch (System.Globalization.CultureNotFoundException) { }
        }
        App.ApplyCulture(selected);
        if (root is FrameworkElement frameworkRoot)
        {
            try { frameworkRoot.Language = XmlLanguage.GetLanguage(culture.IetfLanguageTag); }
            catch (ArgumentException) { }
        }

        var target = TranslationCode(string.IsNullOrWhiteSpace(cultureName) ? culture.Name : cultureName);
        var applyState = ApplyStates.GetOrCreateValue(root);
        var requestVersion = Interlocked.Increment(ref applyState.Version);
        applyState.Target = target;
        var values = CollectStaticText(root);
        if (string.Equals(target, "en", StringComparison.OrdinalIgnoreCase))
        {
            RestoreEnglish(values);
            return;
        }

        await EnsureCacheLoadedAsync(token);
        ApplyAvailableTranslations(values, target);
        var missing = values.Select(item => item.Source)
            .Where(text => !_cache.ContainsKey(CacheKey(target, text)))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            try
            {
                await TranslateWithoutKeyAsync(missing, target, token, async (source, translated) =>
                {
                    await root.Dispatcher.InvokeAsync(() =>
                    {
                        if (applyState.Version != requestVersion || !string.Equals(applyState.Target, target, StringComparison.Ordinal)) return;
                        ApplyTranslation(values, source, translated);
                    });
                });
                await SaveCacheAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { /* Translation or cache failures never block page navigation. */ }
        }

        if (applyState.Version == requestVersion && string.Equals(applyState.Target, target, StringComparison.Ordinal))
            ApplyAvailableTranslations(values, target);
    }

    private static HttpClient CreateHttpClient(bool useProxy)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = useProxy }) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Steamy/0.6.4");
        return client;
    }

    private void CacheTranslation(string target, string source, string? translated)
    {
        if (!string.IsNullOrWhiteSpace(translated)) _cache[CacheKey(target, source)] = translated.Trim();
    }

    /// <summary>Uses the public web translator endpoint, so first-time translations need internet but no API key.</summary>
    private async Task TranslateWithoutKeyAsync(IReadOnlyList<string> source, string target, CancellationToken token,
        Func<string, string, Task>? onTranslation = null)
    {
        await Task.WhenAll(source.Select(async text =>
        {
            var translation = await TranslateWithoutKeyAsync(text, target, token);
            CacheTranslation(target, text, translation);
            if (!string.IsNullOrWhiteSpace(translation) && onTranslation is not null)
                await onTranslation(text, translation);
        }));
    }

    private static async Task<string?> TranslateWithoutKeyAsync(string source, string target, CancellationToken token)
    {
        await AnonymousTranslationSlots.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(7));
            var requestToken = timeout.Token;

            foreach (var host in new[] { "translate.googleapis.com", "translate.google.com" })
            {
                var uri = $"https://{host}/translate_a/single?client=gtx&sl=en&tl="
                    + Uri.EscapeDataString(target) + "&dt=t&q=" + Uri.EscapeDataString(source);
                using var response = await GetTranslationResponseAsync(uri, requestToken);
                if (response is null || !response.IsSuccessStatusCode) continue;

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(requestToken));
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0
                    || document.RootElement[0].ValueKind != JsonValueKind.Array) continue;
                var translated = new System.Text.StringBuilder();
                foreach (var segment in document.RootElement[0].EnumerateArray())
                    if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0
                        && segment[0].ValueKind == JsonValueKind.String)
                        translated.Append(segment[0].GetString());
                var result = translated.ToString().Trim();
                if (result.Length > 0 && !string.Equals(result, source, StringComparison.Ordinal)) return result;
            }

            if (!token.IsCancellationRequested && !timeout.IsCancellationRequested)
            {
                var memoryTranslation = await TranslateFromMemoryAsync(source, target, requestToken);
                if (!string.IsNullOrWhiteSpace(memoryTranslation)) return memoryTranslation;
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return null; }
        finally { AnonymousTranslationSlots.Release(); }
    }

    private static async Task<HttpResponseMessage?> GetTranslationResponseAsync(string uri, CancellationToken token)
    {
        HttpResponseMessage? response = null;
        try { response = await Http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, token); }
        catch (HttpRequestException) { }

        if (response?.IsSuccessStatusCode == true || (int?)response?.StatusCode == 429) return response;
        response?.Dispose();
        try { return await DirectHttp.GetAsync(uri, HttpCompletionOption.ResponseContentRead, token); }
        catch (HttpRequestException) { return null; }
    }

    private static async Task<string?> TranslateFromMemoryAsync(string source, string target, CancellationToken token)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(source) > 500) return null;
        var uri = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(source)
            + "&langpair=en%7C" + Uri.EscapeDataString(target);
        using var response = await GetTranslationResponseAsync(uri, token);
        if (response is null || !response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!document.RootElement.TryGetProperty("responseStatus", out var status) || status.GetInt32() != 200
            || !document.RootElement.TryGetProperty("responseData", out var data)
            || !data.TryGetProperty("translatedText", out var text)) return null;
        var translated = WebUtility.HtmlDecode(text.GetString() ?? string.Empty).Trim();
        return translated.Length == 0 || string.Equals(translated, source, StringComparison.Ordinal) ? null : translated;
    }

    private void PageLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is DependencyObject page) ScheduleApply(page);
    }

    private void ScheduleApply(DependencyObject root)
    {
        if (root.Dispatcher.HasShutdownStarted || root.Dispatcher.HasShutdownFinished) return;
        root.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(async () =>
            {
                try { await ApplyToAsync(root); }
                catch { /* Translation must never interrupt navigation or startup. */ }
            }));
    }

    private static string TranslationCode(string cultureName)
    {
        var normalized = cultureName.Replace('_', '-');
        if (normalized.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
        if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        var separator = normalized.IndexOf('-');
        return (separator < 0 ? normalized : normalized[..separator]).ToLowerInvariant();
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken token)
    {
        if (_cacheLoaded) return;
        await _cacheGate.WaitAsync(token);
        try
        {
            if (_cacheLoaded) return;
            if (File.Exists(_cachePath))
            {
                try
                {
                    await using var input = File.OpenRead(_cachePath);
                    var saved = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(input, cancellationToken: token);
                    if (saved is not null)
                        foreach (var pair in saved) _cache[pair.Key] = pair.Value;
                }
                catch { }
            }
            _cacheLoaded = true;
        }
        finally { _cacheGate.Release(); }
    }

    private async Task SaveCacheAsync(CancellationToken token)
    {
        await _cacheGate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporary = _cachePath + ".tmp";
            await using (var output = File.Create(temporary))
                await JsonSerializer.SerializeAsync(output, _cache, cancellationToken: token);
            File.Move(temporary, _cachePath, true);
        }
        finally { _cacheGate.Release(); }
    }

    private readonly record struct TextItem(DependencyObject Element, DependencyProperty Property, string Source);
    private sealed class TextState(string source, string translated)
    {
        public string Source { get; set; } = source;
        public string Translated { get; set; } = translated;
    }
    private sealed class ApplyState
    {
        public int Version;
        public string Target = "en";
    }

    private static List<TextItem> CollectStaticText(DependencyObject root)
    {
        var result = new List<TextItem>();
        var visited = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (!visited.Add(element)) continue;

            if (element is TextBlock) AddText(element, TextBlock.TextProperty, result);
            if (element is AccessText) AddText(element, AccessText.TextProperty, result);
            if (element is ContentControl and not ComboBoxItem) AddText(element, ContentControl.ContentProperty, result);
            if (element is HeaderedContentControl) AddText(element, HeaderedContentControl.HeaderProperty, result);
            if (element is FrameworkElement) AddText(element, FrameworkElement.ToolTipProperty, result);
            AddPlaceholder(element, result);

            try
            {
                var count = VisualTreeHelper.GetChildrenCount(element);
                for (var index = count - 1; index >= 0; index--) stack.Push(VisualTreeHelper.GetChild(element, index));
            }
            catch (InvalidOperationException) { }
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>()) stack.Push(child);
        }
        return result;
    }

    private static void AddText(DependencyObject element, DependencyProperty property, ICollection<TextItem> output)
    {
        if (BindingOperations.IsDataBound(element, property)) return;
        if (element.GetValue(property) is not string value || !LooksLikeUiText(value)) return;
        var states = Originals.GetOrCreateValue(element);
        if (states.TryGetValue(property, out var state))
        {
            if (string.Equals(value, state.Translated, StringComparison.Ordinal)) value = state.Source;
            else state.Source = value;
        }
        output.Add(new TextItem(element, property, value));
    }

    private static void AddPlaceholder(DependencyObject element, ICollection<TextItem> output)
    {
        var property = FindDependencyProperty(element.GetType(), "PlaceholderTextProperty");
        if (property is not null) AddText(element, property, output);
    }

    private static DependencyProperty? FindDependencyProperty(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null) is DependencyProperty property)
                return property;
        return null;
    }

    private static bool LooksLikeUiText(string value)
    {
        var text = value.Trim();
        if (text.Length is 0 or > 500 || text.Contains("{Binding", StringComparison.Ordinal)) return false;
        return text.Any(char.IsLetter);
    }

    private static void SetTranslated(DependencyObject element, DependencyProperty property, string source, string translated)
    {
        var states = Originals.GetOrCreateValue(element);
        if (!states.TryGetValue(property, out var state))
        {
            state = new TextState(source, source);
            states.Add(property, state);
        }
        else state.Source = source;
        element.SetCurrentValue(property, translated);
        state.Translated = translated;
    }

    private static void RestoreEnglish(IEnumerable<TextItem> values)
    {
        foreach (var item in values)
        {
            if (!Originals.TryGetValue(item.Element, out var states) || !states.TryGetValue(item.Property, out var state)) continue;
            var current = item.Element.GetValue(item.Property) as string;
            if (!string.Equals(current, state.Translated, StringComparison.Ordinal))
                state.Source = current ?? state.Source;
            item.Element.SetCurrentValue(item.Property, state.Source);
            state.Translated = state.Source;
        }
    }

    private static string CacheKey(string language, string text) => language + "\0" + text;

    private void ApplyAvailableTranslations(IEnumerable<TextItem> values, string target)
    {
        foreach (var item in values)
        {
            var translated = _cache.TryGetValue(CacheKey(target, item.Source), out var cached)
                ? cached
                : BuiltInTranslation(target, item.Source) ?? item.Source;
            SetTranslated(item.Element, item.Property, item.Source, translated);
        }
    }

    private static void ApplyTranslation(IEnumerable<TextItem> values, string source, string translated)
    {
        foreach (var item in values.Where(item => string.Equals(item.Source, source, StringComparison.Ordinal)))
            SetTranslated(item.Element, item.Property, source, translated);
    }

    private static string? BuiltInTranslation(string language, string source)
    {
        var table = language switch
        {
            "de" => German,
            "tr" => Turkish,
            "el" => Greek,
            _ => null
        };
        return table is not null && table.TryGetValue(source, out var translated) ? translated : null;
    }

    private static readonly IReadOnlyDictionary<string, string> German = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Select Language"] = "Sprache auswählen", ["Select Theme"] = "Design auswählen", ["API Keys"] = "API-Schlüssel",
        ["Select Folders"] = "Ordner auswählen", ["Download Settings"] = "Download-Einstellungen", ["Recovery"] = "Wiederherstellung",
        ["Required Files"] = "Benötigte Dateien", ["Continue"] = "Weiter", ["Finish"] = "Fertigstellen", ["Back"] = "Zurück",
        ["Dashboard"] = "Übersicht", ["Games"] = "Spiele", ["Downloads"] = "Downloads", ["Settings"] = "Einstellungen",
        ["Discover"] = "Entdecken", ["Tools"] = "Werkzeuge", ["Fixes"] = "Fehlerbehebungen", ["Library"] = "Bibliothek",
        ["Share"] = "Teilen", ["Search"] = "Suchen", ["Cancel"] = "Abbrechen", ["Save"] = "Speichern", ["Close"] = "Schließen",
        ["Refresh"] = "Aktualisieren", ["Browse"] = "Durchsuchen", ["Language"] = "Sprache", ["Theme"] = "Design",
        ["System"] = "System", ["Light"] = "Hell", ["Dark"] = "Dunkel", ["Select your language"] = "Sprache auswählen",
        ["Set up Steamy."] = "Steamy einrichten.",
        ["Choose your preferences one step at a time."] = "Richte Steamy Schritt für Schritt ein.",
        ["SETUP PREFERENCES"] = "EINRICHTUNG", ["YOUR SETUP"] = "DEINE EINRICHTUNG", ["Language and region"] = "Sprache und Region",
        ["Choose your preferred language"] = "Wähle deine bevorzugte Sprache", ["Appearance"] = "Design",
        ["API keys"] = "API-Schlüssel", ["Folders"] = "Ordner", ["Ready"] = "Bereit",
        ["PRIVATE BY DEFAULT"] = "STANDARDMÄSSIG PRIVAT", ["Your preferences stay on this PC."] = "Deine Einstellungen bleiben auf diesem PC.",
        ["Choose the language Steamy should use. You can change it later."] = "Wähle die Sprache für Steamy. Du kannst sie später ändern.",
        ["Pick a look that feels right. You can change it anytime in Settings."] = "Wähle ein Design. Du kannst es jederzeit in den Einstellungen ändern.",
        ["Add optional API keys now, or leave them blank and add them later."] = "Füge optionale API-Schlüssel jetzt hinzu oder später in den Einstellungen.",
        ["Set where Steamy finds Steam and saves downloaded files."] = "Lege fest, wo Steam gefunden und Downloads gespeichert werden.",
        ["Set speed, connection and retry preferences for downloads."] = "Lege Downloadgeschwindigkeit, Verbindungen und Wiederholungen fest.",
        ["Choose how Steamy verifies and resumes interrupted downloads."] = "Lege fest, wie Steamy Downloads prüft und fortsetzt.",
        ["Checking the downloader and required files for your first use."] = "Downloader und erforderliche Dateien werden geprüft.",
        ["Follow Windows"] = "Windows folgen", ["Bright surfaces"] = "Helle Oberflächen", ["Low-light surfaces"] = "Dunkle Oberflächen",
        ["Continue  ›"] = "Weiter  ›",
        ["Interface translations are fetched when needed and cached on this PC. No translation API key is required."] = "Oberflächenübersetzungen werden bei Bedarf geladen und auf diesem PC gespeichert. Ein Übersetzungs-API-Schlüssel ist nicht nötig.",
        ["FIRST TIME SETUP"] = "ERSTE EINRICHTUNG", ["STEAMY"] = "STEAMY"
    };

    // Keep the setup understandable before the online translation cache is populated.
    private static readonly IReadOnlyDictionary<string, string> Greek = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Select Language"] = "Επιλογή γλώσσας",
        ["Choose the language Steamy should use. You can change it later."] = "Επιλέξτε τη γλώσσα του Steamy. Μπορείτε να την αλλάξετε αργότερα.",
        ["SETUP PREFERENCES"] = "ΡΥΘΜΙΣΕΙΣ ΕΓΚΑΤΑΣΤΑΣΗΣ",
        ["LANGUAGE"] = "ΓΛΩΣΣΑ",
        ["Continue"] = "Συνέχεια",
        ["Back"] = "Πίσω",
        ["Skip"] = "Παράλειψη",
        ["Finish"] = "Τέλος",
        ["Interface translations are fetched when needed and cached on this PC. No translation API key is required."] = "Οι μεταφράσεις περιβάλλοντος λήψης αποθηκεύονται σε αυτή τη συσκευή. Δεν απαιτείται κλειδί API."
    };

    private static readonly IReadOnlyDictionary<string, string> Turkish = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Select Language"] = "Dil Seçin", ["Select Theme"] = "Tema Seçin", ["API Keys"] = "API Anahtarları",
        ["Select Folders"] = "Klasörleri Seçin", ["Download Settings"] = "İndirme Ayarları", ["Recovery"] = "Kurtarma",
        ["Required Files"] = "Gerekli Dosyalar", ["Continue"] = "Devam", ["Finish"] = "Bitir", ["Back"] = "Geri",
        ["Dashboard"] = "Kontrol Paneli", ["Games"] = "Oyunlar", ["Downloads"] = "İndirmeler", ["Settings"] = "Ayarlar",
        ["Discover"] = "Keşfet", ["Tools"] = "Araçlar", ["Fixes"] = "Düzeltmeler", ["Library"] = "Kütüphane",
        ["Share"] = "Paylaş", ["Search"] = "Ara", ["Cancel"] = "İptal", ["Save"] = "Kaydet", ["Close"] = "Kapat",
        ["Refresh"] = "Yenile", ["Browse"] = "Gözat", ["Language"] = "Dil", ["Theme"] = "Tema",
        ["System"] = "Sistem", ["Light"] = "Açık", ["Dark"] = "Koyu", ["Follow Windows"] = "Windows'u izleyin",
        ["Set up Steamy."] = "Steamy'yi kur.", ["Choose your preferences one step at a time."] = "Steamy'yi adım adım ayarla.",
        ["SETUP PREFERENCES"] = "KURULUM AYARLARI", ["YOUR SETUP"] = "KURULUMUN",
        ["Language and region"] = "Dil ve bölge", ["Choose your preferred language"] = "Tercih ettiğin dili seç",
        ["Appearance"] = "Görünüm", ["API keys"] = "API anahtarları", ["Folders"] = "Klasörler", ["Ready"] = "Hazır",
        ["PRIVATE BY DEFAULT"] = "GİZLİLİK ÖNCELİKLİ", ["Your preferences stay on this PC."] = "Tercihlerin bu bilgisayarda kalır.",
        ["A few quick choices. You can change them later in Settings."] = "Birkaç hızlı seçim. Bunları daha sonra Ayarlar'dan değiştirebilirsin.",
        ["Choose the language Steamy should use. You can change it later."] = "Steamy için dili seç. Daha sonra değiştirebilirsin.",
        ["Pick a look that feels right. You can change it anytime in Settings."] = "Görünümü seç. İstediğin zaman Ayarlar'dan değiştirebilirsin.",
        ["Add optional API keys now, or leave them blank and add them later."] = "İsteğe bağlı API anahtarlarını şimdi ya da daha sonra ekleyebilirsin.",
        ["Set where Steamy finds Steam and saves downloaded files."] = "Steam'in ve indirilen dosyaların konumunu seç.",
        ["Set speed, connection and retry preferences for downloads."] = "İndirme hızını, bağlantıları ve yeniden deneme ayarlarını seç.",
        ["Choose how Steamy verifies and resumes interrupted downloads."] = "İndirmelerin nasıl doğrulanacağını ve sürdürüleceğini seç.",
        ["Checking the downloader and required files for your first use."] = "İlk kullanım için indirici ve gerekli dosyalar denetleniyor.",
        ["Bright surfaces"] = "Aydınlık yüzeyler", ["Low-light surfaces"] = "Loş yüzeyler", ["Continue  ›"] = "Devam  ›",
        ["Interface translations are fetched when needed and cached on this PC. No translation API key is required."] = "Arayüz çevirileri gerektiğinde indirilir ve bu bilgisayarda önbelleğe alınır. Çeviri API anahtarı gerekmez.",
        ["FIRST TIME SETUP"] = "İLK KURULUM"
    };
}
