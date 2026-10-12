using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.TestTools;

namespace UnityPackageCatalog.Tests
{
    public class CatalogInspectorTests
    {
        [Test]
        public void MalformedPresetLogsToConsoleWithoutInspectorErrorOrRepeatedRenderLogs()
        {
            var path = "Assets/UPCInvalidPreset_" + Guid.NewGuid().ToString("N") + ".upcbuiltinjson";
            Editor editor = null;
            try
            {
                File.WriteAllText(path, "{\"schemaVersion\":1}");
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                editor = Editor.CreateEditor(AssetDatabase.LoadMainAssetAtPath(path));
                LogAssert.Expect(LogType.Error, new Regex(Regex.Escape("[UPC] " + path + ": A Built-in preset needs displayName and builtInModules.")));
                var root = editor.CreateInspectorGUI();
                Assert.That(root.Query<HelpBox>().ToList().Any(box => box.messageType == HelpBoxMessageType.Error), Is.False);
                Assert.That(root.Query<Label>().ToList().Any(label => (label.text ?? "").Contains("needs displayName")), Is.False);
                Assert.That(root.Query<TextField>().ToList().Any(field => field.label == "Repair preset"), Is.True);
                typeof(UpcBuiltInPresetEditor).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, null);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void ExternalPresetEditLogsSaveFailureAndPreservesFile()
        {
            var path = "Assets/UPCExternalPreset_" + Guid.NewGuid().ToString("N") + ".upcbuiltinjson";
            UpcBuiltInPresetEditor editor = null;
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(new BuiltInPresetDocument { displayName = "Original", builtInModules = Array.Empty<BuiltInModuleRule>() }));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                editor = (UpcBuiltInPresetEditor)Editor.CreateEditor(AssetDatabase.LoadMainAssetAtPath(path));
                var root = editor.CreateInspectorGUI();
                root.Q<TextField>().value = "Draft";
                var external = JsonUtility.ToJson(new BuiltInPresetDocument { displayName = "External", builtInModules = Array.Empty<BuiltInModuleRule>() });
                File.WriteAllText(path, external);
                LogAssert.Expect(LogType.Error, new Regex(Regex.Escape("[UPC] " + path + ": The asset changed externally.")));
                Assert.That(editor.TrySave(), Is.False);
                Assert.That(File.ReadAllText(path), Is.EqualTo(external));
                Assert.That(root.Query<Label>().ToList().Any(label => (label.text ?? "").Contains("changed externally")), Is.False);
            }
            finally
            {
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ImportedAssetsHaveValidScriptsAndInlineAuthoring(bool preset)
        {
            var path = "Assets/UPCInspector_" + Guid.NewGuid().ToString("N") + (preset ? ".upcbuiltinjson" : ".upcjson");
            Editor editor = null;
            EditorWindow host = null;
            try
            {
                var json = preset
                    ? JsonUtility.ToJson(new BuiltInPresetDocument { displayName = "Inspector", builtInModules = Array.Empty<BuiltInModuleRule>() })
                    : JsonUtility.ToJson(new CatalogDocument { displayName = "Inspector", packages = Array.Empty<CatalogEntry>() });
                File.WriteAllText(path, json);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(path);
                var importerScript = new SerializedObject(importer).FindProperty("m_Script")?.objectReferenceValue as MonoScript;
                Assert.That(importerScript, Is.Not.Null);
                Assert.That(importerScript.GetClass(), Is.EqualTo(importer.GetType()));
                var asset = AssetDatabase.LoadMainAssetAtPath(path) as ScriptableObject;
                Assert.That(MonoScript.FromScriptableObject(asset).GetClass(), Is.EqualTo(asset.GetType()));
                editor = Editor.CreateEditor(importer);
                Assert.That(editor, Is.InstanceOf<UpcAssetImporterEditor>());
                var root = editor.CreateInspectorGUI();
                Assert.That(root.Q<Button>(preset ? "save-preset" : "save-catalogue"), Is.Not.Null);
                Assert.That(root.Q<Button>(preset ? "apply-preset" : "add-asset-store"), Is.Not.Null);
                Assert.That(((UnityEditor.AssetImporters.ScriptedImporterEditor)editor).showImportedObject, Is.False);
                host = ScriptableObject.CreateInstance<EditorWindow>();
                host.Show();
                host.rootVisualElement.Add(root);
                var name = root.Q<TextField>();
                name.value = "Edited in Inspector";
                Assert.That(editor.hasUnsavedChanges, Is.True);
                editor.SaveChanges();
                var saved = preset ? BuiltInPresetDocument.Load(path).displayName : CatalogDocument.Load(path).displayName;
                Assert.That(saved, Is.EqualTo("Edited in Inspector"));
                Assert.That(editor.hasUnsavedChanges, Is.False);
            }
            finally
            {
                if (host != null) host.Close();
                if (editor != null) UnityEngine.Object.DestroyImmediate(editor);
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
