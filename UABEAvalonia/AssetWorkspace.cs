﻿using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UABEAvalonia
{
    public class AssetWorkspace
    {
        public AssetsManager am { get; }
        public bool fromBundle { get; }

        public List<AssetsFileInstance> LoadedFiles { get; }
        public Dictionary<AssetID, AssetContainer> LoadedAssets { get; }

        public Dictionary<string, AssetsFileInstance> LoadedFileLookup { get; }

        public Dictionary<AssetID, AssetsReplacer> NewAssets { get; }
        public Dictionary<AssetID, Stream> NewAssetDatas { get; } //for preview in info window
        public HashSet<AssetID> RemovedAssets { get; }

        // we have to do this because we want to be able to tell
        // if all changes we've made have been removed so that
        // we can know not to save it. for example, if we removed
        // all replacers, we still need to save it if there were
        // changes to dependencies.
        public Dictionary<AssetsFileInstance, AssetsFileChangeTypes> OtherAssetChanges { get; }

        public bool Modified { get; set; }

        public delegate void AssetWorkspaceItemUpdateEvent(AssetsFileInstance file, AssetID assetId);
        public event AssetWorkspaceItemUpdateEvent? ItemUpdated;

        public AssetWorkspace(AssetsManager am, bool fromBundle)
        {
            this.am = am;
            this.fromBundle = fromBundle;

            LoadedFiles = new List<AssetsFileInstance>();
            LoadedAssets = new Dictionary<AssetID, AssetContainer>();

            LoadedFileLookup = new Dictionary<string, AssetsFileInstance>();

            NewAssets = new Dictionary<AssetID, AssetsReplacer>();
            NewAssetDatas = new Dictionary<AssetID, Stream>();
            RemovedAssets = new HashSet<AssetID>();

            OtherAssetChanges = new Dictionary<AssetsFileInstance, AssetsFileChangeTypes>();

            Modified = false;
        }

        public void AddReplacer(AssetsFileInstance forFile, AssetsReplacer replacer, Stream? previewStream = null)
        {
            AssetsFile assetsFile = forFile.file;
            AssetID assetId = new AssetID(forFile.path, replacer.GetPathID());

            if (NewAssets.ContainsKey(assetId))
                RemoveReplacer(forFile, NewAssets[assetId], true);

            NewAssets[assetId] = replacer;

            //make stream to use as a replacement to the one from file
            if (previewStream == null)
            {
                MemoryStream newStream = new MemoryStream();
                AssetsFileWriter newWriter = new AssetsFileWriter(newStream);
                replacer.Write(newWriter);
                newStream.Position = 0;
                previewStream = newStream;
            }
            NewAssetDatas[assetId] = previewStream;

            if (!(replacer is AssetsRemover))
            {
                AssetsFileReader reader = new AssetsFileReader(previewStream);
                AssetContainer cont = new AssetContainer(
                    reader, 0, replacer.GetPathID(), replacer.GetClassID(),
                    replacer.GetMonoScriptID(), (uint)previewStream.Length, forFile);

                LoadedAssets[assetId] = cont;
            }
            else
            {
                LoadedAssets.Remove(assetId);
            }

            if (ItemUpdated != null)
                ItemUpdated(forFile, assetId);

            Modified = true;
        }

        public void RemoveReplacer(AssetsFileInstance forFile, AssetsReplacer replacer, bool closePreviewStream = true)
        {
            AssetID assetId = new AssetID(forFile.path, replacer.GetPathID());

            if (NewAssets.ContainsKey(assetId))
            {
                NewAssets.Remove(assetId);
            }
            if (NewAssetDatas.ContainsKey(assetId))
            {
                if (closePreviewStream)
                    NewAssetDatas[assetId].Close();
                NewAssetDatas.Remove(assetId);
            }
            if (replacer is AssetsRemover && RemovedAssets.Contains(assetId))
                RemovedAssets.Remove(assetId);

            if (ItemUpdated != null)
                ItemUpdated(forFile, assetId);

            if (NewAssets.Count == 0 && !AnyOtherAssetChanges())
                Modified = false;
        }

        // todo: not very fast and this loop happens twice since it iterates again during write
        public HashSet<AssetsFileInstance> GetChangedFiles()
        {
            HashSet<AssetsFileInstance> changedFiles = new HashSet<AssetsFileInstance>();
            foreach (var newAsset in NewAssets)
            {
                AssetID assetId = newAsset.Key;
                string fileName = assetId.fileName;

                if (LoadedFileLookup.TryGetValue(fileName.ToLower(), out AssetsFileInstance? file))
                {
                    changedFiles.Add(file);
                }
            }

            foreach (var assetChangePair in OtherAssetChanges)
            {
                if (assetChangePair.Value != 0)
                    changedFiles.Add(assetChangePair.Key);
            }

            return changedFiles;
        }

        public void SetOtherAssetChangeFlag(AssetsFileInstance fileInst, AssetsFileChangeTypes changeTypes)
        {
            if (!OtherAssetChanges.ContainsKey(fileInst))
                OtherAssetChanges[fileInst] = AssetsFileChangeTypes.None;

            OtherAssetChanges[fileInst] |= changeTypes;
        }

        public void UnsetOtherAssetChangeFlag(AssetsFileInstance fileInst, AssetsFileChangeTypes changeTypes)
        {
            if (!OtherAssetChanges.ContainsKey(fileInst))
                OtherAssetChanges[fileInst] = AssetsFileChangeTypes.None;

            OtherAssetChanges[fileInst] &= ~changeTypes;

            if (OtherAssetChanges[fileInst] == AssetsFileChangeTypes.None)
                OtherAssetChanges.Remove(fileInst);
        }

        private bool AnyOtherAssetChanges()
        {
            foreach (var assetChangePair in OtherAssetChanges)
            {
                if (assetChangePair.Value != 0)
                    return true;
            }
            return false;
        }

        public void GenerateAssetsFileLookup()
        {
            foreach (AssetsFileInstance inst in LoadedFiles)
            {
                LoadedFileLookup[inst.path.ToLower()] = inst;
            }
        }

        public AssetTypeTemplateField GetTemplateField(AssetContainer cont, bool deserializeMono = true)
        {
            AssetsFileInstance fileInst = cont.FileInstance;
            AssetsFile file = fileInst.file;
            int type = cont.ClassId;
            ushort scriptIndex = cont.MonoId;

            int fixedId = AssetHelper.FixAudioID(type);
            bool hasTypeTree = file.Metadata.TypeTreeEnabled;

            AssetTypeTemplateField baseField = new AssetTypeTemplateField();
            if (hasTypeTree)
            {
                TypeTreeType typeTreeType = AssetHelper.FindTypeTreeTypeByID(file.Metadata, fixedId, scriptIndex);

                if (typeTreeType != null && typeTreeType.Nodes.Count > 0)
                    baseField.FromTypeTree(typeTreeType);
                else //fallback to cldb
                    baseField.FromClassDatabase(am.classDatabase, AssetHelper.FindAssetClassByID(am.classDatabase, fixedId));
            }
            else
            {
                if (type == (uint)AssetClassID.MonoBehaviour && deserializeMono)
                {
                    AssetsFileMetadata meta = cont.FileInstance.file.Metadata;
                    //check if typetree data exists already
                    if (!meta.TypeTreeEnabled || AssetHelper.FindTypeTreeTypeByScriptIndex(meta, cont.MonoId) == null)
                    {
                        //deserialize from dll (todo: ask user if dll isn't in normal location)
                        string filePath;
                        if (fileInst.parentBundle != null)
                            filePath = Path.GetDirectoryName(fileInst.parentBundle.path);
                        else
                            filePath = Path.GetDirectoryName(fileInst.path);

                        string managedPath = Path.Combine(filePath, "Managed");
                        if (Directory.Exists(managedPath))
                        {
                            return GetConcatMonoTemplateField(cont, managedPath);
                        }
                        //fallback to no mono deserialization for now
                    }
                }

                baseField.FromClassDatabase(am.classDatabase, AssetHelper.FindAssetClassByID(am.classDatabase, fixedId));
            }

            return baseField;
        }

        public AssetContainer GetAssetContainer(AssetsFileInstance fileInst, int fileId, long pathId, bool onlyInfo = true)
        {
            if (fileId != 0)
            {
                fileInst = fileInst.GetDependency(am, fileId - 1);
            }

            if (fileInst != null)
            {
                AssetID assetId = new AssetID(fileInst.path, pathId);
                if (LoadedAssets.TryGetValue(assetId, out AssetContainer? cont))
                {
                    if (!onlyInfo && !cont.HasValueField)
                    {
                        AssetTypeTemplateField tempField = GetTemplateField(cont);
                        AssetTypeValueField baseField = tempField.MakeValue(cont.FileReader, cont.FilePosition);
                        cont = new AssetContainer(cont, baseField);
                    }
                    return cont;
                }
            }
            return null;
        }

        public AssetContainer GetAssetContainer(AssetsFileInstance fileInst, AssetTypeValueField pptrField, bool onlyInfo = true)
        {
            int fileId = pptrField["m_FileID"].AsInt;
            long pathId = pptrField["m_PathID"].AsLong;
            return GetAssetContainer(fileInst, fileId, pathId, onlyInfo);
        }

        public AssetTypeValueField? GetBaseField(AssetContainer cont)
        {
            if (cont.HasValueField)
                return cont.BaseValueField;

            cont = GetAssetContainer(cont.FileInstance, 0, cont.PathId, false);
            if (cont != null)
                return cont.BaseValueField;
            else
                return null;
        }

        public AssetTypeValueField? GetBaseField(AssetsFileInstance fileInst, int fileId, long pathId)
        {
            AssetContainer? cont = GetAssetContainer(fileInst, fileId, pathId, false);
            if (cont != null)
                return GetBaseField(cont);
            else
                return null;
        }

        public AssetTypeValueField? GetBaseField(AssetsFileInstance fileInst, AssetTypeValueField pptrField)
        {
            int fileId = pptrField["m_FileID"].AsInt;
            long pathId = pptrField["m_PathID"].AsLong;

            AssetContainer? cont = GetAssetContainer(fileInst, fileId, pathId, false);
            if (cont != null)
                return GetBaseField(cont);
            else
                return null;
        }

        public AssetTypeValueField GetConcatMonoBaseField(AssetContainer cont, string managedPath)
        {
            AssetTypeTemplateField baseTemp = GetConcatMonoTemplateField(cont, managedPath);
            return baseTemp.MakeValue(cont.FileReader, cont.FilePosition);
        }

        public AssetTypeTemplateField GetConcatMonoTemplateField(AssetContainer cont, string managedPath)
        {
            AssetsFile file = cont.FileInstance.file;
            AssetTypeTemplateField baseTemp = GetTemplateField(cont, false);

            ushort scriptIndex = cont.MonoId;
            if (scriptIndex != 0xFFFF)
            {
                AssetTypeValueField baseField = baseTemp.MakeValue(cont.FileReader, cont.FilePosition);

                AssetContainer monoScriptCont = GetAssetContainer(cont.FileInstance, baseField["m_Script"], false);
                if (monoScriptCont == null)
                    return baseTemp;

                AssetTypeValueField scriptBaseField = monoScriptCont.BaseValueField;
                if (scriptBaseField == null)
                    return baseTemp;

                string scriptClassName = scriptBaseField["m_ClassName"].AsString;
                string scriptNamespace = scriptBaseField["m_Namespace"].AsString;
                string assemblyName = scriptBaseField["m_AssemblyName"].AsString;
                string assemblyPath = Path.Combine(managedPath, assemblyName);

                if (scriptNamespace != string.Empty)
                    scriptClassName = scriptNamespace + "." + scriptClassName;

                if (!File.Exists(assemblyPath))
                    return baseTemp;

                MonoCecilTempGenerator mc = new MonoCecilTempGenerator(assemblyPath);

                baseTemp = mc.GetTemplateField(baseTemp, assemblyName, scriptNamespace, scriptClassName, new UnityVersion(file.Metadata.UnityVersion));
            }
            return baseTemp;
        }
    }
}
