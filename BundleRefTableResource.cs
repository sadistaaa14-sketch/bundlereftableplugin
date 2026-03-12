using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FrostySdk.Managers.Entries;
using Frosty.Controls;
using System;
using FrostySdk;
using System.Windows.Documents;
using System.Collections.Generic;
using Frosty.Core;

namespace BundleRefTablePlugin
{
    public static class BRTUtils
    {
        /// <summary>
        /// Returns true if the current game profile uses the legacy BRT format
        /// with 32-bit FNV1 hashes in asset lookups (e.g. FIFA 17).
        /// </summary>
        public static bool IsLegacyBrtFormat => ProfilesLibrary.DataVersion <= (int)ProfileVersion.Fifa17;

        public static string ReadString(NativeReader reader, ulong stringPos)
        {
            long currentPos = reader.Position;

            reader.Position = (long)stringPos;

            string stringData = reader.ReadNullTerminatedString();

            reader.Position = currentPos;

            return stringData;
        }

        /// <summary>
        /// Standard FNV-1 32-bit hash used by FIFA 17 BRT asset lookups.
        /// </summary>
        public static uint Fnv1Hash32(string s)
        {
            uint h = 0x811c9dc5;
            byte[] bytes = Encoding.ASCII.GetBytes(s.ToLower());
            foreach (byte b in bytes)
            {
                h = unchecked(h * 0x01000193);
                h ^= b;
            }
            return h;
        }

        /// <summary>
        /// Returns the appropriate hash for an asset path based on the current game profile.
        /// </summary>
        public static ulong HashAssetPath(string path)
        {
            if (IsLegacyBrtFormat)
                return Fnv1Hash32(path);
            else
                return FNV64StringHash.Fnv64String8(path, FNV64StringHash.CharCase.Lower);
        }
    }


    public class BundleRefTableResource : Resource
    {
        public string Name;
        public Guid InstanceGuid;

        public ModifiedBundleRefTableResource modResource = null;

        public ulong assetLookupPtr;
        public ulong bundleRefPtr;
        public ulong assetsPtr;
        public ulong bundlesPtr;

        public uint assetLookupCount;
        public uint bundleRefCount;
        public uint assetCount;
        public uint bundleCount = 0;
        public uint unkHash;

        public List<BundleRef> bundleRefs;
        public List<AssetLookup> assetLookups;
        public List<Asset> assets;
        public List<Bundle> bundles;

        public class AssetLookup
        {
            public ulong Hash { get; set; }
            public uint BundleRefIndex { get; set; }
            public uint AssetIndex { get; set; }

            public AssetLookup()
            {
                Hash = 0;
                BundleRefIndex = 0;
                AssetIndex = 0;
            }

            public AssetLookup(NativeReader reader)
            {
                if (BRTUtils.IsLegacyBrtFormat)
                    Hash = reader.ReadUInt();
                else
                    Hash = reader.ReadULong();
                BundleRefIndex = reader.ReadUInt();
                AssetIndex = reader.ReadUInt();
            }

            public void Write(NativeWriter writer)
            {
                if (BRTUtils.IsLegacyBrtFormat)
                    writer.Write((uint)Hash);
                else
                    writer.Write(Hash);
                writer.Write(BundleRefIndex);
                writer.Write(AssetIndex);
            }
        }

        public class Asset
        {
            public string Name { get; set; }
            public string Path { get; set; }

            public Asset()
            {
                Name = "";
                Path = "";
            }

            public Asset(NativeReader reader)
            {
                Name = BRTUtils.ReadString(reader, reader.ReadULong());
                Path = BRTUtils.ReadString(reader, reader.ReadULong());
            }

            public void Write(NativeWriter writer, Dictionary<string, ulong> stringMap)
            {
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write(stringMap[Path.ToLower()]);
            }
        }

        public class BundleRef
        {
            public string Name { get; set; }
            public string Directory { get; set; }
            public uint BundleIndex { get; set; }

