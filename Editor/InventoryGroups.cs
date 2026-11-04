using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ProjectInventory.Editor
{
    /// <summary>
    /// Manages user-defined thematic asset groups (including nested sub-groups like "Environment/Park")
    /// that exist purely within Project Inventory. Saved to ProjectSettings/ProjectInventoryGroups.json.
    /// </summary>
    public static class InventoryGroups
    {
        const string SettingsPath = "ProjectSettings/ProjectInventoryGroups.json";

        [Serializable]
        class GroupEntry
        {
            public string Path;
            public List<string> Guids = new();
        }

        [Serializable]
        class SaveData
        {
            public List<GroupEntry> Groups = new();
        }

        // Canonical group path -> direct member GUIDs
        static readonly SortedDictionary<string, HashSet<string>> groups =
            new(StringComparer.OrdinalIgnoreCase);

        // Asset GUID -> canonical group paths it directly belongs to
        static readonly Dictionary<string, List<string>> assetGroups = new();

        static bool loaded;

        public static event Action Changed;

        public static IReadOnlyCollection<string> AllGroups
        {
            get
            {
                EnsureLoaded();
                return groups.Keys;
            }
        }

        public static int GroupCount
        {
            get
            {
                EnsureLoaded();
                return groups.Count;
            }
        }

        public static string NormalizePath(string groupPath)
        {
            if (string.IsNullOrWhiteSpace(groupPath)) return string.Empty;
            var parts = groupPath.Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p));
            return string.Join("/", parts);
        }

        public static string GetLeafName(string groupPath)
        {
            if (string.IsNullOrEmpty(groupPath)) return string.Empty;
            int slash = groupPath.LastIndexOf('/');
            return slash >= 0 ? groupPath.Substring(slash + 1) : groupPath;
        }

        public static string GetParentPath(string groupPath)
        {
            if (string.IsNullOrEmpty(groupPath)) return string.Empty;
            int slash = groupPath.LastIndexOf('/');
            return slash > 0 ? groupPath.Substring(0, slash) : string.Empty;
        }

        public static int GetDepth(string groupPath)
        {
            if (string.IsNullOrEmpty(groupPath)) return 0;
            int depth = 0;
            for (int i = 0; i < groupPath.Length; i++)
                if (groupPath[i] == '/') depth++;
            return depth;
        }

        public static bool HasSubgroups(string groupPath)
        {
            EnsureLoaded();
            string prefix = groupPath + "/";
            foreach (var k in groups.Keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static bool Exists(string groupPath)
        {
            EnsureLoaded();
            groupPath = NormalizePath(groupPath);
            return !string.IsNullOrEmpty(groupPath) && groups.ContainsKey(groupPath);
        }

        public static bool CreateGroup(string groupPath)
        {
            EnsureLoaded();
            groupPath = NormalizePath(groupPath);
            if (string.IsNullOrEmpty(groupPath)) return false;

            bool added = EnsureHierarchy(groupPath);
            if (added)
            {
                Save();
                Changed?.Invoke();
            }
            return added;
        }

        static bool EnsureHierarchy(string groupPath)
        {
            bool changed = false;
            var parts = groupPath.Split('/');
            string current = "";
            for (int i = 0; i < parts.Length; i++)
            {
                current = i == 0 ? parts[0] : current + "/" + parts[i];
                if (!groups.ContainsKey(current))
                {
                    groups[current] = new HashSet<string>();
                    changed = true;
                }
            }
            return changed;
        }

        public static bool RenameGroup(string oldPath, string newPath)
        {
            EnsureLoaded();
            oldPath = NormalizePath(oldPath);
            newPath = NormalizePath(newPath);
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return false;
            if (!groups.ContainsKey(oldPath)) return false;
            if (string.Equals(oldPath, newPath, StringComparison.Ordinal)) return false;

            // Prevent moving a group into its own descendant
            if (newPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) return false;

            string oldPrefix = oldPath + "/";
            var toMove = groups.Where(kv =>
                    string.Equals(kv.Key, oldPath, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var kv in toMove)
                groups.Remove(kv.Key);

            EnsureHierarchy(newPath);

            foreach (var kv in toMove)
            {
                string target = string.Equals(kv.Key, oldPath, StringComparison.OrdinalIgnoreCase)
                    ? newPath
                    : newPath + kv.Key.Substring(oldPath.Length);

                EnsureHierarchy(target);
                if (!groups.TryGetValue(target, out var set))
                    groups[target] = set = new HashSet<string>();

                foreach (var guid in kv.Value)
                    set.Add(guid);
            }

            RebuildReverseIndex();
            Save();
            Changed?.Invoke();
            return true;
        }

        public static bool DeleteGroup(string groupPath)
        {
            EnsureLoaded();
            groupPath = NormalizePath(groupPath);
            if (string.IsNullOrEmpty(groupPath) || !groups.ContainsKey(groupPath)) return false;

            string prefix = groupPath + "/";
            var toRemove = groups.Keys
                .Where(k => string.Equals(k, groupPath, StringComparison.OrdinalIgnoreCase) ||
                            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var k in toRemove)
                groups.Remove(k);

            RebuildReverseIndex();
            Save();
            Changed?.Invoke();
            return true;
        }

        public static void ClearGroup(string groupPath, bool includeSubgroups = false)
        {
            EnsureLoaded();
            groupPath = NormalizePath(groupPath);
            if (string.IsNullOrEmpty(groupPath) || !groups.ContainsKey(groupPath)) return;

            string prefix = groupPath + "/";
            foreach (var kv in groups)
            {
                if (string.Equals(kv.Key, groupPath, StringComparison.OrdinalIgnoreCase) ||
                    (includeSubgroups && kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    kv.Value.Clear();
                }
            }

            RebuildReverseIndex();
            Save();
            Changed?.Invoke();
        }

        public static void AddAssets(string groupPath, IEnumerable<string> guids)
        {
            EnsureLoaded();
            groupPath = NormalizePath(groupPath);
            if (string.IsNullOrEmpty(groupPath) || guids == null) return;

            bool changed = EnsureHierarchy(groupPath);
            // Resolve canonical key in case group existed with different casing
            string canonical = GetCanonicalKey(groupPath);
            var set = groups[canonical];

            foreach (var g in guids)
            {
                if (string.IsNullOrEmpty(g)) continue;
                if (set.Add(g)) changed = true;
            }

            if (changed)
            {
                RebuildReverseIndex();
                Save();
                Changed?.Invoke();
            }
        }

        public static void RemoveAssets(string groupPath, IEnumerable<string> guids, bool includeSubgroups = false)
        {
            EnsureLoaded();
            groupPath = NormalizePath(groupPath);
            if (string.IsNullOrEmpty(groupPath) || guids == null || !groups.ContainsKey(groupPath)) return;

            var guidList = guids as IList<string> ?? guids.ToList();
            bool changed = false;
            string prefix = groupPath + "/";

            foreach (var kv in groups)
            {
                if (string.Equals(kv.Key, groupPath, StringComparison.OrdinalIgnoreCase) ||
                    (includeSubgroups && kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var g in guidList)
                        if (kv.Value.Remove(g)) changed = true;
                }
            }

            if (changed)
            {
                RebuildReverseIndex();
                Save();
                Changed?.Invoke();
            }
        }

        public static bool Contains(string groupPath, string guid, bool includeSubgroups = true)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(groupPath) || string.IsNullOrEmpty(guid)) return false;
            if (!assetGroups.TryGetValue(guid, out var list)) return false;

            string prefix = groupPath + "/";
            for (int i = 0; i < list.Count; i++)
            {
                string g = list[i];
                if (string.Equals(g, groupPath, StringComparison.OrdinalIgnoreCase)) return true;
                if (includeSubgroups && g.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool IsUngrouped(string guid)
        {
            EnsureLoaded();
            return string.IsNullOrEmpty(guid) || !assetGroups.TryGetValue(guid, out var list) || list.Count == 0;
        }

        public static IReadOnlyList<string> GetGroupsForAsset(string guid)
        {
            EnsureLoaded();
            return guid != null && assetGroups.TryGetValue(guid, out var list)
                ? list
                : Array.Empty<string>();
        }

        /// <summary>
        /// Resolves dragged or selected paths/GUIDs into asset GUIDs, expanding folders into all assets inside them.
        /// </summary>
        public static List<string> ExpandPathsToGuids(IEnumerable<string> paths)
        {
            var result = new HashSet<string>();
            if (paths == null) return result.ToList();

            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (AssetDatabase.IsValidFolder(p))
                {
                    string prefix = p.TrimEnd('/') + "/";
                    foreach (var item in InventoryIndex.Items)
                        if (item.Path.StartsWith(prefix, StringComparison.Ordinal))
                            result.Add(item.Guid);
                }
                else
                {
                    string guid = AssetDatabase.AssetPathToGUID(p);
                    if (!string.IsNullOrEmpty(guid)) result.Add(guid);
                }
            }
            return result.ToList();
        }

        static string GetCanonicalKey(string groupPath)
        {
            foreach (var k in groups.Keys)
                if (string.Equals(k, groupPath, StringComparison.OrdinalIgnoreCase))
                    return k;
            return groupPath;
        }

        static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            Load();
        }

        public static void Reload()
        {
            loaded = true;
            Load();
            Changed?.Invoke();
        }

        static void Load()
        {
            groups.Clear();
            assetGroups.Clear();

            if (!File.Exists(SettingsPath)) return;
            try
            {
                string json = File.ReadAllText(SettingsPath);
                var data = JsonUtility.FromJson<SaveData>(json);
                if (data?.Groups == null) return;

                foreach (var entry in data.Groups)
                {
                    string norm = NormalizePath(entry.Path);
                    if (string.IsNullOrEmpty(norm)) continue;
                    EnsureHierarchy(norm);
                    string canonical = GetCanonicalKey(norm);
                    var set = groups[canonical];
                    if (entry.Guids != null)
                    {
                        foreach (var g in entry.Guids)
                            if (!string.IsNullOrEmpty(g)) set.Add(g);
                    }
                }

                RebuildReverseIndex();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Project Inventory: failed to load groups from " + SettingsPath + "\n" + ex.Message);
            }
        }

        static void RebuildReverseIndex()
        {
            assetGroups.Clear();
            foreach (var kv in groups)
            {
                foreach (var guid in kv.Value)
                {
                    if (!assetGroups.TryGetValue(guid, out var list))
                        assetGroups[guid] = list = new List<string>();
                    list.Add(kv.Key);
                }
            }
        }

        static void Save()
        {
            try
            {
                var data = new SaveData();
                foreach (var kv in groups)
                {
                    var guids = kv.Value.Where(g => !string.IsNullOrEmpty(g)).OrderBy(g => g, StringComparer.Ordinal).ToList();
                    data.Groups.Add(new GroupEntry { Path = kv.Key, Guids = guids });
                }

                string dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(SettingsPath, JsonUtility.ToJson(data, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Project Inventory: failed to save groups to " + SettingsPath + "\n" + ex.Message);
            }
        }
    }
}
