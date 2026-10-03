// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace DepotDownloader
{
    static class DepotKeyStore
    {
        private static Dictionary<uint, byte[]> depotKeysCache = new Dictionary<uint, byte[]>();

        public static void AddAll(string[] values)
        {
            var parsed = new Dictionary<uint, byte[]>();
            foreach (string value in values)
            {
                var split = value?.Split(';');
                if (split == null || split.Length != 2
                    || !uint.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out var depotId) || depotId == 0
                    || split[1].Length != 64 || split[1].Any(c => !Uri.IsHexDigit(c))
                    || !parsed.TryAdd(depotId, StringToByteArray(split[1])))
                    throw new FormatException("Invalid or duplicate depot key entry; expected one unique depot ID and a 64-character hexadecimal key.");
            }
            foreach (var entry in parsed)
                if (depotKeysCache.ContainsKey(entry.Key))
                    throw new FormatException("Duplicate depot key entry.");
            foreach (var entry in parsed) depotKeysCache.Add(entry.Key, entry.Value);
        }

        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                .Where(x => x % 2 == 0)
                .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                .ToArray();
        }

        public static bool ContainsKey(uint depotId)
        {
            return depotKeysCache.ContainsKey(depotId);
        }

        public static byte[] Get(uint depotId)
        {
            return depotKeysCache[depotId];
        }


    }
}