            public BundleRef()
            {
                Name = "";
                Directory = "";
                BundleIndex = 0;
            }

            public BundleRef(NativeReader reader, ulong bundlesPtr)
            {
                Name = BRTUtils.ReadString(reader, reader.ReadULong());
                Directory = BRTUtils.ReadString(reader, reader.ReadULong());
                BundleIndex = (uint)(reader.ReadULong() - bundlesPtr) / 16;
            }

            public void Write(NativeWriter writer, Dictionary<string, ulong> stringMap, uint bundleOffset)
            {
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write(stringMap[Directory.ToLower()]);
                writer.Write((ulong)bundleOffset + (BundleIndex * 16));
            }
        }

        public class Bundle
        {
            public string Name { get; set; }
            public uint ParentBundleIndex { get; set; }

            public Bundle()
            {
                Name = "";
                ParentBundleIndex = 0;
            }

            public Bundle(NativeReader reader, ulong bundlesPtr)
            {
                Name = BRTUtils.ReadString(reader, reader.ReadULong());
                ParentBundleIndex = (uint)(reader.ReadULong() - bundlesPtr) / 16;
            }

            public void Write(NativeWriter writer, Dictionary<string, ulong> stringMap, uint bundleOffset)
            {
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write((ulong)bundleOffset + (ParentBundleIndex * 16));
            }
        }


        public BundleRefTableResource()
        {

        }

        /// <summary>
        /// Loads a Frostbite BundleRefTable resource from a stream.
        /// </summary>
        /// <param name="s"></param>
        public override void Read(NativeReader reader, AssetManager am, ResAssetEntry entry, ModifiedResource modifiedData)
        {
            // Ensure the meta is read
            base.Read(reader, am, entry, modifiedData);

            if(modifiedData != null && modifiedData is ModifiedBundleRefTableResource)
            {
                modResource = modifiedData as ModifiedBundleRefTableResource;
            }

            // Pointer to name string in string table
            Name = BRTUtils.ReadString(reader, reader.ReadULong());

            // Data pointers
            assetLookupPtr = reader.ReadULong();
            bundleRefPtr = reader.ReadULong();
            assetsPtr = reader.ReadULong();
            bundlesPtr = reader.ReadULong();

            // Empty string offset
            reader.Position += 8;

            // M25+ resources contain the primary instance GUID of the BRT
            if (ProfilesLibrary.DataVersion >= (int)ProfileVersion.Madden25)
            {
                InstanceGuid = reader.ReadGuid();

                // Empty bytes
                reader.Position += 16;
            }

            // Counts
            assetLookupCount = reader.ReadUInt();
            bundleRefCount = reader.ReadUInt();
            assetCount = reader.ReadUInt();


            reader.Position += 4; // 0

            // Unknown hash, seems to always be the same
            unkHash = reader.ReadUInt();

            reader.Position += 4; // 0
            reader.Position += 4; // 1
            reader.Position += 4; // 0



            // Read all bundlerefs and determine bundleCount based on the highest bundle index referenced
            reader.Position = (long)bundleRefPtr;
            bundleRefs = new List<BundleRef>();
            for (int i = 0; i < bundleRefCount; i++)
            {
                bundleRefs.Add(new BundleRef(reader, bundlesPtr));
                bundleCount = Math.Max(bundleRefs[i].BundleIndex + 1, bundleCount);
            }

            // Read all assets
            reader.Position = (long)assetsPtr;
            assets = new List<Asset>();
            for (int i = 0; i < assetCount; i++)
            {
                assets.Add(new Asset(reader));
            }

            // Read all asset lookups
            reader.Position = (long)assetLookupPtr;
            assetLookups = new List<AssetLookup>();
            for (int i = 0; i < assetLookupCount; i++)
            {
                assetLookups.Add(new AssetLookup(reader));
            }

            // Read all bundles
            reader.Position = (long)bundlesPtr;
            bundles = new List<Bundle>();
            for (int i = 0; i < bundleCount; i++)
            {
                bundles.Add(new Bundle(reader, bundlesPtr));
            }
        }

