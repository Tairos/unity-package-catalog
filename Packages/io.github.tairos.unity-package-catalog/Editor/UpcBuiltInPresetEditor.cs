using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityPackageCatalog
{
    [CustomEditor(typeof(UpcBuiltInPresetAsset))]
    public sealed class UpcBuiltInPresetEditor : Editor
    {
        string original, baseline, message;
        BuiltInPresetDocument draft;
        VisualElement root;
        Label status;
        internal Action<bool> StateChanged;
        internal bool IsDirty => draft != null && JsonUtility.ToJson(draft) != baseline;
        string Path => AssetDatabase.GetAssetPath(target);

        [MenuItem("Assets/Create/Unity Built-in Preset", priority = 211)]
        public static void CreatePreset()
        {
            var selected = AssetDatabase.GetAssetPath(Selection.activeObject);
            var folder = AssetDatabase.IsValidFolder(selected) ? selected : System.IO.Path.GetDirectoryName(selected);
            if (string.IsNullOrEmpty(folder) || (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))) folder = "Assets";
            var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/New Built-in Preset.upcbuiltinjson");
            File.WriteAllText(path, JsonUtility.ToJson(new BuiltInPresetDocument { displayName = "New Built-in Preset", unityVersion = Application.unityVersion, builtInModules = Array.Empty<BuiltInModuleRule>() }, true));
            AssetDatabase.ImportAsset(path);
            var asset = AssetDatabase.LoadAssetAtPath<UpcBuiltInPresetAsset>(path);
            Selection.activeObject = asset; EditorGUIUtility.PingObject(asset);
            UpcAssetSelection.Select(asset);
        }

        public override VisualElement CreateInspectorGUI()
        {
            root = new VisualElement(); root.AddToClassList("upc-editor");
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(CatalogSettings.PackageRoot + "/Editor/CatalogEditor.uss"));
            ReloadDocument(); return root;
        }

        internal void ReloadDocument()
        {
            draft = null; message = null;
            try { original = File.ReadAllText(Path); draft = BuiltInPresetDocument.Parse(Path, original); baseline = JsonUtility.ToJson(draft); }
            catch (Exception error) { ReportError(error); }
            StateChanged?.Invoke(false); Render();
        }

        void Render()
        {
            root.Clear();
            Hint(Path); status = new Label(message ?? ""); status.AddToClassList("upc-wrap");
            if (draft == null)
            {
                Hint("Repair the preset below, then validate and save. See Console for details.");
                var repair = new TextField("Repair preset") { multiline = true, value = original }; repair.AddToClassList("upc-json"); root.Add(repair);
                root.Add(new Button(() => { try { Write(BuiltInPresetDocument.Parse(Path, repair.value)); } catch (Exception error) { ReportError(error); } }) { text = "Validate and save" });
                root.Add(status); return;
            }
            var name = new TextField("Preset name") { value = draft.displayName };
            name.RegisterValueChangedCallback(e => { draft.displayName = e.newValue; Changed(); }); root.Add(name);
            Hint("Created for Unity " + (string.IsNullOrEmpty(draft.unityVersion) ? "an unspecified version" : draft.unityVersion) + ". Running Unity " + Application.unityVersion + ".");
            Hint("Enable adds a direct project dependency; Disable removes it. Modules needed by other packages remain installed as dependencies. The Console lists results and explains any actions that cannot be applied.");
            root.Add(new Button(OpenPicker) { text = "Add Built-in module", name = "add-built-in" });
            var heading = new Label("Module actions (" + draft.builtInModules.Length + ")"); heading.AddToClassList("upc-section-title"); root.Add(heading);
            foreach (var rule in draft.builtInModules)
            {
                var row = new VisualElement(); row.AddToClassList("upc-card"); root.Add(row);
                var title = new Label(rule.name); title.AddToClassList("upc-title"); row.Add(title);
                var actions = new VisualElement(); actions.AddToClassList("upc-actions"); row.Add(actions);
                var action = new PopupField<string>("Action", new List<string> { "Enable", "Disable" }, rule.requirement == "excluded" ? 1 : 0);
                action.AddToClassList("upc-grow"); action.RegisterValueChangedCallback(e => { rule.requirement = e.newValue == "Disable" ? "excluded" : "required"; Changed(); }); actions.Add(action);
                actions.Add(new Button(() => { draft.builtInModules = draft.builtInModules.Where(item => item != rule).ToArray(); Render(); Changed(); }) { text = "Remove action" });
            }
            var footer = new VisualElement(); footer.AddToClassList("upc-actions"); root.Add(footer);
            footer.Add(new Button(() => TrySave()) { text = "Save preset", name = "save-preset" });
            footer.Add(new Button(ReloadDocument) { text = "Revert changes" });
            var preview = new Button(() => Run(false)) { text = "Preview changes", name = "preview-preset" }; footer.Add(preview);
            var apply = new Button(() => Run(true)) { text = "Apply preset", name = "apply-preset" }; footer.Add(apply);
            var operation = new Label(); operation.AddToClassList("upc-wrap"); root.Add(operation);
            operation.schedule.Execute(() =>
            {
                operation.text = BuiltInModuleRequirements.PresetPath == Path ? BuiltInModuleRequirements.Status : "";
                preview.SetEnabled(!BuiltInModuleRequirements.Busy && draft.builtInModules.Length > 0);
                apply.SetEnabled(!BuiltInModuleRequirements.Busy && draft.builtInModules.Length > 0);
            }).Every(250);
            root.Add(status);
        }

        void OpenPicker()
        {
            var owner = draft;
            CatalogItemPicker.Show(CatalogPickerKind.BuiltIn, draft.builtInModules.Select(rule => rule.name), (choices, action) =>
            {
                if (this == null || !ReferenceEquals(owner, draft)) throw new InvalidOperationException("The preset changed. Reopen the picker from the current preset.");
                var rules = new List<BuiltInModuleRule>(draft.builtInModules);
                foreach (var choice in choices) if (!rules.Any(rule => rule.name == choice.module)) rules.Add(new BuiltInModuleRule { name = choice.module, requirement = action });
                draft.builtInModules = rules.ToArray(); Render(); Changed();
            });
        }

        void Run(bool apply)
        {
            if (!TrySave()) return;
            try { if (apply) BuiltInModuleRequirements.Apply(Path); else BuiltInModuleRequirements.Check(Path); }
            catch (Exception error) { ReportError(error); }
        }
        void Hint(string text) { var hint = new Label(text); hint.AddToClassList("upc-subtitle"); root.Add(hint); }
        void Changed() { StateChanged?.Invoke(IsDirty); SetMessage(IsDirty ? "Unsaved changes" : ""); }
        void SetMessage(string value) { message = value; if (status != null) status.text = value; }
        void ReportError(Exception error)
        {
            BuiltInModuleRequirements.LogError(Path, error.Message);
            SetMessage("See Console for preset diagnostics.");
        }
        internal bool TrySave()
        {
            try { Write(draft); return true; }
            catch (Exception error) { ReportError(error); return false; }
        }
        void Write(BuiltInPresetDocument preset)
        {
            if (File.ReadAllText(Path) != original) throw new InvalidOperationException("The asset changed externally. Revert changes before saving.");
            BuiltInPresetDocument.Save(Path, preset);
            original = File.ReadAllText(Path); draft = BuiltInPresetDocument.Parse(Path, original); baseline = JsonUtility.ToJson(draft);
            AssetDatabase.ImportAsset(Path); message = "Preset saved."; StateChanged?.Invoke(false); Render();
        }
    }
}
