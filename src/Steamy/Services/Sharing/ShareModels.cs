using System.Security.Cryptography;
using System.Text;

namespace Steamy.Services;

/// <summary>Where a shareable set of manifests comes from. When several apply, the first wins.</summary>
public enum ShareSourceKind
{
    /// <summary>A game installed in the local Steam library (appmanifest + depot cache).</summary>
    SteamLibrary,

    /// <summary>A Lua script (SteamTools, Steamy downloads) with the manifests it points to.</summary>
    Lua,

    /// <summary>Depot manifests in a cache folder that belong to no installed game and no Lua.</summary>
    Manifests
}

/// <summary>One file that goes into a share and the name it gets inside the archive.</summary>
public sealed record ShareFile(string FullPath, string EntryName, long Length);

/// <summary>An installed depot and the manifest Steam has on disk for it.</summary>
public sealed record DepotManifestRef(uint DepotId, string ManifestId, long Size);

/// <summary>
/// Everything that can be shared for one app from one place: a dump folder or the local Steam
/// library. Pure data, built by <see cref="ManifestLibraryScanner"/>.
/// </summary>
public sealed record ShareCandidate(
    int AppId,
    string Name,
    ShareSourceKind Source,
    string Folder,
    IReadOnlyList<ShareFile> Files,
    IReadOnlyList<DepotManifestRef> Depots,
    DateTime LastModifiedUtc)
{
    public long TotalBytes => Files.Sum(file => file.Length);

    public int ManifestCount => Files.Count(file => file.EntryName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase));

    public bool HasLua => Files.Any(file => file.EntryName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for a game with nothing local to pack yet: an installed game whose depot manifests
    /// Steam does not keep on disk, or an owned game the user has but never installed. The game
    /// still shows on the Share page; sharing it fetches the manifests from a source first.
    /// </summary>
    public bool NeedsManifestFetch => Files.Count == 0 && !HasLua;

    /// <summary>
    /// Identity of this exact set of files. Steam names manifests after their manifest id, so a
    /// game update yields new names and therefore a new fingerprint, while re-scanning the same
    /// files yields the same one. That is what "already shared" is decided on.
    /// </summary>
    public string Fingerprint { get; } = ComputeFingerprint(AppId, Source, Files);

    private static string ComputeFingerprint(int appId, ShareSourceKind source, IReadOnlyList<ShareFile> files)
    {
        var builder = new StringBuilder().Append(appId).Append('|').Append(source);
        foreach (var file in files.OrderBy(file => file.EntryName, StringComparer.OrdinalIgnoreCase))
            builder.Append('\n').Append(file.EntryName.ToLowerInvariant()).Append(':').Append(file.Length);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }
}

/// <summary>Owner, repository and branch that shares are committed to.</summary>
public sealed record GitHubTarget(string Owner, string Repo, string Branch)
{
    public override string ToString() => $"{Owner}/{Repo}";
}