        public override byte[] SaveBytes()
        {
            // We use this to map every string to its offset in the string table. THis will be fully populated after we write the string table
            Dictionary<string, ulong> stringMap = new Dictionary<string, ulong>
            {
                { "", 0 },
                { Name, 0 }
            };

            // Add every string from every bundleref to the string map
            for (int i = 0; i < bundleRefs.Count; i++)
            {
                if (!stringMap.ContainsKey(bundleRefs[i].Name.ToLower()))
                    stringMap.Add(bundleRefs[i].Name.ToLower(), 0);
                if (!stringMap.ContainsKey(bundleRefs[i].Directory.ToLower()))
                    stringMap.Add(bundleRefs[i].Directory.ToLower(), 0);
            }

            // Add every string from every asset to the string map
            for (int i = 0; i < assets.Count; i++)
            {
                if (!stringMap.ContainsKey(assets[i].Name.ToLower()))
                    stringMap.Add(assets[i].Name.ToLower(), 0);

                if (!stringMap.ContainsKey(assets[i].Path.ToLower()))
                    stringMap.Add(assets[i].Path.ToLower(), 0);
            }

            // Note: Asset lookups don't contain any strings, so we don't go through them here

            // Add every string from every bundle to the string map
            for (int i = 0; i < bundles.Count; i++)
            {
                if (!stringMap.ContainsKey(bundles[i].Name.ToLower()))
                    stringMap.Add(bundles[i].Name.ToLower(), 0);
            }

            using (NativeWriter writer = new NativeWriter(new MemoryStream()))
            {
                // Write some placeholder empty pointers for now
                writer.Write((ulong)0); // Name pointer
                writer.Write((ulong)0); // Asset lookups pointer
                writer.Write((ulong)0); // Bundle refs pointer
                writer.Write((ulong)0); // Assets pointer
                writer.Write((ulong)0); // Bundles pointer

                writer.Write((ulong)0); // Empty string pointer

                // Write GUID for games that use it
                if (ProfilesLibrary.DataVersion >= (int)ProfileVersion.Madden25)
                {
                    writer.Write(InstanceGuid);
                    writer.Position += 0x10;
                }

                // Write the counts
                writer.Write(assetLookups.Count);
                writer.Write(bundleRefs.Count);
                writer.Write(assets.Count);

                writer.Position += 4; // 0

                // Write the unknown hash back (always the same, so no need to change it)
                writer.Write(unkHash);

                writer.Position += 4; // 0
                writer.Write((uint)1); // 1
                writer.Position += 4; // 0

                writer.WritePadding(16); // Pad to 16 byte interval

                // Write string table
                List<string> keys = new List<string>(stringMap.Keys);

                // Write each string and store its offset in the string map
                foreach (string data in keys)
                {
                    stringMap[data] = (ulong)writer.Position;
                    writer.WriteNullTerminatedString(data);
                }

                // Pad to 16 byte interval
                writer.WritePadding(16);

                // With the size of the string table now known, we can calculate the remaining offsets easily using some math
                // because we know the size of each entry of each section and how many entries are in each section
                int assetLookupEntrySize = BRTUtils.IsLegacyBrtFormat ? 12 : 16;
                ulong newBundleRefsOffset = (ulong)writer.Position;
                ulong newAssetOffset = newBundleRefsOffset + (ulong)(24 * bundleRefs.Count);
                ulong newAssetLookupOffset = newAssetOffset + (ulong)(16 * assets.Count);
                ulong assetLookupEnd = newAssetLookupOffset + (ulong)(assetLookupEntrySize * assetLookups.Count);
                // Align bundles section to 16-byte boundary (needed for legacy format with 12-byte lookups)
                ulong newBundleOffset = (assetLookupEnd + 15) & ~15UL;

                // Then we can go back and write the offsets we left blank before
                long curPos = writer.Position;
                writer.Position = 0;
                writer.Write(stringMap[Name.ToLower()]);
                writer.Write(newAssetLookupOffset);
                writer.Write(newBundleRefsOffset);
                writer.Write(newAssetOffset);
                writer.Write(newBundleOffset);

                writer.Write(stringMap[""]);

                writer.Position = curPos;

                // Write the bundle refs
                for (int i = 0; i < bundleRefs.Count; i++)
                {
                    bundleRefs[i].Write(writer, stringMap, (uint)newBundleOffset);
                }

                // Write the assets
                for (int i = 0; i < assets.Count; i++)
                {
                    assets[i].Write(writer, stringMap);
                }

                // Sort the asset lookups by hash (this is required as the game seems to use some kind of binary search to look these up)
                assetLookups.Sort((a, b) => a.Hash.CompareTo(b.Hash));

                // Write the asset lookups
                for (int i = 0; i < assetLookups.Count; i++)
                {
                    assetLookups[i].Write(writer);
                }

                // Pad to 16-byte boundary before bundles section
                writer.WritePadding(16);

                // Write the bundles
                for (int i = 0; i < bundleCount; i++)
                {
                    bundles[i].Write(writer, stringMap, (uint)newBundleOffset);
                }

                // Now it's time to write the reloc table, so store the offset where it starts
                ulong relocTableOffset = (ulong)writer.Position;

                // Use a list of pointer offsets, starting with the static ones that are part of the header
                List<uint> pointerLocations = new List<uint>();
                pointerLocations.Add(0x00);
                pointerLocations.Add(0x08);
                pointerLocations.Add(0x10);
                pointerLocations.Add(0x18);
                pointerLocations.Add(0x20);
                pointerLocations.Add(0x28);

                // Every bundleref has 3 pointers
                for (int i = 0; i < bundleRefs.Count; i++)
                {
                    pointerLocations.Add((uint)(newBundleRefsOffset + (ulong)(i * 24)));
                    pointerLocations.Add((uint)(newBundleRefsOffset + (ulong)(i * 24)) + 8);
                    pointerLocations.Add((uint)(newBundleRefsOffset + (ulong)(i * 24)) + 16);
                }

                // Every asset has 2 pointers
                for (int i = 0; i < assets.Count; i++)
                {
                    pointerLocations.Add((uint)(newAssetOffset + (ulong)(i * 16)));
                    pointerLocations.Add((uint)(newAssetOffset + (ulong)(i * 16)) + 8);
                }

                // Note: because the asset lookups use indices rather than pointers, we don't need to add them here

                // Every bundle has 2 pointers
                for (int i = 0; i < bundles.Count; i++)
                {
                    pointerLocations.Add((uint)(newBundleOffset + (ulong)(i * 16)));
                    pointerLocations.Add((uint)(newBundleOffset + (ulong)(i * 16)) + 8);
                }

                // Write each pointer location to the reloc table
                foreach (uint pointerLocation in pointerLocations)
                {
                    writer.Write(pointerLocation);
                }

                // Store the size of the reloc table
                uint relocTableSize = (uint)(writer.Position - (long)relocTableOffset);


                // First 4 bytes of the res meta should be the reloc table offset
                byte[] relocTableOffsetArray = System.BitConverter.GetBytes(relocTableOffset);

                // Next 4 bytes should be the reloc table size
                byte[] relocTableSizeArray = System.BitConverter.GetBytes(relocTableSize);

                // Write the reloc table offset and size to the resource meta
                relocTableOffsetArray.CopyTo(resMeta, 0);
                relocTableSizeArray.CopyTo(resMeta, 4);

                return writer.ToByteArray();
            }
        }

