using System.IO;
using System.Windows.Media;

namespace Steamy.Services;

/// <summary>Use user-supplied SF Pro locally; distributable builds retain the bundled fallback.</summary>
public static class AppTypography
{
    public static string LocalFontDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steamy", "Fonts", "SFPro");
    public static FontFamily Text { get; } = Resolve("SF Pro Text");
    public static FontFamily Display { get; } = Resolve("SF Pro Display");
    private static FontFamily Resolve(string name)
    {
        try
        {
            if (Directory.Exists(LocalFontDirectory))
            {
                // WPF resolves the requested family lazily; enumerating every face twice
                // before the first window is expensive, especially with a cold font cache.
                var regular = name.Replace(' ', '-') + "-Regular.otf";
                if (File.Exists(Path.Combine(LocalFontDirectory, regular)))
                    return new FontFamily(new Uri(LocalFontDirectory + Path.DirectorySeparatorChar), "./#" + name);
                foreach (var family in Fonts.GetFontFamilies(new Uri(LocalFontDirectory + Path.DirectorySeparatorChar)))
                    if (family.FamilyNames.Values.Contains(name, StringComparer.OrdinalIgnoreCase)) return family;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or FileFormatException) { }
        // Let WPF resolve this named family lazily, including the bundled fallback.
        // Enumerating every installed font here runs twice on the first UI load.
        return new FontFamily(new Uri("pack://application:,,,/Steamy;component/"), name + ", ./Resources/Fonts/#Google Sans");
    }
}
