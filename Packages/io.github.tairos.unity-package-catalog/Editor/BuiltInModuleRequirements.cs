using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace UnityPackageCatalog
{
    [InitializeOnLoad]
    public static class BuiltInModuleRequirements
    {
        const string PendingKey = "UnityPackageCatalog.PendingModuleRules";
        const string StatusKey = "UnityPackageCatalog.ModuleStatus";
        static SearchRequest search;
        static ListRequest list;
        static AddAndRemoveRequest change;
        static ModulePackageState[] available;
        static BuiltInModuleRule[] rules;
        static bool apply, verifying;
        static string versionNotice;
        static string[] skipped = Array.Empty<string>();
        static int skippedCount;

        [Serializable]
        internal sealed class PendingOperation
        {
            public BuiltInModuleRule[] builtInModules;
            public string[] skipped;
            public int skippedCount;
        }
        public static string PresetPath => SessionState.GetString("UnityPackageCatalog.ModulePresetPath", "");
        static double deadline;
        public static bool Busy => search != null || list != null || change != null;
        public static string Status => SessionState.GetString(StatusKey, "No module operation requested.");

        static BuiltInModuleRequirements()
        {
            EditorApplication.update += Poll;
            // Enabling/disabling modules may reload scripts before the Request can finish.
            // Verify the saved rule snapshot after reload; never repeat the mutation.
            if (!string.IsNullOrEmpty(SessionState.GetString(PendingKey, "")))
                EditorApplication.delayCall += ResumeVerification;
        }

        public static void Check(string presetPath) => Begin(presetPath, false);
        public static void Apply(string presetPath) => Begin(presetPath, true);

        static void Begin(string presetPath, bool shouldApply)
        {
            if (Busy || CatalogImport.Busy || EditorApplication.isCompiling || NativeCatalogBridge.IsPackageOperationInProgress())
                throw new InvalidOperationException("Wait for the current Package Manager operation or compilation to finish.");
            if (!string.IsNullOrEmpty(SessionState.GetString(PendingKey, "")))
            {
                if (shouldApply) throw new InvalidOperationException("Preview changes to verify the previous operation before applying again.");
                ResumeVerification();
                return;
            }
            var preset = BuiltInPresetDocument.Load(presetPath);
            rules = preset.builtInModules;
            SessionState.SetString("UnityPackageCatalog.ModulePresetPath", presetPath);
            versionNotice = preset.VersionNotice;
            apply = shouldApply;
            verifying = false;
            skipped = Array.Empty<string>(); skippedCount = 0;
            if (rules.Length == 0) { SetStatus("This preset has no built-in module actions."); return; }
            SetStatus("Checking available built-in modules and project dependencies…");
            deadline = EditorApplication.timeSinceStartup + 240;
            search = Client.SearchAll(true);
        }

        static ModulePackageState[] Snapshot(PackageInfo[] packages) => packages.Select(p => new ModulePackageState
        {
            name = p.name, version = p.version, builtIn = p.source == PackageSource.BuiltIn,
            direct = p.isDirectDependency,
            dependencies = (p.dependencies ?? Array.Empty<DependencyInfo>()).Select(d => d.name).ToArray()
        }).ToArray();

        static void ResumeVerification()
        {
            var json = SessionState.GetString(PendingKey, "");
            if (string.IsNullOrEmpty(json) || Busy) return;
            var pending = JsonUtility.FromJson<PendingOperation>(json);
            rules = pending.builtInModules;
            skipped = pending.skipped ?? Array.Empty<string>();
            skippedCount = pending.skippedCount;
            verifying = true;
            SetStatus("Verifying preset actions after package resolution…");
            deadline = EditorApplication.timeSinceStartup + 240;
            list = Client.List(true, true);
        }

        static void Poll()
        {
            if (!Busy || EditorApplication.isCompiling) return;
            try
            {
                if (EditorApplication.timeSinceStartup > deadline)
                    throw new TimeoutException("Module operation timed out. Preview changes again before retrying; Unity may still be resolving packages.");
                if (search != null)
                {
                    if (!search.IsCompleted) return;
                    if (search.Status != StatusCode.Success) throw new InvalidOperationException(search.Error?.message ?? "Could not discover built-in modules.");
                    available = Snapshot(search.Result);
                    search = null;
                    list = Client.List(true, true);
                    return;
                }
                if (change != null)
                {
                    if (!change.IsCompleted) return;
                    if (change.Status != StatusCode.Success)
                    {
                        SessionState.EraseString(PendingKey);
                        throw new InvalidOperationException(change.Error?.message ?? "Unity could not apply the Built-in preset.");
                    }
                    change = null;
                    ResumeVerification();
                    return;
                }
                if (list == null || !list.IsCompleted) return;
                if (list.Status != StatusCode.Success) throw new InvalidOperationException(list.Error?.message ?? "Could not inspect installed packages.");
                var installed = Snapshot(list.Result.ToArray());
                list = null;
                if (verifying)
                {
                    ReportResults(rules, installed, skipped, skippedCount);
                    SessionState.EraseString(PendingKey);
                    return;
                }
                var plan = BuiltInModulePlan.Create(rules, available, installed);
                if (!string.IsNullOrEmpty(versionNotice))
                    Debug.LogWarning("[UPC] " + PresetPath + ": " + versionNotice, AssetDatabase.LoadMainAssetAtPath(PresetPath));
                var actionable = rules.Where(rule => !plan.blocked.Contains(rule.name)).ToArray();
                if (!apply)
                {
                    var satisfied = BuiltInModulePlan.SatisfiedActions(actionable, installed);
                    Debug.Log("[UPC] " + PresetPath + ": Preview changes.\n" +
                        "Enable explicitly: " + (plan.add.Length == 0 ? "none" : string.Join(", ", plan.add)) +
                        "\nRemove explicit dependencies: " + (plan.remove.Length == 0 ? "none" : string.Join(", ", plan.remove)) +
                        "\nAlready satisfied:\n" + (satisfied.Length == 0 ? "none" : string.Join("\n", satisfied)), AssetDatabase.LoadMainAssetAtPath(PresetPath));
                    if (plan.retained.Length != 0)
                        Debug.Log("[UPC] " + PresetPath + ": Retained as dependencies:\n" + string.Join("\n", plan.retained), AssetDatabase.LoadMainAssetAtPath(PresetPath));
                    if (plan.errors.Length != 0) LogError(PresetPath, "Skipped actions:\n" + string.Join("\n", plan.errors));
                    SetStatus("Preview complete: " + (plan.add.Length + plan.remove.Length) + " changes, " +
                        plan.blocked.Length + " skipped, " + plan.retained.Length + " retained as dependencies. See Console for details.");
                    return;
                }
                if (!plan.HasChanges)
                {
                    ReportResults(actionable, installed, plan.errors, plan.blocked.Length);
                    return;
                }
                rules = actionable;
                skipped = plan.errors; skippedCount = plan.blocked.Length;
                SessionState.SetString(PendingKey, JsonUtility.ToJson(new PendingOperation
                {
                    builtInModules = rules, skipped = skipped, skippedCount = skippedCount
                }));
                SetStatus("Applying " + (plan.add.Length + plan.remove.Length) + " module changes…");
                change = Client.AddAndRemove(plan.add, plan.remove);
            }
            catch (Exception error)
            {
                search = null; list = null; change = null;
                ReportError("Module operation failed: " + error.Message);
                // Preserve the snapshot if Unity may still be processing a mutation.
            }
        }

        static void ReportResults(BuiltInModuleRule[] actions, ModulePackageState[] installed, string[] skippedActions, int skippedTotal)
        {
            var succeeded = BuiltInModulePlan.SatisfiedActions(actions, installed);
            var failed = BuiltInModulePlan.Violations(actions, installed);
            var retained = BuiltInModulePlan.DependencyRetentions(actions, installed);
            Debug.Log("[UPC] " + PresetPath + ": Preset completed.\nSucceeded (including already satisfied):\n" +
                (succeeded.Length == 0 ? "none" : string.Join("\n", succeeded)), AssetDatabase.LoadMainAssetAtPath(PresetPath));
            if (retained.Length != 0)
                Debug.Log("[UPC] " + PresetPath + ": Retained as dependencies:\n" + string.Join("\n", retained), AssetDatabase.LoadMainAssetAtPath(PresetPath));
            if (skippedActions.Length != 0 || failed.Length != 0)
                LogError(PresetPath,
                    (skippedActions.Length == 0 ? "" : "Skipped actions:\n" + string.Join("\n", skippedActions)) +
                    (failed.Length == 0 ? "" : "\nFailed verification:\n" + string.Join("\n", failed)));
            SetStatus("Preset completed: " + succeeded.Length + " succeeded, " + skippedTotal + " skipped, " +
                failed.Length + " failed, " + retained.Length + " retained as dependencies. See Console for details.");
        }

        internal static void LogError(string presetPath, string message) =>
            Debug.LogError("[UPC] " + presetPath + ": " + message, AssetDatabase.LoadMainAssetAtPath(presetPath));

        static void ReportError(string message)
        {
            LogError(PresetPath, message);
            SetStatus("See Console for preset diagnostics.");
        }

        static void SetStatus(string message) => SessionState.SetString(StatusKey, message);

    }
}