        // This function is not currently used, I wrote it before I knew how handlers worked
        public void Merge(BundleRefTableResource victim)
        {
            // Crappy but largely effective way to check if we're working with the same base BRT
            if (victim.Name != Name)
            {
                throw new Exception("Cannot merge different BRTs");
            }

            // Iterate through each asset lookup in the victim
            for (int i = 0; i < victim.assetLookups.Count; i++)
            {
                // Check if the asset lookup already exists in the current BRT
                bool exists = false;
                for (int j = 0; j < assetLookups.Count; j++)
                {
                    if (assetLookups[j].Hash == victim.assetLookups[i].Hash)
                    {
                        exists = true;
                        break;
                    }
                }
                // If it doesn't exist, add it to the current BRT
                if (!exists)
                {
                    assetLookups.Add(victim.assetLookups[i]);

                    uint bundleRefIndex = victim.assetLookups[i].BundleRefIndex;
                    uint assetIndex = victim.assetLookups[i].AssetIndex;

                    string originalName = victim.bundleRefs[(int)bundleRefIndex].Name;
                    string originalDir = victim.bundleRefs[(int)bundleRefIndex].Directory;

                    // Check if an identical bundle ref already exists in the current BRT. If so, use it, otherwise, add this new one
                    bool bundleRefExists = false;
                    for (int j = 0; j < bundleRefs.Count; j++)
                    {
                        if (bundleRefs[j].Name == originalName && bundleRefs[j].Directory == originalDir)
                        {
                            bundleRefExists = true;
                            assetLookups[assetLookups.Count - 1].BundleRefIndex = (uint)j;
                            break;
                        }
                    }

                    if (!bundleRefExists)
                    {
                        bundleRefs.Add(victim.bundleRefs[(int)bundleRefIndex]);
                        assetLookups[assetLookups.Count - 1].BundleRefIndex = (uint)(bundleRefs.Count - 1);
                    }

                    string originalAssetName = victim.assets[(int)assetIndex].Name;
                    string originalAssetPath = victim.assets[(int)assetIndex].Path;

                    // Check if an identical asset already exists in the current BRT. If so, use it, otherwise, add this new one
                    bool assetExists = false;
                    for (int j = 0; j < assets.Count; j++)
                    {
                        if (assets[j].Name == originalAssetName && assets[j].Path == originalAssetPath)
                        {
                            assetExists = true;
                            assetLookups[assetLookups.Count - 1].AssetIndex = (uint)j;
                            break;
                        }
                    }

                    if (!assetExists)
                    {
                        assets.Add(victim.assets[(int)assetIndex]);
                        assetLookups[assetLookups.Count - 1].AssetIndex = (uint)(assets.Count - 1);
                    }

                }
            }
        }

