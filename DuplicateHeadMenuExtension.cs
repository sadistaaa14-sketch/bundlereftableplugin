using BundleRefTablePlugin.Windows;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Managers.Entries;
using FrostySdk.Resources;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;

namespace BundleRefTablePlugin
{
    public class DuplicateHeadMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName => "Tools";
        public override string SubLevelMenuName => null;
        public override string MenuItemName => "Duplicate Head";
        public override ImageSource Icon => null;

        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            EbxAssetEntry selectedEntry = App.SelectedAsset;
            if (selectedEntry == null)
            {
                FrostyMessageBox.Show("No asset selected. Select an asset inside the head folder you want to duplicate.", "Frosty Editor");
                return;
            }

            string sourceFolder = selectedEntry.Path;
            if (string.IsNullOrEmpty(sourceFolder))
            {
                FrostyMessageBox.Show("Selected asset has no folder path.", "Frosty Editor");
                return;
            }

            DuplicateHeadWindow win = new DuplicateHeadWindow(sourceFolder);
            if (win.ShowDialog() != true)
                return;

            string newFolder = win.NewFolder;
            string hostFolder = win.HostFolder;

            FrostyTaskWindow.Show("Duplicating Head", "", (task) =>
            {
                try
                {
                    DuplicateHead(task, sourceFolder, newFolder, hostFolder);
                }
                catch (Exception ex)
                {
                    App.Logger.Log("Error duplicating head: " + ex.Message);
                }
            });

