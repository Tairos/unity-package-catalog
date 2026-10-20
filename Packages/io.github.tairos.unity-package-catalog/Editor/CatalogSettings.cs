using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityPackageCatalog
{
    public static class CatalogSettings
    {
        internal const string PackageRoot = "Packages/io.github.tairos.unity-package-catalog";
        static string Key
        {
            get
            {
                using var hash = SHA256.Create();
                return "UnityPackageCatalog." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Application.dataPath)));
            }
        }
        internal static Texture2D Icon => AssetDatabase.LoadAssetAtPath<Texture2D>(PackageRoot + "/Editor/Icons/UpcCatalogIcon.png");
        internal static Texture2D BuiltInPresetIcon => AssetDatabase.LoadAssetAtPath<Texture2D>(PackageRoot + "/Editor/Icons/UpcBuiltInPresetIcon.png");
        public static bool Enabled => EditorPrefs.GetBool(Key + ".enabled", true);

        public static void SetEnabled(bool enabled)
        {
            NativeCatalogBridge.Detach();
            EditorPrefs.SetBool(Key + ".enabled", enabled);
            EditorPrefs.DeleteKey(Key + ".path");
            NativeCatalogBridge.Reload();
        }

        [MenuItem("Window/Package Management/Package Catalog Settings")]
        public static void OpenSettings() => SettingsService.OpenUserPreferences("Preferences/Package Catalog");

        [MenuItem("Window/Package Management/Package Catalog")]
        public static void OpenCatalog()
        {
            EditorApplication.ExecuteMenuItem("Window/Package Management/Package Manager");
            NativeCatalogBridge.RequestOpen();
        }

        [SettingsProvider]
        static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Preferences/Package Catalog", SettingsScope.User)
            {
                label = "Package Catalog",
                keywords = new System.Collections.Generic.HashSet<string>(new[] { "upcjson", "catalogue", "packages" }),
                activateHandler = (_, container) =>
                {
                    container.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(PackageRoot + "/Editor/CatalogEditor.uss"));
                    var scroll = new ScrollView(); scroll.AddToClassList("upc-scroll"); container.Add(scroll);
                    var root = new VisualElement(); root.AddToClassList("upc-settings"); scroll.Add(root);
                    var header = new VisualElement(); header.AddToClassList("upc-row");
                    var heading = new Label("Project catalogues"); heading.AddToClassList("upc-heading"); header.Add(heading);
                    var count = new Label(); count.AddToClassList("upc-badge"); header.Add(count); root.Add(header);
                    var list = new VisualElement { name = "catalogue-list" }; root.Add(list);
                    var validation = new HelpBox("", HelpBoxMessageType.Error); validation.AddToClassList("upc-hidden"); root.Add(validation);
                    var buttons = new VisualElement(); buttons.AddToClassList("upc-actions"); root.Add(buttons);
                    buttons.Add(new Button(UpcCatalogEditor.CreateCatalog) { text = "Create catalogue", name = "create-catalogue" });
                    buttons.Add(new Button(OpenCatalog) { text = "Open Package Manager" });
                    var hint = new Label("Select a catalogue above to locate its asset. Edit the selected asset in the Inspector.");
                    hint.AddToClassList("upc-subtitle"); root.Add(hint);
                    var enabled = new Toggle("Show catalogues in Package Manager") { value = Enabled };
                    enabled.AddToClassList("upc-setting"); enabled.RegisterValueChangedCallback(e => SetEnabled(e.newValue)); root.Add(enabled);
                    var presetHeading = new Label("Built-in presets"); presetHeading.AddToClassList("upc-section-title"); root.Add(presetHeading);
                    var presetList = new VisualElement { name = "preset-list" }; root.Add(presetList);
                    root.Add(new Button(UpcBuiltInPresetEditor.CreatePreset) { text = "Create Built-in preset" });
                    var presetHint = new Label("Apply a saved preset here, including presets in read-only packages. They do not add a Package Manager source. See Console for action details.");
                    presetHint.AddToClassList("upc-subtitle"); root.Add(presetHint);
                    string presetSignature = null;
                    string signature = null;
                    Action refresh = () =>
                    {
                        var presets = BuiltInPresetDocument.AssetPaths();
                        var presetCurrent = string.Join("|", presets.Select(path => path + "|" + File.GetLastWriteTimeUtc(path).Ticks));
                        if (presetCurrent != presetSignature)
                        {
                            presetSignature = presetCurrent; presetList.Clear();
                            if (presets.Length == 0) { var empty = new Label("No Built-in presets yet."); empty.AddToClassList("upc-empty"); presetList.Add(empty); }
                            foreach (var path in presets)
                            {
                                var asset = AssetDatabase.LoadAssetAtPath<UpcBuiltInPresetAsset>(path);
                                string title = Path.GetFileNameWithoutExtension(path), error = null;
                                BuiltInPresetDocument preset = null;
                                try { preset = BuiltInPresetDocument.Load(path); title = preset.displayName; } catch (Exception failure) { error = failure.Message; }
                                var row = new VisualElement(); row.AddToClassList("upc-catalogue-row"); presetList.Add(row);
                                var select = new Button(() => { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); });
                                select.AddToClassList("upc-catalogue-select"); row.Add(select);
                                var text = new VisualElement(); text.AddToClassList("upc-grow"); select.Add(text);
                                var name = new Label(title); name.AddToClassList("upc-title"); text.Add(name);
                                var location = new Label(path); location.AddToClassList("upc-subtitle"); text.Add(location);
                                if (error != null) { var warning = new Label("Needs attention"); warning.tooltip = error; warning.AddToClassList("upc-error"); text.Add(warning); }
                                row.Add(new Button(() => UpcAssetSelection.Select(asset)) { text = "Edit" });
                                var operation = new Label(); operation.AddToClassList("upc-wrap");
                                string runError = null;
                                var apply = new Button(() =>
                                {
                                    try { BuiltInModuleRequirements.Apply(path); runError = null; }
                                    catch (Exception failure) { runError = failure.Message; BuiltInModuleRequirements.LogError(path, runError); }
                                    operation.text = runError ?? BuiltInModuleRequirements.Status;
                                }) { text = "Apply preset", name = "apply-preset", tooltip = "Apply all saved module actions to this project without modifying the preset asset." };
                                row.Add(apply);
                                presetList.Add(operation);
                                Action refreshOperation = () =>
                                {
                                    apply.SetEnabled(preset != null && preset.builtInModules.Length > 0 &&
                                        !BuiltInModuleRequirements.Busy && !EditorApplication.isCompiling && !NativeCatalogBridge.IsPackageOperationInProgress());
                                    operation.text = runError ?? (BuiltInModuleRequirements.PresetPath == path ? BuiltInModuleRequirements.Status : "");
                                };
                                refreshOperation();
                                row.schedule.Execute(refreshOperation).Every(250);
                            }
                        }
                        var paths = CatalogRegistry.AssetPaths();
                        string problem = null;
                        try { CatalogRegistry.LoadSources(); }
                        catch (Exception error) { problem = error.Message; }
                        if (NativeCatalogBridge.Status.StartsWith("Integration stopped:", StringComparison.Ordinal)) problem = NativeCatalogBridge.Status;
                        validation.text = problem ?? ""; validation.EnableInClassList("upc-hidden", problem == null);
                        var current = string.Join("|", paths.Select(path => path + "|" + File.GetLastWriteTimeUtc(path).Ticks));
                        if (current == signature) return;
                        signature = current; list.Clear(); count.text = paths.Length.ToString();
                        if (paths.Length == 0)
                        {
                            var empty = new Label("No catalogues yet. Create a .upcjson asset to collect packages for this project.");
                            empty.AddToClassList("upc-empty"); list.Add(empty);
                        }
                        foreach (var path in paths)
                        {
                            var asset = AssetDatabase.LoadAssetAtPath<UpcCatalogAsset>(path);
                            string title = Path.GetFileNameWithoutExtension(path), error = null;
                            try { title = CatalogDocument.Load(path).displayName; }
                            catch (Exception entryError) { error = entryError.Message; }
                            var row = new VisualElement(); row.AddToClassList("upc-catalogue-row"); list.Add(row);
                            var select = new Button(() => { Selection.activeObject = asset; EditorGUIUtility.PingObject(asset); });
                            select.AddToClassList("upc-catalogue-select"); row.Add(select);
                            var icon = new Image { image = Icon }; icon.AddToClassList("upc-asset-icon"); select.Add(icon);
                            var text = new VisualElement(); text.AddToClassList("upc-grow"); select.Add(text);
                            var name = new Label(title); name.AddToClassList("upc-title"); text.Add(name);
                            var location = new Label(path); location.AddToClassList("upc-subtitle"); text.Add(location);
                            if (error != null) { var warning = new Label("Needs attention"); warning.tooltip = error; warning.AddToClassList("upc-error"); text.Add(warning); }
                            row.Add(new Button(() => UpcAssetSelection.Select(asset)) { text = "Edit" });
                        }
                    };
                    refresh();
                    root.schedule.Execute(refresh).Every(1000);
                }
            };
        }
    }
}
