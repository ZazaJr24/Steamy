// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ProtoBuf;

namespace DepotDownloader
{
    [ProtoContract]
    class DepotConfigStore
    {
        [ProtoMember(1)]
        public Dictionary<uint, ulong> InstalledManifestIDs { get; private set; }

        string FileName;
        bool RecoveredFromBackup;

        DepotConfigStore()
        {
            InstalledManifestIDs = [];
        }

        static bool Loaded
        {
            get { return Instance != null; }
        }

        public static DepotConfigStore Instance;

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
                throw new Exception("Config already loaded");

            if (File.Exists(filename))
            {
                try { Instance = Read(filename); }
                catch (Exception) when (File.Exists(filename + ".bak"))
                {
                    try { Instance = Read(filename + ".bak"); Instance.RecoveredFromBackup = true; }
                    catch (Exception) { Instance = new DepotConfigStore(); ContentDownloader.Config.VerifyAll = true; }
                }
                catch (Exception) { Instance = new DepotConfigStore(); ContentDownloader.Config.VerifyAll = true; }
            }
            else
            {
                Instance = new DepotConfigStore();
            }

            Instance.FileName = filename;
        }

        private static DepotConfigStore Read(string filename)
        {
            using var fs = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ds = new DeflateStream(fs, CompressionMode.Decompress);
            return Serializer.Deserialize<DepotConfigStore>(ds) ?? throw new InvalidDataException("Invalid checkpoint.");
        }

        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");

            SteamyAtomicFile.Write(Instance.FileName, stream =>
            {
                using var ds = new DeflateStream(stream, CompressionMode.Compress, leaveOpen: true);
                Serializer.Serialize(ds, Instance);
            }, preserveBackup: !Instance.RecoveredFromBackup);
            Instance.RecoveredFromBackup = false;
        }
    }
}