        // Takes in a ModifiedResource and applies the changes to the BRT resource
        public void ApplyModifiedResource(ModifiedResource inModResource)
        {
            modResource = inModResource as ModifiedBundleRefTableResource;

            // Add a new entry for every duplicated asset
            foreach(KeyValuePair<string, string> kvp in modResource.DuplicationDict)
            {
                AddDupeEntry(kvp.Key, kvp.Value);
            }
        }

        public override ModifiedResource SaveModifiedResource()
        {
            return modResource;
        }

        public bool DupeAsset(string newAssetPath, string existingAssetPath)
        {
            // Create a new modified resource if we don't have one already
            if (modResource == null)
            {
                modResource = new ModifiedBundleRefTableResource();
            }

            // Add the new to old asset mapping to the modified resource
            return modResource.AddAsset(newAssetPath, existingAssetPath);
        }

        public bool RevertDupe(string newAssetPath)
        {
            if (modResource == null)
            {
                return false;
            }

            return modResource.RemoveAsset(newAssetPath);
        }

        public bool AddDupeEntry(string newAssetPath, string existingAssetPath)
        {
            // Get hashes for the old asset path, new asset path, and new asset filename
            ulong oldHash = BRTUtils.HashAssetPath(existingAssetPath);
            ulong newHashFull = BRTUtils.HashAssetPath(newAssetPath);
            ulong newHashName = BRTUtils.HashAssetPath(newAssetPath.Substring(newAssetPath.LastIndexOf("/") + 1));

            // Check if the new asset path already exists in the asset lookups
            List<int> indicesToRemove = new List<int>();
            for (int i = 0; i < assetLookups.Count; i++)
            {
                if (assetLookups[i].Hash == newHashFull)
                {
                    indicesToRemove.Add(i);
                }
                else if (assetLookups[i].Hash == newHashName)
                {
                    indicesToRemove.Add(i);
                }
                
            }

            foreach(int i in indicesToRemove)
            {
                assetLookups.RemoveAt(i);
            }

            // Find the old hash in the asset lookups
            for (int i = 0; i < assetLookups.Count; i++)
            {
                if (assetLookups[i].Hash == oldHash)
                {                    
                    // Create a new asset lookup with the new hash and the same bundle ref index as the old one
                    AssetLookup newAssetLookup = new AssetLookup();
                    AssetLookup newAssetLookupNameOnly = new AssetLookup();
                    newAssetLookup.Hash = newHashFull;
                    newAssetLookupNameOnly.Hash = newHashName;
                    newAssetLookup.BundleRefIndex = assetLookups[i].BundleRefIndex;
                    newAssetLookupNameOnly.BundleRefIndex = assetLookups[i].BundleRefIndex;

                    Asset newAsset = new Asset();
                    newAsset.Name = newAssetPath.Substring(newAssetPath.LastIndexOf("/") + 1);
                    newAsset.Path = newAssetPath.Substring(0, newAssetPath.LastIndexOf("/")).Trim('/');

                    // Check if the new asset already exists in the assets list
                    bool found = false;
                    for (int j = 0; j < assets.Count; j++)
                    {
                        if (assets[j].Name == newAsset.Name && assets[j].Path == newAsset.Path)
                        {
                            // If it exists, set the asset index to the existing asset's index
                            newAssetLookup.AssetIndex = (uint)j;
                            newAssetLookupNameOnly.AssetIndex = (uint)j;
                            found = true;
                        }
                    }

                    // If it doesn't exist, add it to the assets list
                    if (!found)
                    {
                        assets.Add(newAsset);
                        newAssetLookup.AssetIndex = (uint)(assets.Count - 1);
                        newAssetLookupNameOnly.AssetIndex = (uint)(assets.Count - 1);
                    }


                    assetLookups.Add(newAssetLookup);
                    assetLookups.Add(newAssetLookupNameOnly);
                    return true;
                }
            }

            return false;
        }
    }

