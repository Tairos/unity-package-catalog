using System;
using System.IO;
using System.Reflection;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnityPackageCatalog.Tests
{
    public class CatalogDocumentTests
    {
        string directory;
        [SetUp] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "upc-test-" + Guid.NewGuid()); Directory.CreateDirectory(directory); }
        [TearDown] public void Cleanup() { Directory.Delete(directory, true); }

        CatalogEntry Entry(string source = "ssh://git@github.com/example-owner/example-package.git#v1.0.0") => new CatalogEntry
        { name = "com.example.tool", displayName = "Example Tool", description = "Example", version = "1.0.0", source = source };
        CatalogDocument Load(params CatalogEntry[] entries)
        {
            var path = Path.Combine(directory, "catalog.upcjson");
            File.WriteAllText(path, JsonUtility.ToJson(new CatalogDocument { displayName = "Example Packages", packages = entries }));
            return CatalogDocument.Load(path);
        }

        [Test] public void PrivateSshUrlKeepsTagAndNeedsNoStoredCredential()
        {
            var entry = Entry();
            Assert.That(Load(entry).packages[0].resolvedSource, Is.EqualTo(entry.source));
        }
        [Test] public void GitSubfolderAndTagArePreserved()
        {
            var entry = Entry("https://github.com/example-owner/repository.git?path=/Packages/tool#v1.0.0");
            Assert.That(Load(entry).packages[0].resolvedSource, Is.EqualTo(entry.source));
        }
        [Test] public void DuplicatePackageIdsAreRejected() => Assert.Throws<FormatException>(() => Load(Entry(), Entry()));
        [TestCase("https://user:secret@github.com/example/repo.git")]
        [TestCase("ssh://git:secret@github.com/example/repo.git")]
        [TestCase("http://github.com/example/repo.git")]
        [TestCase("https://github.com/example/repo.git\nmalformed")]
        public void UnsafeOrMalformedSourcesAreRejected(string source) => Assert.Throws<FormatException>(() => Load(Entry(source)));
        [Test] public void LocalSourceResolvesRelativeToCatalogue()
        {
            var package = Path.Combine(directory, "tool"); Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), JsonUtility.ToJson(Entry()));
            Assert.That(Load(Entry("file:./tool")).packages[0].resolvedSource, Is.EqualTo("file:" + package.Replace('\\', '/')));
        }
        [Test] public void WrongLocalPackageIdentityIsRejected()
        {
            var package = Path.Combine(directory, "tool"); Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"com.example.other\",\"version\":\"1.0.0\"}");
            Assert.Throws<FormatException>(() => Load(Entry("file:./tool")));
        }
        [Test] public void UnsupportedSchemaIsRejected()
        {
            var path = Path.Combine(directory, "catalog.upcjson");
            File.WriteAllText(path, "{\"schemaVersion\":2,\"displayName\":\"Example\",\"packages\":[]}");
            Assert.Throws<FormatException>(() => CatalogDocument.Load(path));
        }
        [Test] public void EmptyCatalogueIsValid() => Assert.That(Load().packages, Is.Empty);

        [Test] public void BulkImportPreservesSourcesAndSeparatesAssetStoreEntriesWithoutChangingAsset()
        {
            var local = Path.Combine(directory, "tool"); Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "package.json"), JsonUtility.ToJson(Entry()));
            var git = Entry("git+https://github.com/example/repository.git?path=/Packages/tool#v2");
            git.name = "com.example.git";
            var registry = Entry("registry"); registry.name = "com.example.registry";
            var document = Load(git, registry, Entry("file:./tool"), new CatalogEntry { assetStoreProductId = 12345 });
            var path = Path.Combine(directory, "catalog.upcjson");
            var original = File.ReadAllText(path);
            var plan = CatalogImport.CreatePlan(path, document);
            Assert.That(plan.packageIds, Is.EqualTo(new[] { git.source, "com.example.registry@1.0.0", "file:" + local.Replace('\\', '/') }));
            Assert.That(plan.assetIds, Is.EqualTo(new long[] { 12345 }));
            Assert.That(File.ReadAllText(path), Is.EqualTo(original));
            var restored = JsonUtility.FromJson<CatalogImport.Plan>(JsonUtility.ToJson(plan));
            Assert.That(restored.packageIds, Is.EqualTo(plan.packageIds));
            Assert.That(restored.assetIds, Is.EqualTo(plan.assetIds));
        }

        [TestCase("com.example.tool@1.0.0", "com.example.tool@1.0.0", true)]
        [TestCase("com.example.tool@1.0.0", "com.example.tool@2.0.0", false)]
        [TestCase("git+https://example.com/tool.git#v1", "com.example.tool@https://example.com/tool.git#v1", true)]
        [TestCase("https://example.com/tool.git#v1", "com.example.tool@https://example.com/tool.git#v2", false)]
        [TestCase("file:/tmp/tool", "com.example.tool@file:/tmp/tool", true)]
        public void BulkImportVerificationRequiresRequestedSource(string requested, string installed, bool matches)
        {
            Assert.That(CatalogImport.MatchesId(requested, installed), Is.EqualTo(matches));
        }

        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("invalid json")]
        public void MalformedLocalManifestHasPackageContext(string json)
        {
            var package = Path.Combine(directory, "tool"); Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), json);
            var error = Assert.Throws<FormatException>(() => Load(Entry("file:./tool")));
            Assert.That(error.Message, Does.Contain("entry 1 (com.example.tool)"));
        }

        [Test] public void LocalMismatchReportsExpectedAndActualIdentity()
        {
            var package = Path.Combine(directory, "tool"); Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"com.example.other\",\"version\":\"2.0.0\"}");
            var error = Assert.Throws<FormatException>(() => Load(Entry("file:./tool")));
            Assert.That(error.Message, Does.Contain("com.example.other@2.0.0"));
            Assert.That(error.Message, Does.Contain("com.example.tool@1.0.0"));
        }

        [Test] public void MalformedCatalogueHasClearError()
        {
            var path = Path.Combine(directory, "catalog.upcjson");
            File.WriteAllText(path, "invalid json");
            Assert.That(Assert.Throws<FormatException>(() => CatalogDocument.Load(path)).Message,
                Is.EqualTo("Catalogue is not valid JSON."));
        }

        [Test] public void RefreshDetectsProjectEditsAndRetainsValidDocumentDuringInvalidEdit()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(NativeCatalogBridge);
            if (type.GetField("manager", flags).GetValue(null) != null)
                Assert.Ignore("Run refresh state test with Package Manager closed.");
            var fields = new[] { "document", "observedJson", "refreshError", "openRequested", "sources", "requestedPageId" }
                .Select(name => type.GetField(name, flags)).ToArray();
            var saved = fields.Select(field => field.GetValue(null)).ToArray();
            var refresh = type.GetMethod("RefreshDocument", flags);
            var path = "Assets/UPCRefresh_" + Guid.NewGuid().ToString("N") + ".upcjson";
            void Write(params CatalogEntry[] entries)
            {
                File.WriteAllText(path, JsonUtility.ToJson(new CatalogDocument { displayName = "Refresh test", packages = entries }));
                UnityEditor.AssetDatabase.ImportAsset(path);
            }
            try
            {
                fields[0].SetValue(null, null); fields[1].SetValue(null, null); fields[2].SetValue(null, null); fields[3].SetValue(null, false);
                Write(Entry()); refresh.Invoke(null, Array.Empty<object>());
                Assert.That(((CatalogDocument)fields[0].GetValue(null)).packages.Any(entry => entry.name == "com.example.tool"), Is.True);
                Write(); refresh.Invoke(null, Array.Empty<object>());
                var updated = (CatalogDocument)fields[0].GetValue(null);
                Assert.That(updated.packages.Any(entry => entry.name == "com.example.tool"), Is.False);
                File.WriteAllText(path, "invalid json"); refresh.Invoke(null, Array.Empty<object>());
                Assert.That(fields[0].GetValue(null), Is.SameAs(updated)); Assert.That(fields[2].GetValue(null), Is.Not.Null);
                Write(); refresh.Invoke(null, Array.Empty<object>()); Assert.That(fields[2].GetValue(null), Is.Null);
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(path);
                for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            }
        }

        [Test] public void PlainJsonCatalogueIsRejected()
        {
            var path = Path.Combine(directory, "catalog.json");
            File.WriteAllText(path, JsonUtility.ToJson(new CatalogDocument { displayName = "Test", packages = Array.Empty<CatalogEntry>() }));
            Assert.That(Assert.Throws<FormatException>(() => CatalogDocument.Load(path)).Message, Does.Contain(".upcjson"));
        }

        [Test] public void AssetStoreEntryUsesProductIdWithoutUpmMetadata()
        {
            var entry = new CatalogEntry { assetStoreProductId = 12345 };
            Assert.That(Load(entry).packages[0].assetStoreProductId, Is.EqualTo(12345));
        }
        [Test] public void GitAndAssetStoreEntriesCanShareCatalogue()
        {
            Assert.That(Load(Entry(), new CatalogEntry { assetStoreProductId = 12345 }).packages.Length, Is.EqualTo(2));
        }
        [Test] public void DuplicateAssetStoreIdsAreRejected()
        {
            Assert.Throws<FormatException>(() => Load(new CatalogEntry { assetStoreProductId = 12345 }, new CatalogEntry { assetStoreProductId = 12345 }));
        }
        [Test] public void NegativeAssetStoreIdIsRejected()
        {
            Assert.Throws<FormatException>(() => Load(new CatalogEntry { assetStoreProductId = -1 }));
        }
        [Test] public void AssetStoreEntryCannotMasqueradeAsGitPackage()
        {
            var entry = Entry(); entry.assetStoreProductId = 12345;
            Assert.Throws<FormatException>(() => Load(entry));
        }
        [Test] public void AddingAssetsPreservesGitEntriesAndDeduplicatesSelection()
        {
            Load(Entry());
            var path = Path.Combine(directory, "catalog.upcjson");
            var additions = new[] { new CatalogEntry { assetStoreProductId = 12345 }, new CatalogEntry { assetStoreProductId = 12345 } };
            Assert.That(CatalogDocument.AddAssetStoreEntries(path, additions), Is.EqualTo(1));
            Assert.That(CatalogDocument.AddAssetStoreEntries(path, additions), Is.Zero);
            var result = CatalogDocument.Load(path);
            Assert.That(result.packages[0].source, Is.EqualTo(Entry().source));
            Assert.That(result.packages[1].assetStoreProductId, Is.EqualTo(12345));
        }
        [Test] public void InvalidAdditionDoesNotOverwriteCatalogue()
        {
            Load(Entry());
            var path = Path.Combine(directory, "catalog.upcjson");
            var before = File.ReadAllText(path);
            var invalid = Entry(); invalid.assetStoreProductId = 12345;
            Assert.Throws<FormatException>(() => CatalogDocument.AddAssetStoreEntries(path, new[] { invalid }));
            Assert.That(File.ReadAllText(path), Is.EqualTo(before));
        }
    }
}
