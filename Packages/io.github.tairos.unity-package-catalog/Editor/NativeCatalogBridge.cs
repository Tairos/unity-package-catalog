using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityPackageCatalog
{
    // All unsupported Unity internals are isolated here. No Unity reference-source code is vendored.
    [InitializeOnLoad]
    public static class NativeCatalogBridge
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        const string Namespace = "UnityEditor.PackageManager.UI.Internal.";
        static CatalogDocument document;
        static CatalogSource[] sources = Array.Empty<CatalogSource>();
        static readonly Dictionary<string, object> catalogPages = new Dictionary<string, object>();
        static object manager, database, page;
        static EditorWindow attachedWindow;
        static readonly Dictionary<string, object> synthetic = new Dictionary<string, object>();
        static readonly HashSet<long> visibleOwnedAssets = new HashSet<long>();
        static readonly HashSet<string> requestedRegistry = new HashSet<string>();
        static readonly HashSet<long> requestedAssets = new HashSet<long>();
        static double assetFetchStarted;
        static double nextTick;
        static string observedJson, refreshError;
        static bool stopped, openRequested;
        public static string Status { get; private set; } = "Not enabled.";

        static NativeCatalogBridge()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Detach;
            EditorApplication.quitting += Detach;
        }

        public static void Reload()
        {
            document = null;
            observedJson = refreshError = null;
            stopped = false;
            nextTick = 0;
            Status = CatalogSettings.Enabled ? "Waiting for Package Manager." : "Disabled.";
        }

        public static void RequestOpen(string path = null) { if (!string.IsNullOrEmpty(path)) { var guid = AssetDatabase.AssetPathToGUID(path); requestedPageId = string.IsNullOrEmpty(guid) ? null : "Extension/UnityPackageCatalog." + guid; } openRequested = true; nextTick = 0; }

        static void Tick()
        {
            if (!CatalogSettings.Enabled || stopped || EditorApplication.isCompiling || EditorApplication.timeSinceStartup < nextTick)
                return;
            nextTick = EditorApplication.timeSinceStartup + 0.75;
            try
            {
                if (!Application.unityVersion.StartsWith("6000.6.", StringComparison.Ordinal))
                    throw new NotSupportedException("Package Catalog currently supports Unity 6000.6. This Editor needs a compatible adapter.");
                var windowType = FindType("UnityEditor.PackageManager.UI.PackageManagerWindow");
                var window = Resources.FindObjectsOfTypeAll(windowType).OfType<EditorWindow>().FirstOrDefault();
                if (window == null)
                {
                    if (manager != null) Detach();
                    return;
                }
                RefreshDocument();
                if (document == null) { Status = refreshError; return; }
                if (manager == null || attachedWindow != window)
                    Attach(window);
                EnsureEntries();
                EnsureAssetStoreEntries();
                foreach (var source in sources) MoveRowToSources(window, "Extension/" + source.id);
                if (openRequested)
                {
                    if (requestedPageId != null && catalogPages.TryGetValue(requestedPageId, out var requestedPage)) page = requestedPage;
                    if (page != null) SetProperty(manager, "activePage", page);
                    openRequested = false;
                }
                Status = "Connected to Unity " + Application.unityVersion + " · " + document.displayName + " · " + document.packages.Length + " catalogue entries.";
                if (refreshError != null) Status += "\n" + refreshError;
            }
            catch (Exception error)
            {
                var message = error.GetBaseException().Message;
                stopped = true;
                try { Detach(); } catch { /* Preserve the first diagnostic. */ }
                Status = "Integration stopped: " + message + " Open Package Catalog Settings to retry or disable.";
                Debug.LogWarning("[Unity Package Catalog] " + Status);
            }
        }

        static void RefreshDocument()
        {
            CatalogSource[] updatedSources;
            string json;
            try
            {
                updatedSources = CatalogRegistry.LoadSources();
                json = string.Join("\n", updatedSources.Select(source => source.id + "|" + source.path + "|" + File.ReadAllText(source.path)));
            }
            catch (Exception error)
            {
                refreshError = "Catalogue refresh failed: " + error.Message + ". Last valid catalogue set remains active.";
                return;
            }
            if (json == observedJson) { refreshError = null; return; }
            if (BuiltInModuleRequirements.Busy || (manager != null && IsPackageOperationInProgress())) return;
            var activeId = manager == null ? null : (string)GetProperty(GetProperty(manager, "activePage"), "id");
            Detach();
            sources = updatedSources;
            document = new CatalogDocument
            {
                displayName = sources.Length + " catalogues",
                packages = sources.SelectMany(source => source.document.packages).GroupBy(entry => entry.assetStoreProductId > 0 ? "asset:" + entry.assetStoreProductId : entry.name).Select(group => group.First()).ToArray()
            };
            observedJson = json;
            refreshError = null;
            openRequested |= activeId != null && sources.Any(source => "Extension/" + source.id == activeId);
            if (!openRequested || requestedPageId == null) requestedPageId = activeId;
        }

        static string requestedPageId;

        static void Attach(EditorWindow window)
        {
            manager = Service("PageManager");
            database = Service("IPackageDatabase");
            foreach (var source in sources)
            {
                var id = "Extension/" + source.id;
                var args = Activator.CreateInstance(Type("ExtensionPageArgs"), true);
                SetField(args, "name", source.id);
                SetField(args, "displayName", source.document.displayName);
                SetField(args, "icon", Enum.Parse(Type("Icon"), "MyRegistriesPage"));
                SetField(args, "refreshOptions", Enum.Parse(Type("RefreshOptions"),
                    source.document.packages.Any(p => p.assetStoreProductId > 0) ? "UpmList, LocalInfo, ImportedAssets" : "UpmList"));
                var sorts = Array.CreateInstance(Type("PageSortOption"), 2);
                sorts.SetValue(Enum.Parse(Type("PageSortOption"), "NameAsc"), 0);
                sorts.SetValue(Enum.Parse(Type("PageSortOption"), "NameDesc"), 1);
                SetField(args, "supportedSortOptions", sorts);
                var filterField = args.GetType().GetField("filter", Flags);
                var parameter = Expression.Parameter(Type("IPackage"), "package");
                var callback = typeof(NativeCatalogBridge).GetMethod(nameof(Includes), Flags);
                filterField.SetValue(args, Expression.Lambda(filterField.FieldType,
                    Expression.Call(callback, Expression.Constant(source.id), Expression.Convert(parameter, typeof(object))), parameter).Compile());
                var groupField = args.GetType().GetField("getGroupName", Flags);
                groupField.SetValue(args, Expression.Lambda(groupField.FieldType,
                    Expression.Call(typeof(NativeCatalogBridge).GetMethod(nameof(GroupName), Flags), Expression.Constant(source.id), Expression.Convert(parameter, typeof(object))), parameter).Compile());
                var pages = (IDictionary)GetField(manager, "m_Pages");
                if (pages.Contains(id))
                    Invoke(pages[id], "UpdateArgs", args);
                var orderedArgs = (IList)GetField(manager, "m_OrderedExtensionPageArgs");
                if (!orderedArgs.Cast<object>().Any(a => (string)GetField(a, "name") == source.id))
                    Invoke(manager, "AddExtensionPage", args);
                catalogPages[id] = Invoke(manager, "GetPage", id);
            }
            page = requestedPageId != null && catalogPages.TryGetValue(requestedPageId, out var requested) ? requested : catalogPages.Values.FirstOrDefault();
            attachedWindow = window;
        }

        static bool Includes(string sourceId, object package)
        {
            var document = sources.FirstOrDefault(source => source.id == sourceId)?.document;
            if (document == null) return false;
            var product = GetProperty(package, "product");
            if (product != null)
            {
                var id = (long)GetProperty(product, "id");
                if (document.packages.Any(p => p.assetStoreProductId == id) && IsAssetOwned(id)) return true;
            }
            var name = (string)GetProperty(package, "name");
            return document.packages.Any(p => p.assetStoreProductId == 0 && p.name == name);
        }

        static string GroupName(string sourceId, object package)
        {
            var document = sources.First(source => source.id == sourceId).document;
            var product = GetProperty(package, "product");
            if (product != null)
            {
                var assetEntry = document.packages.FirstOrDefault(item => item.assetStoreProductId == (long)GetProperty(product, "id"));
                return string.IsNullOrWhiteSpace(assetEntry?.group) ? "Asset Store" : assetEntry.group;
            }
            var name = (string)GetProperty(package, "name");
            var entry = document.packages.FirstOrDefault(item => item.name == name);
            if (!string.IsNullOrWhiteSpace(entry?.group)) return entry.group;
            if (entry == null) return "Packages";
            return entry.source == "registry" ? "UPM" : entry.resolvedSource.StartsWith("file:", StringComparison.Ordinal) ? "Local" : "Git";
        }

        static void RebuildPages()
        {
            foreach (var value in catalogPages.Values) Invoke(value, "Rebuild", false);
        }

        static void EnsureEntries()
        {
            // Unity owns real installed records and operation progress. Never overwrite them.
            if (BuiltInModuleRequirements.Busy || (bool)GetProperty(Service("IPackageOperationDispatcher"), "isInstallOrUninstallInProgress")) return;
            var additions = new List<object>();
            foreach (var entry in document.packages)
            {
                if (entry.assetStoreProductId > 0) continue;
                if (entry.source == "registry")
                {
                    if (requestedRegistry.Add(entry.name + "@" + entry.version)) Invoke(Service("IUpmClient"), "ExtraFetchPackageInfo", entry.name + "@" + entry.version, null, null, null);
                    continue;
                }
                var existing = Invoke(database, "GetPackage", entry.name);
                if (existing != null) continue;
                var version = Activator.CreateInstance(Type("UpmPackageVersion"), Flags, null,
                    new object[] { entry.name, entry.version, Enum.Parse(Type("RegistryType"), "None") }, null);
                SetField(version, "m_DisplayName", entry.displayName);
                SetField(version, "m_Description", entry.description ?? "");
                SetField(version, "m_IsFullyFetched", true);
                SetField(version, "m_PackageId", entry.name + "@" + entry.resolvedSource);
                SetField(version, "m_Tag", Enum.Parse(Type("PackageTag"), entry.resolvedSource.StartsWith("file:") ? "UpmFormat, Local" : "UpmFormat, Git"));
                // UpmVersionList has no empty constructor. Populate every field needed for a single uninstalled version.
                var versions = FormatterServices.GetUninitializedObject(Type("UpmVersionList"));
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("UpmPackageVersion")));
                list.Add(version);
                SetField(versions, "m_Versions", list);
                SetField(versions, "m_InstalledIndex", -1);
                SetField(versions, "m_RecommendedIndex", -1);
                SetField(versions, "m_SuggestedUpdateIndex", -1);
                var package = Activator.CreateInstance(Type("Package"), Flags, null,
                    new object[] { entry.name, versions, null, false, false, null, null }, null);
                synthetic[entry.name] = package;
                additions.Add(package);
            }
            if (additions.Count != 0)
            {
                var array = Array.CreateInstance(Type("IPackage"), additions.Count);
                for (var i = 0; i < additions.Count; i++) array.SetValue(additions[i], i);
                Invoke(database, "UpdatePackages", array, null, Enum.Parse(Type("PackagesChangedSource"), "Other"));
            }
        }

        static bool IsAssetOwned(long id) =>
            (bool)GetProperty(Service("IUnityConnectProxy"), "isUserLoggedIn") &&
            Invoke(Service("IAssetStoreCache"), "GetPurchaseInfo", id) != null;

        static void EnsureAssetStoreEntries()
        {
            var entries = document.packages.Where(p => p.assetStoreProductId > 0).ToArray();
            if (entries.Length == 0) return;
            if (!(bool)GetProperty(Service("IUnityConnectProxy"), "isUserLoggedIn"))
            {
                requestedAssets.Clear();
                if (visibleOwnedAssets.Count > 0)
                {
                    visibleOwnedAssets.Clear();
                    RebuildPages();
                }
                return;
            }
            var owned = new HashSet<long>(entries.Where(e => IsAssetOwned(e.assetStoreProductId)).Select(e => e.assetStoreProductId));
            if (!visibleOwnedAssets.SetEquals(owned))
            {
                visibleOwnedAssets.Clear();
                visibleOwnedAssets.UnionWith(owned);
                RebuildPages();
            }
            var client = Service("IAssetStoreClient");
            foreach (var entry in entries)
                if (requestedAssets.Add(entry.assetStoreProductId))
                {
                    assetFetchStarted = EditorApplication.timeSinceStartup;
                    // Unity fetches purchase and product metadata and creates its own package record.
                    Invoke(client, "ExtraFetch", entry.assetStoreProductId);
                }
        }

        static string AssetStoreDiagnostic(CatalogEntry entry)
        {
            var label = "Asset Store " + entry.assetStoreProductId + ": ";
            if (!(bool)GetProperty(Service("IUnityConnectProxy"), "isUserLoggedIn"))
                return label + "sign in to Unity to access owned assets.";
            var fetch = Invoke(Service("IFetchStatusTracker"), "GetProductInfoFetchStatus", entry.assetStoreProductId);
            var error = GetField(fetch, "error");
            if (error != null) return label + "Unity could not load product metadata: " + GetProperty(error, "message") + ". Turn catalogue integration off and on to retry.";
            if (!IsAssetOwned(entry.assetStoreProductId))
                return label + (EditorApplication.timeSinceStartup - assetFetchStarted < 30
                    ? "checking ownership…" : "ownership unavailable or not owned by this account. Check My Assets, then turn catalogue integration off and on.");
            var package = Invoke(database, "GetPackage", entry.assetStoreProductId);
            if (package == null) return label + "loading native metadata…";
            var cache = Service("IAssetStoreCache");
            return label + GetProperty(package, "displayName") + " · " +
                (Invoke(cache, "GetImportedPackage", entry.assetStoreProductId) != null ? "imported" :
                 Invoke(cache, "GetLocalInfo", entry.assetStoreProductId) != null ? "downloaded; ready to import" : "owned; ready to download");
        }

        public static bool IsPackageOperationInProgress() =>
            (bool)GetProperty(Service("IPackageOperationDispatcher"), "isInstallOrUninstallInProgress");

        internal static void FetchImportAsset(long id)
        {
            if (!(bool)GetProperty(Service("IUnityConnectProxy"), "isUserLoggedIn"))
                throw new InvalidOperationException("Sign in to Unity to import Asset Store entries.");
            Invoke(Service("IAssetStoreClient"), "ExtraFetch", id);
        }

        internal static string PrepareImportAsset(long id, ref bool downloadRequested)
        {
            var fetch = Invoke(Service("IFetchStatusTracker"), "GetProductInfoFetchStatus", id);
            var error = GetField(fetch, "error");
            if (error != null) throw new InvalidOperationException((string)GetProperty(error, "message"));
            if (!IsAssetOwned(id)) return null;
            var local = Invoke(Service("IAssetStoreCache"), "GetLocalInfo", id);
            var path = local == null ? null : (string)GetField(local, "packagePath");
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            var downloads = Service("IAssetStoreDownloadManager");
            if (!downloadRequested)
            {
                if (!(bool)Invoke(downloads, "Download", new long[] { id }))
                    throw new InvalidOperationException("Unity could not start the Asset Store download. Check My Assets and try again.");
                downloadRequested = true;
            }
            var operation = Invoke(downloads, "GetDownloadOperation", id);
            if (operation != null)
            {
                var state = GetProperty(operation, "state").ToString();
                if (state == "Error" || state == "Aborted")
                    throw new InvalidOperationException("Asset Store download " + state + ": " + GetProperty(operation, "errorMessage"));
            }
            return null;
        }

        internal static void FetchOwnedAssets(string search, int start, int limit, Action<CatalogEntry[], long> success, Action<string> failure)
        {
            if (!(bool)GetProperty(Service("IUnityConnectProxy"), "isUserLoggedIn"))
            {
                failure("Sign in to Unity to browse your owned Asset Store items, then choose Refresh.");
                return;
            }
            var query = Activator.CreateInstance(Type("PurchasesQueryArgs"), new object[] { start, limit, search ?? "", null });
            var api = Service("IAssetStoreRestAPI");
            var method = api.GetType().GetMethods(Flags).Single(m => m.Name == "GetPurchases" && m.GetParameters().Length == 3);
            var parameters = method.GetParameters();
            Action<object> received = purchases =>
            {
                try
                {
                    var list = GetField(purchases, "list");
                    Invoke(Service("IAssetStoreCache"), "SetPurchaseInfos", list);
                    var entries = ((IEnumerable)list).Cast<object>().Select(item => new CatalogEntry
                    {
                        assetStoreProductId = (long)GetField(item, "productId"),
                        displayName = (string)GetField(item, "displayName")
                    }).ToArray();
                    success(entries, (long)GetField(purchases, "total"));
                }
                catch (Exception error) { failure(error.GetBaseException().Message); }
            };
            Action<object> failed = error => failure((string)GetProperty(error, "message"));
            method.Invoke(api, new[] { query, Callback(parameters[1].ParameterType, received), Callback(parameters[2].ParameterType, failed) });
        }

        static Delegate Callback(System.Type delegateType, Action<object> callback)
        {
            var parameter = Expression.Parameter(delegateType.GetMethod("Invoke").GetParameters()[0].ParameterType, "value");
            return Expression.Lambda(delegateType, Expression.Invoke(Expression.Constant(callback), Expression.Convert(parameter, typeof(object))), parameter).Compile();
        }

        static void MoveRowToSources(EditorWindow window, string pageId)
        {
            var sidebar = window.rootVisualElement.Query<VisualElement>().ToList().FirstOrDefault(e => e.GetType().Name == "Sidebar");
            if (sidebar == null) return;
            var row = (VisualElement)Invoke(sidebar, "GetRow", pageId);
            if (row == null) return;
            // Find the native Built-in row rather than relying on localized foldout text.
            var builtIn = sidebar.Query<VisualElement>().ToList().FirstOrDefault(e => e.GetType().Name == "SidebarRow" &&
                (string)GetProperty(e, "pageId") == "BuiltIn");
            var sources = builtIn?.GetFirstAncestorOfType<Foldout>();
            if (sources == null) throw new MissingMemberException("Could not find the native Sources group.");
            PlaceSourceRow(sidebar, row, sources, pageId);
        }

        internal static void PlaceSourceRow(VisualElement sidebar, VisualElement row, Foldout sources, string pageId)
        {
            // Sidebar rebuilds can recreate an extension row in its original group while
            // retaining the moved row. Keep the row owned by the native sidebar lookup.
            foreach (var duplicate in sidebar.Query<VisualElement>().ToList().Where(element =>
                element.GetType().Name == "SidebarRow" && !ReferenceEquals(element, row) &&
                (string)GetProperty(element, "pageId") == pageId)) duplicate.RemoveFromHierarchy();
            if (row.GetFirstAncestorOfType<Foldout>() != sources) sources.Add(row);
        }

        public static void Detach()
        {
            if (manager == null) return;
            if (catalogPages.Values.Any(value => ReferenceEquals(GetProperty(manager, "activePage"), value)))
                SetProperty(manager, "activePage", Invoke(manager, "GetPage", "InProject"));
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
                foreach (var element in window.rootVisualElement.Query<VisualElement>().ToList())
                    if (element.GetType().Name == "SidebarRow" && catalogPages.ContainsKey((string)GetProperty(element, "pageId")))
                        element.RemoveFromHierarchy();
            var owned = synthetic.Where(p => ReferenceEquals(Invoke(database, "GetPackage", p.Key), p.Value)).Select(p => p.Key).ToArray();
            if (owned.Length > 0)
                Invoke(database, "UpdatePackages", null, owned, Enum.Parse(Type("PackagesChangedSource"), "Other"));
            foreach (var pair in catalogPages)
            {
                Invoke(pair.Value, "OnDisable");
                ((IDictionary)GetField(manager, "m_Pages")).Remove(pair.Key);
            }
            catalogPages.Clear();
            var args = (IList)GetField(manager, "m_OrderedExtensionPageArgs");
            for (var i = args.Count - 1; i >= 0; i--)
                if (((string)GetField(args[i], "name")).StartsWith("UnityPackageCatalog", StringComparison.Ordinal)) args.RemoveAt(i);
            synthetic.Clear();
            requestedAssets.Clear();
            requestedRegistry.Clear();
            visibleOwnedAssets.Clear();
            manager = database = page = null;
            attachedWindow = null;
        }

        public static string Diagnostics()
        {
            if (database == null) return Status;
            var registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            return Status + "\n" + string.Join("\n", document.packages.Select(entry =>
            {
                if (entry.assetStoreProductId > 0) return AssetStoreDiagnostic(entry);
                var package = Invoke(database, "GetPackage", entry.name);
                var versions = package == null ? null : GetProperty(package, "versions");
                var installed = versions == null ? null : GetProperty(versions, "installed");
                if (installed == null)
                {
                    var other = registered.FirstOrDefault(p => p.name != entry.name &&
                        (p.source == UnityEditor.PackageManager.PackageSource.Git && p.packageId == p.name + "@" + GitSource(entry.resolvedSource) ||
                         entry.resolvedSource.StartsWith("file:", StringComparison.Ordinal) && p.source == UnityEditor.PackageManager.PackageSource.Local &&
                         Path.GetFullPath(p.resolvedPath).TrimEnd('/', '\\') == entry.resolvedSource.Substring(5).TrimEnd('/', '\\')));
                    if (other != null) return entry.name + ": package ID mismatch — source installed as " + other.name + "; catalogue expects " + entry.name;
                    return entry.name + ": " + (versions == null ? "missing" : "available");
                }
                var info = registered.FirstOrDefault(p => p.name == entry.name);
                if (info == null) return entry.name + ": installed (metadata unavailable)";
                var issues = new List<string>();
                if (info.version != entry.version) issues.Add("version " + info.version + "; catalogue expects " + entry.version);
                if (entry.resolvedSource.StartsWith("file:", StringComparison.Ordinal))
                {
                    if (info.source != UnityEditor.PackageManager.PackageSource.Local ||
                        Path.GetFullPath(info.resolvedPath).TrimEnd('/', '\\') != entry.resolvedSource.Substring(5).TrimEnd('/', '\\'))
                        issues.Add("source differs from catalogue local package");
                }
                else if (entry.source == "registry")
                {
                    if (info.source != UnityEditor.PackageManager.PackageSource.Registry) issues.Add("source differs from registry");
                }
                else if (info.source != UnityEditor.PackageManager.PackageSource.Git ||
                    info.packageId != entry.name + "@" + GitSource(entry.resolvedSource))
                    issues.Add("Git source/revision differs from catalogue (or Unity normalized its URL); verify installed package source");
                return entry.name + ": installed" + (issues.Count == 0 ? "" : " — " + string.Join("; ", issues));
            }));
        }

        static string GitSource(string source) => source.StartsWith("git+", StringComparison.Ordinal) ? source.Substring(4) : source;

        static Type FindType(string fullName) => typeof(UnityEditor.PackageManager.UI.PackageManagerExtensions).Assembly.GetType(fullName)
            ?? throw new TypeLoadException("Missing Unity internal type " + fullName);
        static Type Type(string name) => FindType(Namespace + name);
        static object Service(string name)
        {
            var container = Type("ServicesContainer").GetProperty("instance", Flags).GetValue(null);
            return container.GetType().GetMethods(Flags).Single(m => m.Name == "Resolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                .MakeGenericMethod(Type(name)).Invoke(container, null);
        }
        static FieldInfo Field(object target, string name)
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Flags);
                if (field != null) return field;
            }
            throw new MissingFieldException(target.GetType().FullName, name);
        }
        static object GetField(object target, string name) => Field(target, name).GetValue(target);
        static void SetField(object target, string name, object value) => Field(target, name).SetValue(target, value);
        static object GetProperty(object target, string name) => target.GetType().GetProperty(name, Flags).GetValue(target);
        static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, Flags).SetValue(target, value);
        static object Invoke(object target, string name, params object[] args)
        {
            var methods = target.GetType().GetMethods(Flags).Where(m => m.Name == name && !m.IsGenericMethod && m.GetParameters().Length == args.Length);
            var method = methods.FirstOrDefault(m => m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(v => v));
            if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
            return method.Invoke(target, args);
        }
    }
}
