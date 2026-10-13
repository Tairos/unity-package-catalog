using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace UnityPackageCatalog
{
    [ScriptedImporter(3, "upcbuiltinjson")]
    public sealed class UpcBuiltInPresetImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var asset = ScriptableObject.CreateInstance<UpcBuiltInPresetAsset>();
            asset.json = File.ReadAllText(context.assetPath);
            context.AddObjectToAsset("preset", asset, CatalogSettings.BuiltInPresetIcon);
            context.SetMainObject(asset);
        }
    }
}
