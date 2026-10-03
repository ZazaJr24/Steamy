// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

// Steamy fork addition, 2026-10-03.
using System;
using System.Collections.Generic;
using System.Linq;
using SteamKit2;

namespace DepotDownloader;

internal static class SteamyManifestValidation
{
    private const uint MaximumChunkBytes = 64 * 1024 * 1024;

    public static void Validate(DepotManifest manifest, uint requestedDepot, ulong requestedManifest, string installDirectory)
    {
        if (manifest == null || manifest.DepotID != requestedDepot || manifest.ManifestGID != requestedManifest)
            throw new SteamyDownloadException("manifest_mismatch", "Download blocked: Steam returned a manifest for a different depot or version.");
        if (manifest.Files == null || manifest.Files.Count > 2_000_000)
            throw new SteamyDownloadException("manifest_invalid", "Download blocked: the depot manifest is incomplete or too large.");

        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            var target = SteamyPaths.Resolve(installDirectory, file.FileName);
            var normalized = file.FileName.Replace('\\', '/');
            if (!names.Add(normalized) || file.TotalSize > long.MaxValue || file.FileHash == null || file.FileHash.Length != 20 || file.Chunks == null)
                throw new SteamyDownloadException("manifest_invalid", "Download blocked: the depot manifest contains conflicting or invalid file metadata.");

            ulong previousEnd = 0;
            foreach (var chunk in file.Chunks.OrderBy(chunk => chunk.Offset))
            {
                if (chunk.ChunkID == null || chunk.ChunkID.Length != 20 || chunk.CompressedLength == 0
                    || chunk.UncompressedLength == 0 || chunk.UncompressedLength > MaximumChunkBytes
                    || chunk.Offset > file.TotalSize || chunk.UncompressedLength > file.TotalSize - chunk.Offset
                    || chunk.Offset < previousEnd)
                    throw new SteamyDownloadException("manifest_invalid", "Download blocked: the depot manifest contains an invalid file range.");
                previousEnd = chunk.Offset + chunk.UncompressedLength;
            }
            _ = target;
        }
    }
}