    public class ModifiedBundleRefTableResource : ModifiedResource
    {
        public Dictionary<string, string> DuplicationDict => newAssetMapping;

        // stores modifications as a dictionary of new asset path to old asset path
        private Dictionary<string, string> newAssetMapping = new Dictionary<string, string>();

        public bool AddAsset(string newAsset, string oldAsset)
        {
            if(!newAssetMapping.ContainsKey(newAsset))
            {
                newAssetMapping.Add(newAsset, oldAsset);

                return true;
            }

            return false;
        }

        public bool RemoveAsset(string newAsset)
        {
            if (newAssetMapping.ContainsKey(newAsset))
            {
                newAssetMapping.Remove(newAsset);
                return true;
            }
            return false;
        }

        // This function is responsible for reading in the modified data from the project file
        public override void ReadInternal(NativeReader reader)
        {
            int count = reader.ReadInt();
            for (int i = 0; i < count; i++)
            {
                string newAsset = reader.ReadNullTerminatedString();
                string oldAsset = reader.ReadNullTerminatedString();

                EbxAssetEntry existingNewEntry = App.AssetManager.GetEbxEntry(newAsset.ToLower());

                if (existingNewEntry != null && !existingNewEntry.IsAdded)
                {
                    continue;
                }

                newAssetMapping.Add(newAsset, oldAsset);
            }
        }

        // This function is responsible for writing out the modified data to the project file
        public override void SaveInternal(NativeWriter writer)
        {
            writer.Write(newAssetMapping.Count);
            foreach (string key in newAssetMapping.Keys)
            {
                writer.WriteNullTerminatedString(key);
                writer.WriteNullTerminatedString(newAssetMapping[key]);
            }
        }
    }
}