            App.EditorWindow.DataExplorer.RefreshAll();
        });

        private void DuplicateHead(FrostyTaskWindow task, string sourceFolder, string newFolder, string hostFolder)
        {
            // Phase 1: Find all EBX assets in the source folder
            task.Update("Finding assets in source folder...");
            List<EbxAssetEntry> sourceAssets = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (entry.Path.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase))
                {
                    sourceAssets.Add(entry);
                }
            }

            if (sourceAssets.Count == 0)
            {
                App.Logger.Log("No assets found in folder: " + sourceFolder);
                return;
            }

            App.Logger.Log($"Found {sourceAssets.Count} assets in {sourceFolder}");

            // Map from old asset name to new asset entry (for cross-reference updating)
            Dictionary<string, EbxAssetEntry> oldToNewMap = new Dictionary<string, EbxAssetEntry>();

            // Phase 2: Duplicate each EBX asset
            int current = 0;
            foreach (EbxAssetEntry sourceEntry in sourceAssets)
            {
                current++;
                string newName = newFolder + "/" + sourceEntry.Filename;
                task.Update($"Duplicating {sourceEntry.Filename} ({current}/{sourceAssets.Count})...");

                EbxAssetEntry newEntry = DuplicateEbxAsset(sourceEntry, newName);
                if (newEntry != null)
                {
                    oldToNewMap[sourceEntry.Name] = newEntry;
                    App.Logger.Log($"Duplicated EBX: {sourceEntry.Name} -> {newEntry.Name}");
                }
            }

            // Phase 3: For each new EBX, duplicate linked resources (textures, meshes)
            current = 0;
            foreach (var kvp in oldToNewMap)
            {
                current++;
                EbxAssetEntry originalEntry = App.AssetManager.GetEbxEntry(kvp.Key);
                EbxAssetEntry newEntry = kvp.Value;
                task.Update($"Duplicating resources for {newEntry.Filename} ({current}/{oldToNewMap.Count})...");

                DuplicateLinkedResources(originalEntry, newEntry);
            }

            // Phase 4: Update cross-references within the new assets
            task.Update("Updating cross-references...");
            UpdateCrossReferences(oldToNewMap);

            // Phase 5: Update BRT entries
            task.Update("Updating BRT entries...");
            UpdateBrtEntries(sourceFolder, newFolder, hostFolder, oldToNewMap);

            App.Logger.Log($"Head duplication complete: {sourceFolder} -> {newFolder}");
        }

        /// <summary>
        /// Deep-clones an EBX asset to a new name, generating new GUIDs.
        /// </summary>
        private EbxAssetEntry DuplicateEbxAsset(EbxAssetEntry entry, string newName)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);

            // Deep clone via serialize/deserialize
            EbxAsset newAsset;
            using (EbxBaseWriter writer = EbxBaseWriter.CreateWriter(new MemoryStream(), EbxWriteFlags.DoNotSort))
            {
                writer.WriteAsset(asset);
                byte[] buf = writer.ToByteArray();
                using (EbxReader reader = EbxReader.CreateReader(new MemoryStream(buf)))
                    newAsset = reader.ReadAsset<EbxAsset>();
            }

            newAsset.SetFileGuid(Guid.NewGuid());

            dynamic obj = newAsset.RootObject;
            obj.Name = newName;

            AssetClassGuid guid = new AssetClassGuid(
                Utils.GenerateDeterministicGuid(newAsset.Objects, (Type)obj.GetType(), newAsset.FileGuid), -1);
            obj.SetInstanceGuid(guid);

            EbxAssetEntry newEntry = App.AssetManager.AddEbx(newName, newAsset);

            newEntry.AddedBundles.AddRange(entry.EnumerateBundles());
            newEntry.ModifiedEntry.DependentAssets.AddRange(newAsset.Dependencies);

            return newEntry;
        }

        /// <summary>
        /// Duplicates res and chunk resources linked to an EBX asset (textures, meshes, etc.)
        /// </summary>
        private void DuplicateLinkedResources(EbxAssetEntry originalEntry, EbxAssetEntry newEntry)
        {
            EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);
            dynamic newRoot = newAsset.RootObject;

            EbxAsset origAsset = App.AssetManager.GetEbx(originalEntry);
            dynamic origRoot = origAsset.RootObject;

            bool modified = false;
            Type rootType = origRoot.GetType();

            // Handle TextureBaseAsset (textures have a Resource property pointing to a res)
            if (TypeLibrary.IsSubClassOf(rootType, "TextureBaseAsset"))
            {
                modified = DuplicateTextureResources(originalEntry, newEntry, origRoot, newRoot, newAsset);
            }
            // Handle MeshAsset (meshes have a MeshSetResource property)
            else if (TypeLibrary.IsSubClassOf(rootType, "MeshAsset"))
            {
                modified = DuplicateMeshResources(originalEntry, newEntry, origRoot, newRoot, newAsset);
            }

            if (modified)
            {
                App.AssetManager.ModifyEbx(newEntry.Name, newAsset);
            }
        }

        private bool DuplicateTextureResources(EbxAssetEntry origEntry, EbxAssetEntry newEntry,
            dynamic origRoot, dynamic newRoot, EbxAsset newAsset)
        {
            try
            {
                ResAssetEntry resEntry = App.AssetManager.GetResEntry((ulong)origRoot.Resource);
                if (resEntry == null)
                    return false;

                Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);
                ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(texture.ChunkId);

                // Duplicate the chunk
                ChunkAssetEntry newChunkEntry = DuplicateChunk(chunkEntry);

                // Duplicate the res
                ResAssetEntry newResEntry = DuplicateRes(resEntry, newEntry.Name, ResourceType.Texture);
                if (newResEntry == null)
                    return false;

                newRoot.Resource = newResEntry.ResRid;
                Texture newTexture = App.AssetManager.GetResAs<Texture>(newResEntry);
                newTexture.ChunkId = newChunkEntry.Id;
                newTexture.AssetNameHash = (uint)Utils.HashString(newResEntry.Name, true);

                newResEntry.LinkAsset(newChunkEntry);
                newEntry.LinkAsset(newResEntry);

                App.AssetManager.ModifyRes(newResEntry.Name, newTexture);

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.Log($"Warning: Could not duplicate texture resources for {origEntry.Name}: {ex.Message}");
                return false;
            }
        }

        private bool DuplicateMeshResources(EbxAssetEntry origEntry, EbxAssetEntry newEntry,
            dynamic origRoot, dynamic newRoot, EbxAsset newAsset)
        {
            try
            {
                ResAssetEntry resEntry = App.AssetManager.GetResEntry((ulong)origRoot.MeshSetResource);
                if (resEntry == null)
                    return false;

                // Duplicate the res at the byte level (we don't have MeshSet class access)
                ResAssetEntry newResEntry = DuplicateRes(resEntry, newEntry.Name, (ResourceType)resEntry.ResType);
                if (newResEntry == null)
                    return false;

                newRoot.MeshSetResource = newResEntry.ResRid;

                // Try to update NameHash if the property exists
                try
                {
                    newRoot.NameHash = (uint)Utils.HashString(newEntry.Name.ToLower());
                }
                catch { }

                newEntry.LinkAsset(newResEntry);

                // Duplicate chunks referenced by the mesh res
                // We read the res data to find chunk references, but since we don't have MeshSet,
                // we just link the original chunks for now
                // TODO: Full mesh chunk duplication requires MeshSetPlugin reference

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.Log($"Warning: Could not duplicate mesh resources for {origEntry.Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates references within the new assets to point to other new assets
        /// (e.g., if a blueprint in the folder referenced a texture in the same folder,
        /// update it to reference the new duplicated texture).
        /// </summary>
        private void UpdateCrossReferences(Dictionary<string, EbxAssetEntry> oldToNewMap)
        {
            foreach (var kvp in oldToNewMap)
            {
                EbxAssetEntry newEntry = kvp.Value;
                EbxAsset newAsset = App.AssetManager.GetEbx(newEntry);

                bool modified = false;

                // Check all objects in the EBX for references to old assets
                foreach (object obj in newAsset.Objects)
                {
                    // Look through all properties for PointerRef types that reference old assets
                    foreach (var prop in obj.GetType().GetProperties())
                    {
                        if (prop.PropertyType == typeof(PointerRef))
                        {
                            try
                            {
                                PointerRef pref = (PointerRef)prop.GetValue(obj);
                                if (pref.Type == PointerRefType.External)
                                {
                                    // Check if this references an old asset we duplicated
                                    EbxAssetEntry refEntry = App.AssetManager.GetEbxEntry(pref.External.FileGuid);
                                    if (refEntry != null && oldToNewMap.ContainsKey(refEntry.Name))
                                    {
                                        EbxAssetEntry newRefEntry = oldToNewMap[refEntry.Name];
                                        EbxAsset newRefAsset = App.AssetManager.GetEbx(newRefEntry);

                                        // Create new pointer ref to the duplicated asset
                                        prop.SetValue(obj, new PointerRef(
                                            new EbxImportReference
                                            {
                                                FileGuid = newRefAsset.FileGuid,
                                                ClassGuid = pref.External.ClassGuid
                                            }));
                                        modified = true;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (modified)
                {
                    App.AssetManager.ModifyEbx(newEntry.Name, newAsset);
                    App.Logger.Log($"Updated cross-references in {newEntry.Name}");
                }
            }
        }

        /// <summary>
        /// Updates BRT entries for the new duplicated assets.
        /// For each new asset, creates a BRT entry pointing to the host bundle.
        /// </summary>
        private void UpdateBrtEntries(string sourceFolder, string newFolder, string hostFolder,
            Dictionary<string, EbxAssetEntry> oldToNewMap)
        {
            // Find all BRT res entries
            foreach (ResAssetEntry resEntry in App.AssetManager.EnumerateRes((uint)ResourceType.BundleRefTableResource))
            {
                BundleRefTableResource brt = App.AssetManager.GetResAs<BundleRefTableResource>(resEntry);
                if (brt == null)
                    continue;

                bool brtModified = false;

                // For each old->new asset mapping, check if the old asset exists in this BRT
                foreach (var kvp in oldToNewMap)
                {
                    string oldAssetPath = kvp.Key;
                    string newAssetPath = kvp.Value.Name;

                    if (brt.DupeAsset(newAssetPath, oldAssetPath))
                    {
                        brtModified = true;
                        App.Logger.Log($"Added BRT entry: {newAssetPath} (based on {oldAssetPath})");
                    }
                }

                if (brtModified)
                {
                    App.Logger.Log($"Updated BRT: {resEntry.Name}");
                }
            }
        }

        #region -- Chunk and Res Duplication Helpers --

        private static ChunkAssetEntry DuplicateChunk(ChunkAssetEntry entry, Texture texture = null)
        {
            byte[] random = new byte[16];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                while (true)
                {
                    rng.GetBytes(random);
                    random[15] |= 1;
                    if (App.AssetManager.GetChunkEntry(new Guid(random)) == null)
                        break;
                }
            }

            Guid newGuid;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetChunk(entry)))
            {
                newGuid = App.AssetManager.AddChunk(reader.ReadToEnd(), new Guid(random), texture,
                    entry.EnumerateBundles().ToArray());
            }

            return App.AssetManager.GetChunkEntry(newGuid);
        }

        private static ResAssetEntry DuplicateRes(ResAssetEntry entry, string name, ResourceType resType)
        {
            if (App.AssetManager.GetResEntry(name) != null)
            {
                App.Logger.Log($"Res already exists: {name}");
                return null;
            }

            ResAssetEntry newEntry;
            using (NativeReader reader = new NativeReader(App.AssetManager.GetRes(entry)))
            {
                newEntry = App.AssetManager.AddRes(name, resType, entry.ResMeta,
                    reader.ReadToEnd(), entry.EnumerateBundles().ToArray());
            }
            return newEntry;
        }

        #endregion
    }
}
