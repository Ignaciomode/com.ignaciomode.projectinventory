using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace ProjectInventory.Editor
{
    /// <summary>
    /// In-memory index of every asset in the project. Cheap metadata only — nothing
    /// is loaded. An optional reference scan builds a reverse dependency map so the
    /// UI can show "Used By" and find unreferenced assets.
    /// </summary>
    public static class InventoryIndex
    {
        public static readonly List<InventoryItem> Items = new();
        static readonly Dictionary<string, InventoryItem> byGuid = new();

        // guid -> guids of assets that depend on it (direct dependencies only)
        static Dictionary<string, HashSet<string>> usedBy;

        static bool includedPackages;

        public static bool IsDirty { get; internal set; } = true;
        public static bool HasReferenceData => usedBy != null;
        public static bool ReferencesStale { get; private set; }
        public static DateTime LastScan { get; private set; }
        public static event Action Changed;

        public static void Rebuild(bool includePackages)
        {
            Items.Clear();
            byGuid.Clear();
            includedPackages = includePackages;

            var roots = includePackages ? new[] { "Assets", "Packages" } : new[] { "Assets" };
            var guids = AssetDatabase.FindAssets(string.Empty, roots);
            var seen = new HashSet<string>();

            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string guid = guids[i];
                    if (!seen.Add(guid)) continue;

                    if (i % 200 == 0)
                        EditorUtility.DisplayProgressBar("Project Inventory", "Indexing assets...", (float)i / guids.Length);

                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;

                    var item = InventoryItem.Create(guid, path);
                    Items.Add(item);
                    byGuid[guid] = item;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (usedBy != null) ApplyUsedByCounts();
            IsDirty = false;
            LastScan = DateTime.Now;
            Changed?.Invoke();
        }

        public static InventoryItem Get(string guid) => guid != null && byGuid.TryGetValue(guid, out var i) ? i : null;

        public static void RefreshLabels(InventoryItem item)
        {
            if (GUID.TryParse(item.Guid, out var g))
                item.Labels = AssetDatabase.GetLabels(g) ?? Array.Empty<string>();
            Changed?.Invoke();
        }

        public static IEnumerable<string> AllLabels() =>
            Items.SelectMany(i => i.Labels).Distinct().OrderBy(l => l, StringComparer.OrdinalIgnoreCase);

        /// <summary>Scans direct dependencies of every asset. Returns false if cancelled.</summary>
        public static bool BuildReferenceIndex()
        {
            var map = new Dictionary<string, HashSet<string>>();
            try
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    var item = Items[i];
                    if (i % 50 == 0 &&
                        EditorUtility.DisplayCancelableProgressBar("Project Inventory",
                            "Scanning references... " + item.Name, (float)i / Items.Count))
                        return false;

                    foreach (var dep in AssetDatabase.GetDependencies(item.Path, false))
                    {
                        if (dep == item.Path) continue;
                        string depGuid = AssetDatabase.AssetPathToGUID(dep);
                        if (string.IsNullOrEmpty(depGuid)) continue;
                        if (!map.TryGetValue(depGuid, out var set)) map[depGuid] = set = new HashSet<string>();
                        set.Add(item.Guid);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            usedBy = map;
            ReferencesStale = false;
            ApplyUsedByCounts();
            Changed?.Invoke();
            return true;
        }

        static void ApplyUsedByCounts()
        {
            foreach (var item in Items)
                item.UsedByCount = usedBy.TryGetValue(item.Guid, out var s) ? s.Count : 0;
        }

        public static IEnumerable<string> GetUsedBy(string guid) =>
            usedBy != null && usedBy.TryGetValue(guid, out var s) ? s : Enumerable.Empty<string>();

        /// <summary>
        /// Assets that are commonly "used" without being referenced by another asset
        /// (scenes, scripts, editor-only files, Resources…). Excluded from the Unused filter.
        /// </summary>
        public static bool IsImplicitlyUsed(InventoryItem item)
        {
            if (item.Category == AssetCategory.Scene || item.Category == AssetCategory.Script) return true;
            if (item.Extension is "asmdef" or "asmref" or "dll" or "rsp" or "md" or "txt") return true;
            if (item.Path.Contains("/Resources/") || item.Path.Contains("/Editor/") ||
                item.Path.Contains("/StreamingAssets/") || item.Path.Contains("/Settings/")) return true;
            return false;
        }

        static bool InScope(string path) =>
            path.StartsWith("Assets/", StringComparison.Ordinal) ||
            (includedPackages && path.StartsWith("Packages/", StringComparison.Ordinal));

        /// <summary>Incrementally applies AssetDatabase changes instead of re-indexing everything.</summary>
        static void ApplyChanges(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (IsDirty || Items.Count == 0) return; // full rebuild pending anyway

            var touched = new HashSet<string>(deleted.Concat(movedFrom).Concat(imported).Concat(moved));
            Items.RemoveAll(i =>
            {
                if (!touched.Contains(i.Path)) return false;
                byGuid.Remove(i.Guid);
                return true;
            });

            foreach (var path in imported.Concat(moved).Distinct())
            {
                if (!InScope(path) || AssetDatabase.IsValidFolder(path)) continue;
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;
                if (byGuid.TryGetValue(guid, out var old)) Items.Remove(old);

                var item = InventoryItem.Create(guid, path);
                if (usedBy != null) item.UsedByCount = usedBy.TryGetValue(guid, out var s) ? s.Count : 0;
                Items.Add(item);
                byGuid[guid] = item;
            }

            if (usedBy != null) ReferencesStale = true;
            Changed?.Invoke();
        }

        /// <summary>Keeps the index in sync with the AssetDatabase.</summary>
        class Watcher : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (imported.Length + deleted.Length + moved.Length == 0) return;
                ApplyChanges(imported, deleted, moved, movedFrom);
            }
        }
    }
}
