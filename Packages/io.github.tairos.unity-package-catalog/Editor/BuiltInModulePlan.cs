using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityPackageCatalog
{
    public sealed class ModulePackageState
    {
        public string name;
        public string version;
        public bool builtIn;
        public bool direct;
        public string[] dependencies = Array.Empty<string>();
    }

    public sealed class BuiltInModulePlan
    {
        public string[] add = Array.Empty<string>();
        public string[] remove = Array.Empty<string>();
        public string[] errors = Array.Empty<string>();
        public string[] blocked = Array.Empty<string>();
        public string[] retained = Array.Empty<string>();
        public bool HasChanges => add.Length != 0 || remove.Length != 0;

        public static BuiltInModulePlan Create(BuiltInModuleRule[] rules, ModulePackageState[] available, ModulePackageState[] installed)
        {
            BuiltInModuleRule.Validate(rules);
            rules ??= Array.Empty<BuiltInModuleRule>();
            var known = available.Where(p => p.builtIn).ToDictionary(p => p.name, StringComparer.Ordinal);
            var current = installed.ToDictionary(p => p.name, StringComparer.Ordinal);
            var issues = new List<string>();
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            var additions = new List<string>();
            var removals = new List<string>();
            foreach (var rule in rules)
            {
                current.TryGetValue(rule.name, out var existing);
                if (existing != null && !existing.builtIn)
                {
                    blocked.Add(rule.name);
                    issues.Add(rule.name + ": installed source is not built-in; no change made.");
                    continue;
                }
                if (rule.requirement == "excluded")
                {
                    // Match Unity's Disable action: remove the manifest entry. The resolver
                    // retains this module indirectly if another package still needs it.
                    if (existing != null && existing.direct) removals.Add(rule.name);
                    continue;
                }
                if (existing != null && existing.direct) continue;
                if (!known.TryGetValue(rule.name, out var module)) module = existing;
                if (module == null)
                {
                    blocked.Add(rule.name);
                    issues.Add(rule.name + ": module is unavailable in this Editor.");
                    continue;
                }
                additions.Add(rule.name + "@" + module.version);
            }
            var graph = new Dictionary<string, ModulePackageState>(known, StringComparer.Ordinal);
            foreach (var package in installed) graph[package.name] = package;
            var excluded = new HashSet<string>(rules.Where(r => r.requirement == "excluded" && !blocked.Contains(r.name)).Select(r => r.name), StringComparer.Ordinal);
            var roots = installed.Where(p => p.direct && !excluded.Contains(p.name)).Select(p => p.name)
                .Concat(rules.Where(r => r.requirement == "required" && !blocked.Contains(r.name)).Select(r => r.name)).Distinct().ToArray();
            return new BuiltInModulePlan
            {
                add = additions.ToArray(), remove = removals.ToArray(),
                blocked = blocked.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                errors = issues.ToArray(),
                retained = Retentions(rules.Where(r => !blocked.Contains(r.name)).ToArray(), roots, graph, false, new HashSet<string>(current.Keys, StringComparer.Ordinal))
            };
        }

        static List<string> FindPath(string name, string target, Dictionary<string, ModulePackageState> graph, HashSet<string> visited)
        {
            if (!visited.Add(name)) return null;
            if (name == target) return new List<string> { name };
            if (!graph.TryGetValue(name, out var package)) return null;
            foreach (var dependency in package.dependencies ?? Array.Empty<string>())
            {
                var path = FindPath(dependency, target, graph, visited);
                if (path == null) continue;
                path.Insert(0, name);
                return path;
            }
            return null;
        }

        static bool Satisfied(BuiltInModuleRule rule, Dictionary<string, ModulePackageState> installed)
        {
            installed.TryGetValue(rule.name, out var package);
            return rule.requirement == "required" ? package != null && package.builtIn && package.direct
                : package == null || (package.builtIn && !package.direct);
        }

        public static string[] Violations(BuiltInModuleRule[] rules, ModulePackageState[] installed)
        {
            BuiltInModuleRule.Validate(rules);
            var current = installed.ToDictionary(p => p.name, StringComparer.Ordinal);
            return (rules ?? Array.Empty<BuiltInModuleRule>()).Where(r => !Satisfied(r, current))
                .Select(r => r.name + (r.requirement == "required" ? " must be explicitly enabled." : " still has an explicit dependency or a non-built-in source.")).ToArray();
        }

        public static string[] SatisfiedActions(BuiltInModuleRule[] rules, ModulePackageState[] installed)
        {
            var current = installed.ToDictionary(p => p.name, StringComparer.Ordinal);
            return (rules ?? Array.Empty<BuiltInModuleRule>()).Where(r => Satisfied(r, current))
                .Select(r => (r.requirement == "required" ? "Enabled: " : current.ContainsKey(r.name)
                    ? "Explicit dependency removed (still installed as dependency): " : "Disabled: ") + r.name).ToArray();
        }

        public static string[] DependencyRetentions(BuiltInModuleRule[] rules, ModulePackageState[] installed)
        {
            var graph = installed.ToDictionary(p => p.name, StringComparer.Ordinal);
            var roots = installed.Where(p => p.direct).Select(p => p.name).ToArray();
            return Retentions(rules, roots, graph, true, new HashSet<string>(graph.Keys, StringComparer.Ordinal));
        }

        static string[] Retentions(BuiltInModuleRule[] rules, string[] roots, Dictionary<string, ModulePackageState> graph, bool resolved, HashSet<string> present)
        {
            var messages = new List<string>();
            foreach (var rule in rules.Where(r => r.requirement == "excluded"))
            {
                graph.TryGetValue(rule.name, out var module);
                if (resolved && (module == null || !module.builtIn || module.direct)) continue;
                var path = roots.Where(root => root != rule.name).OrderBy(root => root, StringComparer.Ordinal)
                    .Select(root => FindPath(root, rule.name, graph, new HashSet<string>(StringComparer.Ordinal)))
                    .FirstOrDefault(found => found != null);
                if (path != null) messages.Add(rule.name + ": retained as dependency; required by " + string.Join(" → ", path) + ".");
                else if (resolved || (present.Contains(rule.name) && module != null && module.builtIn && !module.direct))
                    messages.Add(rule.name + ": installed as dependency; there is no explicit manifest entry to remove.");
            }
            return messages.ToArray();
        }

        public string Describe() =>
            "Enable explicitly: " + (add.Length == 0 ? "none" : string.Join(", ", add)) +
            "\nRemove explicit dependencies: " + (remove.Length == 0 ? "none" : string.Join(", ", remove)) +
            (retained.Length == 0 ? "" : "\nRetained as dependencies:\n" + string.Join("\n", retained)) +
            (errors.Length == 0 ? "" : "\nSkipped:\n" + string.Join("\n", errors));
    }
}
