using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace UnityPackageCatalog
{
    [InitializeOnLoad]
    internal static class CatalogImport
    {
        const string Key = "UnityPackageCatalog.Import.";
        [Serializable]
        internal sealed class Plan
        {
            public string path;
            public string[] packageIds;
            public long[] assetIds;
            public int nextAsset;
            public string stage;
            public string importName;
        }

        static Plan pending;
        static AddAndRemoveRequest install;
        static ListRequest verify;
        static bool fetching, downloadRequested;
        static double deadline;
        public static bool Busy => pending != null;
        public static bool CanStopQueue => pending?.stage == "assets";
        public static string Path => SessionState.GetString(Key + "path", "");
        public static string Status => SessionState.GetString(Key + "status", "");

        static CatalogImport()
        {
            var saved = SessionState.GetString(Key + "pending", "");
            if (!string.IsNullOrEmpty(saved)) pending = JsonUtility.FromJson<Plan>(saved);
            deadline = EditorApplication.timeSinceStartup + 600;
            EditorApplication.update += Poll;
            AssetDatabase.importPackageStarted += name =>
            {
                if (pending?.stage == "import") { pending.importName = name; Save(); }
            };
            AssetDatabase.importPackageCompleted += Completed;
            AssetDatabase.importPackageCancelled += Cancelled;
            AssetDatabase.importPackageFailed += Failed;
        }

        internal static Plan CreatePlan(string path, CatalogDocument document) => new Plan
        {
            path = path,
            packageIds = document.packages.Where(e => e.assetStoreProductId == 0)
                .Select(e => e.source == "registry" ? e.name + "@" + e.version : e.resolvedSource).ToArray(),
            assetIds = document.packages.Where(e => e.assetStoreProductId > 0).Select(e => e.assetStoreProductId).ToArray(),
            stage = "packages"
        };

        public static void Begin(string path)
        {
            if (Busy || BuiltInModuleRequirements.Busy || EditorApplication.isCompiling || NativeCatalogBridge.IsPackageOperationInProgress())
                throw new InvalidOperationException("Wait for the current import, package operation or compilation to finish.");
            var plan = CreatePlan(path, CatalogDocument.Load(path));
            if (plan.packageIds.Length == 0 && plan.assetIds.Length == 0)
                throw new InvalidOperationException("This catalogue has no entries to import.");
            pending = plan;
            SessionState.SetString(Key + "path", path);
            deadline = EditorApplication.timeSinceStartup + 600;
            Save();
            try
            {
                if (plan.packageIds.Length == 0) StartAssets();
                else
                {
                    SetStatus("Installing " + plan.packageIds.Length + " catalogue packages…");
                    install = Client.AddAndRemove(plan.packageIds);
                }
            }
            catch (Exception error) { Stop("Import failed: " + error.GetBaseException().Message, true); }
        }

        static void Poll()
        {
            if (!Busy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            try
            {
                if (pending.stage == "import") return;
                if (EditorApplication.timeSinceStartup > deadline)
                    throw new TimeoutException("Import timed out. Check Package Manager for download, ownership or package errors before retrying.");
                if (pending.stage == "packages")
                {
                    if (install != null)
                    {
                        if (!install.IsCompleted) return;
                        if (install.Status != StatusCode.Success) throw new InvalidOperationException(install.Error?.message ?? "Package installation failed.");
                        install = null;
                        StartAssets();
                    }
                    else
                    {
                        if (NativeCatalogBridge.IsPackageOperationInProgress()) return;
                        if (verify == null) { verify = Client.List(true, true); return; }
                        if (!verify.IsCompleted) return;
                        if (verify.Status != StatusCode.Success) throw new InvalidOperationException(verify.Error?.message ?? "Could not verify installed packages.");
                        var installed = verify.Result.Select(p => NormalizeId(p.packageId)).ToArray();
                        var missing = pending.packageIds.Where(id => !installed.Any(actual => MatchesId(id, actual))).ToArray();
                        if (missing.Length > 0) throw new InvalidOperationException("Installation could not be verified after script reload: " + string.Join(", ", missing) + ". Check Package Manager before retrying.");
                        verify = null;
                        StartAssets();
                    }
                    return;
                }
                if (pending.nextAsset >= pending.assetIds.Length)
                {
                    Stop("Catalogue import complete: " + pending.packageIds.Length + " packages installed, " + pending.assetIds.Length + " Asset Store imports completed.", false);
                    return;
                }
                if (NativeCatalogBridge.IsPackageOperationInProgress()) return;
                var id = pending.assetIds[pending.nextAsset];
                if (!fetching)
                {
                    NativeCatalogBridge.FetchImportAsset(id);
                    fetching = true;
                    SetStatus("Preparing Asset Store " + id + " (" + (pending.nextAsset + 1) + "/" + pending.assetIds.Length + ")…");
                }
                var file = NativeCatalogBridge.PrepareImportAsset(id, ref downloadRequested);
                if (file == null) return;
                pending.stage = "import";
                pending.importName = System.IO.Path.GetFileNameWithoutExtension(file);
                Save();
                SetStatus("Review Unity's import dialog for Asset Store " + id + ". Cancel stops the remaining catalogue imports.");
                UnityEditor.AssetPackage.Package.Import(file, true);
            }
            catch (Exception error) { Stop("Import failed: " + error.GetBaseException().Message, true); }
        }

        internal static string NormalizeId(string id) => id.Replace("git+https://", "https://").Replace("git+ssh://", "ssh://").Replace('\\', '/');
        internal static bool MatchesId(string requested, string installed)
        {
            requested = NormalizeId(requested);
            installed = NormalizeId(installed);
            return installed == requested || installed.EndsWith("@" + requested, StringComparison.Ordinal);
        }

        static void StartAssets()
        {
            pending.stage = "assets";
            fetching = downloadRequested = false;
            deadline = EditorApplication.timeSinceStartup + 1800;
            Save();
        }

        static bool IsCurrentImport(string name) => pending?.stage == "import" && name == pending.importName;
        static void Completed(string name)
        {
            if (!IsCurrentImport(name)) return;
            pending.nextAsset++;
            StartAssets();
        }
        static void Cancelled(string name)
        {
            if (IsCurrentImport(name)) Stop("Catalogue import cancelled. Packages and assets already imported remain installed.", false);
        }
        static void Failed(string name, string error)
        {
            if (IsCurrentImport(name)) Stop("Asset Store import failed: " + error, true);
        }
        public static void StopQueue()
        {
            if (CanStopQueue) Stop("Stopped queuing catalogue imports. Any download already started is still managed by Unity.", false);
        }
        static void Stop(string message, bool error)
        {
            pending = null;
            install = null;
            verify = null;
            fetching = downloadRequested = false;
            SessionState.EraseString(Key + "pending");
            SetStatus(message);
            if (error) Debug.LogError("[UPC] " + Path + ": " + message);
            else Debug.Log("[UPC] " + Path + ": " + message);
        }
        static void Save() => SessionState.SetString(Key + "pending", JsonUtility.ToJson(pending));
        static void SetStatus(string message) => SessionState.SetString(Key + "status", message);
    }
}
