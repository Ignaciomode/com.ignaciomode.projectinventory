using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ProjectInventory.Editor
{
    /// <summary>High level grouping used by the type filter and sidebar.</summary>
    public enum AssetCategory
    {
        Texture,
        Sprite,
        Model,
        Prefab,
        Material,
        Shader,
        Audio,
        Video,
        Animation,
        Script,
        Scene,
        ScriptableObject,
        Font,
        Text,
        Other
    }

    /// <summary>Lightweight, load-free description of one project asset.</summary>
    [Serializable]
    public class InventoryItem
    {
        public string Guid;
        public string Path;
        public string Name;
        public string Extension;
        public string Folder;
        public string RootFolder;
        public AssetCategory Category;
        public string TypeName;
        public long Size;
        public DateTime Modified;
        public string[] Labels = Array.Empty<string>();

        // Filled by the optional reference scan.
        public int UsedByCount = -1;

        public bool IsPackage => Path.StartsWith("Packages/", StringComparison.Ordinal);

        public static InventoryItem Create(string guid, string path)
        {
            var item = new InventoryItem
            {
                Guid = guid,
                Path = path,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Extension = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                Folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty,
            };

            var parts = path.Split('/');
            item.RootFolder = parts.Length > 2 ? parts[0] + "/" + parts[1] : parts[0];

            Type mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
            item.TypeName = mainType != null ? mainType.Name : "Unknown";
            item.Category = Categorize(mainType, item.Extension, path);

            try
            {
                var fi = new FileInfo(System.IO.Path.GetFullPath(path));
                if (fi.Exists)
                {
                    item.Size = fi.Length;
                    item.Modified = fi.LastWriteTime;
                }
            }
            catch
            {
                // Virtual package paths may not resolve; ignore.
            }

            if (GUID.TryParse(guid, out var g))
                item.Labels = AssetDatabase.GetLabels(g) ?? Array.Empty<string>();

            return item;
        }

        static AssetCategory Categorize(Type t, string ext, string path)
        {
            switch (ext)
            {
                case "fbx":
                case "obj":
                case "blend":
                case "dae":
                case "3ds":
                case "max":
                case "ma":
                case "mb":
                case "gltf":
                case "glb":
                    return AssetCategory.Model;
                case "prefab":
                    return AssetCategory.Prefab;
                case "unity":
                    return AssetCategory.Scene;
                case "cs":
                    return AssetCategory.Script;
                case "shader":
                case "shadergraph":
                case "shadersubgraph":
                case "compute":
                case "hlsl":
                case "cginc":
                    return AssetCategory.Shader;
                case "anim":
                case "controller":
                case "overridecontroller":
                case "mask":
                case "playable":
                    return AssetCategory.Animation;
                case "ttf":
                case "otf":
                    return AssetCategory.Font;
                case "txt":
                case "json":
                case "xml":
                case "md":
                case "csv":
                case "yaml":
                case "ink":
                case "bytes":
                    return AssetCategory.Text;
            }

            if (t == null) return AssetCategory.Other;
            if (typeof(Texture2D).IsAssignableFrom(t))
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && ti.textureType == TextureImporterType.Sprite)
                    return AssetCategory.Sprite;
                return AssetCategory.Texture;
            }
            if (typeof(Texture).IsAssignableFrom(t)) return AssetCategory.Texture;
            if (typeof(Material).IsAssignableFrom(t)) return AssetCategory.Material;
            if (typeof(Shader).IsAssignableFrom(t)) return AssetCategory.Shader;
            if (typeof(AudioClip).IsAssignableFrom(t)) return AssetCategory.Audio;
            if (typeof(UnityEngine.Video.VideoClip).IsAssignableFrom(t)) return AssetCategory.Video;
            if (typeof(AnimationClip).IsAssignableFrom(t) || typeof(RuntimeAnimatorController).IsAssignableFrom(t))
                return AssetCategory.Animation;
            if (typeof(Font).IsAssignableFrom(t)) return AssetCategory.Font;
            if (typeof(TextAsset).IsAssignableFrom(t)) return AssetCategory.Text;
            if (typeof(MonoScript).IsAssignableFrom(t)) return AssetCategory.Script;
            if (typeof(SceneAsset).IsAssignableFrom(t)) return AssetCategory.Scene;
            if (typeof(GameObject).IsAssignableFrom(t)) return AssetCategory.Model;
            if (typeof(Mesh).IsAssignableFrom(t)) return AssetCategory.Model;
            if (typeof(ScriptableObject).IsAssignableFrom(t)) return AssetCategory.ScriptableObject;
            return AssetCategory.Other;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024f).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024f * 1024f)).ToString("0.#") + " MB";
            return (bytes / (1024f * 1024f * 1024f)).ToString("0.##") + " GB";
        }
    }
}
