using Frosty.Core.IO;
using Frosty.Core.Mod;
using Frosty.Hash;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using FrostySdk.Managers;
using FrostySdk.Resources;
using FrostySdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using BundleRefTablePlugin;

namespace BundleRefTablePlugin.Handlers
{
    public class BundleRefTableCustomActionHandler : ICustomActionHandler
    {
        // This is purely for the mod managers action view and has no impact on how the handler actually executes.
        // It tells the mod manager actions view what type of action this handler performs, wether it replaces (Modify)
        // data from one mod with another, or does it merge the two together.
        public HandlerUsage Usage => HandlerUsage.Merge;

        // A mod is comprised of a series of base resources, embedded, ebx, res, and chunks. Embedded are used internally
        // for the icon and images of a mod. Ebx/Res/Chunks are the core resources used for applying data to the game.
        // When you create a custom handler, you need to provide your own resources for your custom handled data. This
        // resource is unique however it is based on one of the three core types.
        private class BundleRefTableModResource : EditorModResource
        {
            // Defines which type of resource this resource is.
            public override ModResourceType Type => ModResourceType.Res;

            // The resType is vital to be kept
            private readonly uint m_resType;

            // these other two fields may have to be written to the mod as well
            private readonly ulong m_resRid;
            private readonly byte[] m_resMeta;

            // Creates a new resource of the specified type and adds its data to the mod manifest.
            public BundleRefTableModResource(ResAssetEntry entry, FrostyModWriter.Manifest manifest)
                : base(entry)
            {
                // obtain the modified data
                ModifiedResource md = entry.ModifiedEntry.DataObject as ModifiedResource;
                byte[] data = md.Save();

                // store data and details about resource
                name = entry.Name.ToLower();
                sha1 = Utils.GenerateSha1(data);
                resourceIndex = manifest.Add(sha1, data);
                size = data.Length;

                // set the handler hash to the hash of the ebx type name
                handlerHash = Fnv1.HashString(entry.Type.ToLower());

                // store res specific information
                m_resType = entry.ResType;
                m_resRid = entry.ResRid;
                m_resMeta = entry.ResMeta;
            }

            /// <summary>
            /// This method is calles when writing the mod. For Res Types it is vital that some additional information is persisted that is not written by the base method.
            /// Mainly that is the ResourceType as uint
            /// Additional data that is read, but I'm not sure whether it is actually necessary:
            /// <ul>
            /// <li>ResRid as ulong (not sure if this is really necessary, i.e., actually read)
            /// <li>resMeta length
            /// <li>resMeta as byte array
            /// </ul>
            /// </summary>
            /// <param name="writer"></param>
            public override void Write(NativeWriter writer)
            {
                // do all the regular writing for the base resource
                base.Write(writer);

                // write the res specific information
                writer.Write(m_resType);

                writer.Write(m_resRid);
                writer.Write((m_resMeta != null) ? m_resMeta.Length : 0);
                if (m_resMeta != null)
                {
                    writer.Write(m_resMeta);
                }
            }
        }

        // The below functions are specific to the editor, it is used to save the modified data to a mod.

        #region -- Editor Specific --

        // This function is for writing resources to the mod file, this is where you would add your custom
        // resources to be written.
        public void SaveToMod(FrostyModWriter writer, AssetEntry entry)
        {
            writer.AddResource(new BundleRefTableModResource(entry as ResAssetEntry, writer.ResourceManifest));
        }

        #endregion

        // The below functions are specific to the mod manager, it revolves around loading and potentially merging
        // of the data loaded from a mod.

        #region -- Mod Specific --

        // This function is for the mod managers action view, to allow a handler to describe detailed actions performed
        // format of the action string is <ResourceName>;<ResourceType>;<Action> where action can be Modify or Merge
        // and ResourceType can be Ebx,Res,Chunk @todo
        public IEnumerable<string> GetResourceActions(string name, byte[] data)
        {
            // @todo: implement something here to display the entries added to the BRT
            
            var newTable = ModifiedResource.Read(data) as ModifiedBundleRefTableResource;
            List<string> resourceActions = new List<string>();

            /*foreach (var value in newTable.Values)
            {
                string resourceName = name + " (Row: " + value.Row + "/Col: " + value.Column + ")";
                string resourceType = "ebx";
                string action = "Modify";

                resourceActions.Add(resourceName + ";" + resourceType + ";" + action);
            }*/

            return resourceActions;
        }

        // This function is invoked when a mod with such a handler is loaded, if a previous mod with a handler for this
        // particular asset was loaded previously, then existing will be populated with that data, allowing this function
        // the chance to merge the two datasets together
        public object Load(object existing, byte[] newData)
        {
            // load the existing modified data (from any previous mods)
            var oldTable = (ModifiedBundleRefTableResource)existing;

            // load the new modified data from the current mod
            ModifiedBundleRefTableResource newTable = (ModifiedBundleRefTableResource)ModifiedResource.Read(newData);

            // return the new data if there was no previous data
            if (oldTable == null)
                return newTable;

            // otherwise merge the two together by adding any non-duplicate keys from the new data to the old data
            foreach (string key in newTable.DuplicationDict.Keys)
            {
                oldTable.AddAsset(key, newTable.DuplicationDict[key]);
            }

            return oldTable;
        }

        // This function is invoked at the end of the mod loading, to actually modify the existing game data with the end
        // result of the mod loaded data, it also allows for a handler to add new Resources to be replaced.
        // ie. an Ebx handler might want to add a new Chunk resource that it is dependent on.
        public void Modify(AssetEntry origEntry, AssetManager am, RuntimeResources runtimeResources, object data, out byte[] outData)
        {
            // obtain the modified data that has been loaded and merged from the mods
            ModifiedBundleRefTableResource modifiedData = data as ModifiedBundleRefTableResource;

            // get the res asset entry for the BRT
            ResAssetEntry resAssetEntry = am.GetResEntry(origEntry.Name);

            // load the original res asset
            BundleRefTableResource resource = am.GetResAs<BundleRefTableResource>(resAssetEntry, modifiedData);

            // apply the changes to the resource
            resource.ApplyModifiedResource(modifiedData);

            // save the modified data to a byte array
            byte[] savedBytes = resource.SaveBytes();
            origEntry.OriginalSize = savedBytes.Length;
            outData = Utils.CompressFile(savedBytes);

            // update relevant asset entry values
            ((ResAssetEntry)origEntry).ResMeta = resource.ResourceMeta;
            origEntry.Size = outData.Length;
            origEntry.Sha1 = Utils.GenerateSha1(outData);
        }

        #endregion
    }
}
