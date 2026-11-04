using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProjectInventory.Editor
{
    /// <summary>
    /// Browsable, filterable inventory of every asset in the project: sidebar filters,
    /// virtualized thumbnail grid / list, and a details panel with interactive preview,
    /// metadata, labels, dependencies and reverse references.
    /// </summary>
    public class ProjectInventoryWindow : EditorWindow
    {
        enum SortMode { Name, Type, Size, Modified, Path, UsedBy }
        enum Splitter { None, Sidebar, Details }

        static readonly string[] MinSizeLabels = { "Any size", "> 100 KB", "> 1 MB", "> 10 MB", "> 50 MB" };
        static readonly long[] MinSizeValues = { 0, 100L * 1024, 1024L * 1024, 10L * 1024 * 1024, 50L * 1024 * 1024 };

        const float ToolbarHeight = 21f;
        const float StatusHeight = 20f;
        const float SplitterWidth = 4f;
        const float Spacing = 6f;
        const float ListRowHeight = 20f;
        const string UngroupedFilter = "__ungrouped__";

        // ---- persisted UI state -------------------------------------------------
        [SerializeField] string searchText = "";
        [SerializeField] int categoryMask; // 0 == all
        [SerializeField] string groupFilter = "";
        [SerializeField] List<string> excludedGroups = new();
        [SerializeField] string folderFilter = "";
        [SerializeField] string labelFilter = "";
        [SerializeField] int minSizeIndex;
        [SerializeField] bool unusedOnly;
        [SerializeField] SortMode sortMode = SortMode.Name;
        [SerializeField] bool sortDescending;
        [SerializeField] bool gridMode = true;
        [SerializeField] float tileSize = 96f;
        [SerializeField] bool showTitles = true;
        [SerializeField] bool showPreview = true;
        [SerializeField] bool includePackages;
        [SerializeField] bool syncSelection;
        [SerializeField] bool autoPlayAudio;
        [SerializeField] bool showSidebar = true;
        [SerializeField] float sidebarWidth = 190f;
        [SerializeField] float detailsWidth = 320f;
        [SerializeField] List<string> selection = new();
        [SerializeField] Vector2 resultsScroll, sidebarScroll, detailsScroll;
        [SerializeField] bool foldGroups = true, foldTypes = true, foldFolders = true, foldLabels = true, foldFilters = true;
        [SerializeField] bool foldDeps = true, foldUsedBy = true;
        [SerializeField] List<string> collapsedGroups = new();

        // ---- runtime state --------------------------------------------------------
        SearchField searchField;
        List<InventoryItem> filtered = new();
        bool filterDirty = true;
        readonly Dictionary<AssetCategory, int> categoryCounts = new();
        readonly SortedDictionary<string, int> groupCounts = new(StringComparer.OrdinalIgnoreCase);
        int ungroupedCount;
        readonly SortedDictionary<string, int> folderCounts = new(StringComparer.OrdinalIgnoreCase);
        readonly SortedDictionary<string, int> labelCounts = new(StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<string, Texture> thumbs = new();
        readonly HashSet<string> noPreview = new();
        bool needsRepaint;

        Splitter activeSplitter;
        int lastClickIndex = -1;
        string pendingSingleSelect;
        bool dragCandidate;
        Vector2 dragStart;
        int lastCols = 1;
        float lastCellHeight = 100f, lastViewHeight = 100f;
        string newLabel = "";
        string newGroup = "";
        bool isCreatingGroup;
        string renamingGroupPath;
        string editingGroupText = "";
        bool focusGroupField;
        List<string> pendingGroupAddGuids;

        // details cache
        string detailsGuid;
        Object detailsObject;
        UnityEditor.Editor previewEditor;
        readonly List<KeyValuePair<string, string>> extraInfo = new();
        readonly List<string> dependencyPaths = new();
        string previewText;
        Vector2 textPreviewScroll;

        GUIStyle tileLabelStyle, badgeStyle, countStyle, monoStyle, headerStyle;

        // ---- menu entries -------------------------------------------------------------
        [MenuItem("Tools/Project Inventory")]
        public static ProjectInventoryWindow Open()
        {
            var w = GetWindow<ProjectInventoryWindow>();
            w.minSize = new Vector2(260, 200);
            w.Show();
            return w;
        }

        [MenuItem("Assets/Show in Project Inventory", false, 2000)]
        static void ShowInInventory()
        {
            var w = Open();
            if (InventoryIndex.Items.Count == 0) w.RebuildIndex();
            w.selection = Selection.assetGUIDs
                .Where(g => InventoryIndex.Get(g) != null).ToList();
            w.ClearFilters();
            w.RefreshFilter();
            if (w.selection.Count > 0)
                w.ScrollTo(w.filtered.FindIndex(i => i.Guid == w.selection[0]));
        }

        // ---- lifecycle ------------------------------------------------------------
        void OnEnable()
        {
            titleContent = new GUIContent("Inventory", EditorGUIUtility.IconContent("Folder Icon").image);
            searchField = new SearchField();
            AssetPreview.SetPreviewTextureCacheSize(1024);
            InventoryIndex.Changed += OnIndexChanged;
            InventoryGroups.Changed += OnGroupsChanged;

            if (InventoryIndex.IsDirty || InventoryIndex.Items.Count == 0)
                EditorApplication.delayCall += RebuildIndex;
            else
                OnIndexChanged();
        }

        void OnDisable()
        {
            InventoryIndex.Changed -= OnIndexChanged;
            InventoryGroups.Changed -= OnGroupsChanged;
            DestroyPreviewEditor();
            InventoryAudio.Stop();
        }

        void OnIndexChanged()
        {
            thumbs.Clear();
            noPreview.Clear();
            detailsGuid = null; // force details refresh
            RecountSidebar();
            filterDirty = true;
            Repaint();
        }

        void OnGroupsChanged()
        {
            if (!string.IsNullOrEmpty(groupFilter) && groupFilter != UngroupedFilter && !InventoryGroups.Exists(groupFilter))
                groupFilter = "";
            excludedGroups.RemoveAll(g => !InventoryGroups.Exists(g));
            RecountSidebar();
            filterDirty = true;
            Repaint();
        }

        void RebuildIndex()
        {
            if (this == null) return;
            InventoryIndex.Rebuild(includePackages);
        }

        // ---- GUI ------------------------------------------------------------------
        void InitStyles()
        {
            if (tileLabelStyle != null) return;
            tileLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                clipping = TextClipping.Clip,
                wordWrap = false
            };
            badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 8,
                normal = { textColor = Color.white }
            };
            countStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }
            };
            monoStyle = new GUIStyle(EditorStyles.label)
            {
                font = Font.CreateDynamicFontFromOSFont("Consolas", 11),
                fontSize = 11,
                wordWrap = false,
                richText = false,
                alignment = TextAnchor.UpperLeft
            };
            headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14, wordWrap = true };
        }

        void OnGUI()
        {
            InitStyles();
            needsRepaint = false;
            if (filterDirty) RefreshFilter();

            bool isNarrow = position.width < 540f;
            float currentToolbarHeight = isNarrow ? (ToolbarHeight * 2) : ToolbarHeight;
            float bodyY = currentToolbarHeight;
            float bodyH = position.height - currentToolbarHeight - StatusHeight;

            bool drawSidebar = showSidebar;
            bool drawDetails = showPreview;

            float effSidebarWidth = 0f;
            if (drawSidebar)
            {
                float maxSidebar = Mathf.Max(60f, position.width * 0.4f);
                float minSidebar = Mathf.Min(100f, maxSidebar);
                sidebarWidth = Mathf.Clamp(sidebarWidth, minSidebar, maxSidebar);
                effSidebarWidth = sidebarWidth;
            }

            float effDetailsWidth = 0f;
            if (drawDetails)
            {
                float remainingForDetails = position.width - effSidebarWidth - (drawSidebar ? SplitterWidth : 0f) - 60f;
                float maxDetails = Mathf.Max(80f, Mathf.Min(position.width * 0.55f, remainingForDetails));
                float minDetails = Mathf.Min(140f, maxDetails);
                detailsWidth = Mathf.Clamp(detailsWidth, minDetails, maxDetails);
                effDetailsWidth = detailsWidth;
            }

            var toolbarRect = new Rect(0, 0, position.width, currentToolbarHeight);
            var sidebarRect = new Rect(0, bodyY, effSidebarWidth, bodyH);
            var split1 = new Rect(sidebarRect.xMax, bodyY, drawSidebar ? SplitterWidth : 0f, bodyH);
            var detailsRect = new Rect(position.width - effDetailsWidth, bodyY, effDetailsWidth, bodyH);
            var split2 = new Rect(detailsRect.x - (drawDetails ? SplitterWidth : 0f), bodyY, drawDetails ? SplitterWidth : 0f, bodyH);
            float resultsRight = drawDetails ? split2.x : position.width;
            float resultsLeft = drawSidebar ? split1.xMax : 0f;
            var resultsRect = new Rect(resultsLeft, bodyY, Mathf.Max(0f, resultsRight - resultsLeft), bodyH);
            var statusRect = new Rect(0, position.height - StatusHeight, position.width, StatusHeight);

            HandleSplitters(split1, split2);
            HandleKeyboard();

            DrawToolbar(toolbarRect);
            if (drawSidebar) DrawSidebar(sidebarRect);
            DrawResults(resultsRect);
            if (drawDetails) DrawDetails(detailsRect);
            DrawStatusBar(statusRect);

            var line = EditorGUIUtility.isProSkin ? new Color(0.1f, 0.1f, 0.1f) : new Color(0.6f, 0.6f, 0.6f);
            if (drawSidebar)
                EditorGUI.DrawRect(new Rect(split1.x + 1, split1.y, 1, split1.height), line);
            if (drawDetails)
                EditorGUI.DrawRect(new Rect(split2.x + 2, split2.y, 1, split2.height), line);

            if (needsRepaint || AssetPreview.IsLoadingAssetPreviews()) Repaint();
        }

        void HandleSplitters(Rect s1, Rect s2)
        {
            if (showSidebar) EditorGUIUtility.AddCursorRect(s1, MouseCursor.ResizeHorizontal);
            if (showPreview) EditorGUIUtility.AddCursorRect(s2, MouseCursor.ResizeHorizontal);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (showSidebar && s1.Contains(e.mousePosition)) { activeSplitter = Splitter.Sidebar; e.Use(); }
                else if (showPreview && s2.Contains(e.mousePosition)) { activeSplitter = Splitter.Details; e.Use(); }
            }
            else if (activeSplitter != Splitter.None && e.type == EventType.MouseDrag)
            {
                if (activeSplitter == Splitter.Sidebar) sidebarWidth = e.mousePosition.x;
                else detailsWidth = position.width - e.mousePosition.x;
                e.Use();
                Repaint();
            }
            else if (activeSplitter != Splitter.None && e.rawType == EventType.MouseUp)
            {
                activeSplitter = Splitter.None;
            }
        }

        // ---- toolbar ------------------------------------------------------------------
        void DrawToolbar(Rect rect)
        {
            GUILayout.BeginArea(rect);
            if (rect.width >= 540f)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    if (GUILayout.Button(new GUIContent(EditorGUIUtility.IconContent("Refresh").image, "Re-index the project"),
                            EditorStyles.toolbarButton, GUILayout.Width(28)))
                        RebuildIndex();

                    GUILayout.Space(4);
                    EditorGUI.BeginChangeCheck();
                    searchText = searchField.OnToolbarGUI(searchText, GUILayout.ExpandWidth(true), GUILayout.MinWidth(80));
                    if (EditorGUI.EndChangeCheck()) filterDirty = true;

                    var help = new GUIContent("?",
                        "Search syntax (space separated, all must match):\n" +
                        "  word        name or path contains word\n" +
                        "  -word       exclude\n" +
                        "  t:texture   category or type name\n" +
                        "  g:park      inventory group (use -g:park to exclude)\n" +
                        "  l:label     asset label\n" +
                        "  ext:png     file extension\n" +
                        "  dir:ui      folder contains");
                    GUILayout.Label(help, EditorStyles.toolbarButton, GUILayout.Width(18));

                    GUILayout.Space(6);
                    EditorGUI.BeginChangeCheck();
                    sortMode = (SortMode)EditorGUILayout.EnumPopup(sortMode, EditorStyles.toolbarPopup, GUILayout.Width(80));
                    if (GUILayout.Button(new GUIContent(sortDescending ? "▼" : "▲", "Sort direction"),
                            EditorStyles.toolbarButton, GUILayout.Width(22)))
                        sortDescending = !sortDescending;
                    if (EditorGUI.EndChangeCheck()) filterDirty = true;

                    GUILayout.Space(6);
                    if (GUILayout.Toggle(gridMode, "Grid", EditorStyles.toolbarButton, GUILayout.Width(38))) gridMode = true;
                    if (GUILayout.Toggle(!gridMode, "List", EditorStyles.toolbarButton, GUILayout.Width(38))) gridMode = false;
                    using (new EditorGUI.DisabledScope(!gridMode))
                        tileSize = GUILayout.HorizontalSlider(tileSize, 48f, 256f, GUILayout.Width(90));

                    GUILayout.Space(6);
                    if (GUILayout.Button(EditorGUIUtility.IconContent("_Popup"), EditorStyles.toolbarButton, GUILayout.Width(26)))
                        ShowSettingsMenu();
                }
            }
            else
            {
                // Multi-row toolbar for narrow layouts (e.g. docked in right column or lower right)
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    if (GUILayout.Button(new GUIContent(EditorGUIUtility.IconContent("Refresh").image, "Re-index the project"),
                            EditorStyles.toolbarButton, GUILayout.Width(26)))
                        RebuildIndex();

                    GUILayout.Space(2);
                    EditorGUI.BeginChangeCheck();
                    searchText = searchField.OnToolbarGUI(searchText, GUILayout.ExpandWidth(true), GUILayout.MinWidth(50));
                    if (EditorGUI.EndChangeCheck()) filterDirty = true;

                    var help = new GUIContent("?",
                        "Search syntax (space separated, all must match):\n" +
                        "  word        name or path contains word\n" +
                        "  -word       exclude\n" +
                        "  t:texture   category or type name\n" +
                        "  g:park      inventory group (use -g:park to exclude)\n" +
                        "  l:label     asset label\n" +
                        "  ext:png     file extension\n" +
                        "  dir:ui      folder contains");
                    GUILayout.Label(help, EditorStyles.toolbarButton, GUILayout.Width(18));

                    GUILayout.Space(2);
                    if (GUILayout.Button(EditorGUIUtility.IconContent("_Popup"), EditorStyles.toolbarButton, GUILayout.Width(24)))
                        ShowSettingsMenu();
                }

                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    EditorGUI.BeginChangeCheck();
                    sortMode = (SortMode)EditorGUILayout.EnumPopup(sortMode, EditorStyles.toolbarPopup, GUILayout.Width(Mathf.Clamp(rect.width * 0.28f, 65f, 85f)));
                    if (GUILayout.Button(new GUIContent(sortDescending ? "▼" : "▲", "Sort direction"),
                            EditorStyles.toolbarButton, GUILayout.Width(20)))
                        sortDescending = !sortDescending;
                    if (EditorGUI.EndChangeCheck()) filterDirty = true;

                    GUILayout.Space(4);
                    if (GUILayout.Toggle(gridMode, "Grid", EditorStyles.toolbarButton, GUILayout.Width(34))) gridMode = true;
                    if (GUILayout.Toggle(!gridMode, "List", EditorStyles.toolbarButton, GUILayout.Width(34))) gridMode = false;

                    if (gridMode)
                    {
                        GUILayout.Space(2);
                        tileSize = GUILayout.HorizontalSlider(tileSize, 48f, 256f, GUILayout.MinWidth(40), GUILayout.MaxWidth(120));
                    }
                    else
                    {
                        GUILayout.FlexibleSpace();
                    }
                }
            }
            GUILayout.EndArea();
        }

        void ShowSettingsMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Show Sidebar"), showSidebar, () => showSidebar = !showSidebar);
            menu.AddItem(new GUIContent("Show Preview & Details"), showPreview, () =>
            {
                showPreview = !showPreview;
                if (!showPreview)
                {
                    DestroyPreviewEditor();
                    detailsGuid = null;
                }
            });
            menu.AddItem(new GUIContent("Show Tile Titles"), showTitles, () => showTitles = !showTitles);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("View/Grid Mode"), gridMode, () => gridMode = true);
            menu.AddItem(new GUIContent("View/List Mode"), !gridMode, () => gridMode = false);

            foreach (SortMode mode in (SortMode[])Enum.GetValues(typeof(SortMode)))
            {
                SortMode m = mode;
                menu.AddItem(new GUIContent($"Sort By/{m}"), sortMode == m, () =>
                {
                    sortMode = m;
                    filterDirty = true;
                });
            }
            menu.AddItem(new GUIContent("Sort Descending"), sortDescending, () =>
            {
                sortDescending = !sortDescending;
                filterDirty = true;
            });

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Include Packages"), includePackages, () =>
            {
                includePackages = !includePackages;
                RebuildIndex();
            });
            menu.AddItem(new GUIContent("Sync Unity Selection"), syncSelection, () => syncSelection = !syncSelection);
            menu.AddItem(new GUIContent("Auto-play Audio"), autoPlayAudio, () => autoPlayAudio = !autoPlayAudio);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Scan References (Used By)"), false, ScanReferences);
            menu.AddItem(new GUIContent("Clear All Filters"), false, () => { ClearFilters(); filterDirty = true; });
            menu.ShowAsContext();
        }

        void ScanReferences()
        {
            if (InventoryIndex.BuildReferenceIndex()) filterDirty = true;
        }

        // ---- sidebar --------------------------------------------------------------
        void RecountSidebar()
        {
            categoryCounts.Clear();
            groupCounts.Clear();
            ungroupedCount = 0;
            folderCounts.Clear();
            labelCounts.Clear();

            foreach (var g in InventoryGroups.AllGroups)
                groupCounts[g] = 0;

            var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var i in InventoryIndex.Items)
            {
                categoryCounts.TryGetValue(i.Category, out int c);
                categoryCounts[i.Category] = c + 1;
                folderCounts.TryGetValue(i.RootFolder, out int f);
                folderCounts[i.RootFolder] = f + 1;
                foreach (var l in i.Labels)
                {
                    labelCounts.TryGetValue(l, out int lc);
                    labelCounts[l] = lc + 1;
                }

                var itemGroups = InventoryGroups.GetGroupsForAsset(i.Guid);
                if (itemGroups.Count == 0)
                {
                    ungroupedCount++;
                }
                else
                {
                    seenGroups.Clear();
                    for (int idx = 0; idx < itemGroups.Count; idx++)
                    {
                        string gp = itemGroups[idx];
                        while (!string.IsNullOrEmpty(gp))
                        {
                            seenGroups.Add(gp);
                            gp = InventoryGroups.GetParentPath(gp);
                        }
                    }
                    foreach (var gp in seenGroups)
                    {
                        groupCounts.TryGetValue(gp, out int gc);
                        groupCounts[gp] = gc + 1;
                    }
                }
            }
        }

        void DrawSidebar(Rect rect)
        {
            GUILayout.BeginArea(rect);
            sidebarScroll = GUILayout.BeginScrollView(sidebarScroll);

            DrawGroupsSidebar();

            foldTypes = EditorGUILayout.BeginFoldoutHeaderGroup(foldTypes, "Types");
            if (foldTypes)
            {
                if (SidebarRow("All", InventoryIndex.Items.Count, categoryMask == 0, null))
                { categoryMask = 0; filterDirty = true; }

                foreach (AssetCategory cat in Enum.GetValues(typeof(AssetCategory)))
                {
                    if (!categoryCounts.TryGetValue(cat, out int count)) continue;
                    int bit = 1 << (int)cat;
                    if (SidebarRow(ObjectNames.NicifyVariableName(cat.ToString()), count,
                            (categoryMask & bit) != 0, CategoryIcon(cat)))
                    {
                        // Ctrl/Cmd-click toggles multiple, plain click selects exclusively.
                        if (Event.current.control || Event.current.command) categoryMask ^= bit;
                        else categoryMask = categoryMask == bit ? 0 : bit;
                        filterDirty = true;
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            foldFolders = EditorGUILayout.BeginFoldoutHeaderGroup(foldFolders, "Folders");
            if (foldFolders)
            {
                var folderIcon = EditorGUIUtility.IconContent("Folder Icon").image;
                if (SidebarRow("All", InventoryIndex.Items.Count, string.IsNullOrEmpty(folderFilter), null))
                { folderFilter = ""; filterDirty = true; }

                // A deeper folder filter (set from the details panel) is shown as its own row.
                if (!string.IsNullOrEmpty(folderFilter) && !folderCounts.ContainsKey(folderFilter))
                    SidebarRow(folderFilter, filtered.Count, true, folderIcon);

                foreach (var kv in folderCounts)
                {
                    string display = kv.Key.Substring(kv.Key.IndexOf('/') + 1);
                    if (kv.Key.IndexOf('/') < 0) display = kv.Key + " (root)";
                    if (SidebarRow(display, kv.Value, folderFilter == kv.Key, folderIcon))
                    {
                        folderFilter = folderFilter == kv.Key ? "" : kv.Key;
                        filterDirty = true;
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            if (labelCounts.Count > 0)
            {
                foldLabels = EditorGUILayout.BeginFoldoutHeaderGroup(foldLabels, "Labels");
                if (foldLabels)
                {
                    foreach (var kv in labelCounts)
                    {
                        if (SidebarRow(kv.Key, kv.Value, labelFilter == kv.Key, null))
                        {
                            labelFilter = labelFilter == kv.Key ? "" : kv.Key;
                            filterDirty = true;
                        }
                    }
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            foldFilters = EditorGUILayout.BeginFoldoutHeaderGroup(foldFilters, "Filters");
            if (foldFilters)
            {
                EditorGUI.BeginChangeCheck();
                minSizeIndex = EditorGUILayout.Popup(minSizeIndex, MinSizeLabels);
                bool newUnused = EditorGUILayout.ToggleLeft(
                    new GUIContent("Unreferenced only", "Assets no other asset depends on. Scenes, scripts, Resources, Editor and Settings folders are ignored."),
                    unusedOnly);
                if (newUnused && !InventoryIndex.HasReferenceData)
                {
                    if (EditorUtility.DisplayDialog("Project Inventory",
                            "Finding unreferenced assets requires a reference scan of the whole project. Run it now?", "Scan", "Cancel"))
                        InventoryIndex.BuildReferenceIndex();
                    newUnused = InventoryIndex.HasReferenceData;
                }
                unusedOnly = newUnused;
                if (EditorGUI.EndChangeCheck()) filterDirty = true;

                GUILayout.Space(4);
                if (GUILayout.Button("Clear Filters")) { ClearFilters(); filterDirty = true; }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawGroupsSidebar()
        {
            foldGroups = EditorGUILayout.BeginFoldoutHeaderGroup(foldGroups, "Groups");
            if (!foldGroups)
            {
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            var folderIcon = EditorGUIUtility.IconContent("Folder Icon").image;
            if (SidebarRow("All", InventoryIndex.Items.Count, string.IsNullOrEmpty(groupFilter) && excludedGroups.Count == 0, null))
            {
                groupFilter = "";
                excludedGroups.Clear();
                filterDirty = true;
            }

            if (InventoryGroups.GroupCount > 0)
            {
                if (SidebarRow("Ungrouped", ungroupedCount, groupFilter == UngroupedFilter, null))
                {
                    groupFilter = groupFilter == UngroupedFilter ? "" : UngroupedFilter;
                    filterDirty = true;
                }
            }

            foreach (var groupPath in InventoryGroups.AllGroups.ToList())
            {
                if (IsAncestorCollapsed(groupPath)) continue;

                if (string.Equals(renamingGroupPath, groupPath, StringComparison.OrdinalIgnoreCase))
                {
                    DrawInlineGroupEditor(isRename: true);
                    continue;
                }

                groupCounts.TryGetValue(groupPath, out int count);
                bool selected = string.Equals(groupFilter, groupPath, StringComparison.OrdinalIgnoreCase);
                bool excluded = IsGroupExcluded(groupPath);
                int depth = InventoryGroups.GetDepth(groupPath);
                bool hasChildren = InventoryGroups.HasSubgroups(groupPath);
                bool isCollapsed = collapsedGroups.Any(c => string.Equals(c, groupPath, StringComparison.OrdinalIgnoreCase));

                GroupSidebarRow(groupPath, count, selected, excluded, depth, hasChildren, !isCollapsed, folderIcon);
            }

            if (isCreatingGroup)
            {
                DrawInlineGroupEditor(isRename: false);
            }
            else
            {
                var btnRect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
                btnRect.xMin += 6;
                btnRect.xMax -= 6;
                btnRect.yMin += 2;

                HandleDropOnNewGroupButton(btnRect);

                if (GUI.Button(btnRect, new GUIContent("+ New Group", "Create an inventory-only group or nested folder (e.g. Environment/Park). Drag assets here to group them."), EditorStyles.miniButton))
                {
                    StartCreateGroup("", null);
                }
            }

            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        bool IsAncestorCollapsed(string groupPath)
        {
            string parent = InventoryGroups.GetParentPath(groupPath);
            while (!string.IsNullOrEmpty(parent))
            {
                if (collapsedGroups.Any(c => string.Equals(c, parent, StringComparison.OrdinalIgnoreCase)))
                    return true;
                parent = InventoryGroups.GetParentPath(parent);
            }
            return false;
        }

        void ToggleGroupCollapsed(string groupPath, bool collapse)
        {
            collapsedGroups.RemoveAll(c => string.Equals(c, groupPath, StringComparison.OrdinalIgnoreCase));
            if (collapse) collapsedGroups.Add(groupPath);
        }

        void ExpandAncestors(string groupPath)
        {
            string parent = InventoryGroups.GetParentPath(groupPath);
            while (!string.IsNullOrEmpty(parent))
            {
                ToggleGroupCollapsed(parent, false);
                parent = InventoryGroups.GetParentPath(parent);
            }
        }

        bool IsGroupExcluded(string groupPath) =>
            excludedGroups.Any(g => string.Equals(g, groupPath, StringComparison.OrdinalIgnoreCase));

        void ToggleGroupExcluded(string groupPath)
        {
            int removed = excludedGroups.RemoveAll(g => string.Equals(g, groupPath, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                excludedGroups.Add(groupPath);
                if (string.Equals(groupFilter, groupPath, StringComparison.OrdinalIgnoreCase))
                    groupFilter = "";
            }
            filterDirty = true;
        }

        void GroupSidebarRow(string groupPath, int count, bool selected, bool excluded, int depth, bool hasChildren, bool expanded, Texture icon)
        {
            var r = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
            var e = Event.current;
            bool isHover = r.Contains(e.mousePosition);

            bool isDragHover = (e.type == EventType.DragUpdated || e.type == EventType.DragPerform) &&
                               isHover && HasDraggableAssets();

            if (isDragHover) EditorGUI.DrawRect(r, SelectionColor(0.65f));
            else if (selected) EditorGUI.DrawRect(r, SelectionColor(0.45f));
            else if (excluded) EditorGUI.DrawRect(r, new Color(0.75f, 0.22f, 0.22f, 0.42f));
            else if (isHover) EditorGUI.DrawRect(r, new Color(1, 1, 1, 0.05f));

            float x = r.x + 6 + depth * 12f;
            var foldRect = new Rect(x, r.y + 1, 12, 16);
            if (hasChildren)
            {
                GUI.Label(foldRect, expanded ? "▾" : "▸", EditorStyles.miniLabel);
            }
            x += 12;

            if (icon != null)
            {
                GUI.DrawTexture(new Rect(x, r.y + 1, 16, 16), icon, ScaleMode.ScaleToFit);
                x += 18;
            }

            bool showExcludeBtn = excluded || (isHover && !isDragHover);
            var excludeBtnRect = new Rect(r.xMax - 18, r.y + 2, 14, 14);
            float rightMargin = showExcludeBtn ? 54f : 44f;

            string leaf = InventoryGroups.GetLeafName(groupPath);
            string displayLabel = excluded ? "− " + leaf : leaf;
            string tooltip = $"{groupPath}\nClick to filter · Alt-click or '−' to exclude";
            GUI.Label(new Rect(x, r.y, r.xMax - x - rightMargin, r.height), new GUIContent(displayLabel, tooltip));
            GUI.Label(new Rect(r.xMax - rightMargin - 4, r.y, 40, r.height), count.ToString(), countStyle);

            if (showExcludeBtn)
            {
                if (excluded || excludeBtnRect.Contains(e.mousePosition))
                    EditorGUI.DrawRect(excludeBtnRect, excluded ? new Color(0.85f, 0.25f, 0.25f, 0.85f) : new Color(1f, 1f, 1f, 0.15f));
                GUI.Label(excludeBtnRect, new GUIContent("−", excluded ? "Stop excluding this group" : "Exclude this group"), badgeStyle);
            }

            if (isDragHover)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    var guids = InventoryGroups.ExpandPathsToGuids(DragAndDrop.paths);
                    InventoryGroups.AddAssets(groupPath, guids);
                }
                e.Use();
                Repaint();
                return;
            }

            if (e.type == EventType.MouseDown && isHover)
            {
                if (e.button == 0)
                {
                    if (hasChildren && foldRect.Contains(e.mousePosition))
                    {
                        ToggleGroupCollapsed(groupPath, expanded);
                    }
                    else if (showExcludeBtn && excludeBtnRect.Contains(e.mousePosition))
                    {
                        ToggleGroupExcluded(groupPath);
                    }
                    else if (e.alt)
                    {
                        ToggleGroupExcluded(groupPath);
                    }
                    else if (e.clickCount == 2)
                    {
                        StartRenameGroup(groupPath);
                    }
                    else
                    {
                        excludedGroups.RemoveAll(g => string.Equals(g, groupPath, StringComparison.OrdinalIgnoreCase));
                        groupFilter = selected ? "" : groupPath;
                        filterDirty = true;
                    }
                    e.Use();
                }
                else if (e.button == 1)
                {
                    ShowGroupSidebarMenu(groupPath);
                    e.Use();
                }
            }
        }

        void HandleDropOnNewGroupButton(Rect rect)
        {
            var e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !rect.Contains(e.mousePosition) || !HasDraggableAssets())
                return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                var guids = InventoryGroups.ExpandPathsToGuids(DragAndDrop.paths);
                StartCreateGroup("", guids);
            }
            e.Use();
            Repaint();
        }

        static bool HasDraggableAssets() =>
            DragAndDrop.paths != null && DragAndDrop.paths.Length > 0;

        void StartCreateGroup(string initialPath, List<string> guidsToAdd)
        {
            foldGroups = true;
            renamingGroupPath = null;
            isCreatingGroup = true;
            editingGroupText = initialPath ?? "";
            pendingGroupAddGuids = guidsToAdd;
            focusGroupField = true;
            Repaint();
        }

        void StartRenameGroup(string groupPath)
        {
            foldGroups = true;
            isCreatingGroup = false;
            renamingGroupPath = groupPath;
            editingGroupText = groupPath;
            pendingGroupAddGuids = null;
            focusGroupField = true;
            Repaint();
        }

        void CancelInlineGroupEdit()
        {
            isCreatingGroup = false;
            renamingGroupPath = null;
            editingGroupText = "";
            pendingGroupAddGuids = null;
            GUIUtility.keyboardControl = 0;
            Repaint();
        }

        void ConfirmInlineGroupEdit()
        {
            string target = InventoryGroups.NormalizePath(editingGroupText);
            if (string.IsNullOrEmpty(target))
            {
                CancelInlineGroupEdit();
                return;
            }

            if (renamingGroupPath != null)
            {
                string old = renamingGroupPath;
                string prevFilter = groupFilter;
                var prevExcluded = excludedGroups.ToList();
                if (InventoryGroups.RenameGroup(old, target))
                {
                    ExpandAncestors(target);
                    if (string.Equals(prevFilter, old, StringComparison.OrdinalIgnoreCase))
                        groupFilter = target;
                    else if (prevFilter.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase))
                        groupFilter = target + prevFilter.Substring(old.Length);

                    excludedGroups = prevExcluded.Select(ex =>
                    {
                        if (string.Equals(ex, old, StringComparison.OrdinalIgnoreCase)) return target;
                        if (ex.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase)) return target + ex.Substring(old.Length);
                        return ex;
                    }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    filterDirty = true;
                }
            }
            else
            {
                InventoryGroups.CreateGroup(target);
                if (pendingGroupAddGuids != null && pendingGroupAddGuids.Count > 0)
                    InventoryGroups.AddAssets(target, pendingGroupAddGuids);
                ExpandAncestors(target);
                groupFilter = target;
                filterDirty = true;
            }

            CancelInlineGroupEdit();
        }

        void DrawInlineGroupEditor(bool isRename)
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.Use();
                    ConfirmInlineGroupEdit();
                    return;
                }
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    CancelInlineGroupEdit();
                    return;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(6);
                GUI.SetNextControlName("InventoryGroupInlineField");
                editingGroupText = EditorGUILayout.TextField(editingGroupText);
                if (focusGroupField)
                {
                    EditorGUI.FocusTextInControl("InventoryGroupInlineField");
                    focusGroupField = false;
                }
                if (GUILayout.Button(new GUIContent("✓", isRename ? "Rename group" : "Create group"), EditorStyles.miniButton, GUILayout.Width(22)))
                    ConfirmInlineGroupEdit();
                if (GUILayout.Button(new GUIContent("×", "Cancel"), EditorStyles.miniButton, GUILayout.Width(22)))
                    CancelInlineGroupEdit();
                GUILayout.Space(4);
            }
        }

        void ShowGroupSidebarMenu(string groupPath)
        {
            var menu = new GenericMenu();
            bool isExcluded = IsGroupExcluded(groupPath);
            menu.AddItem(new GUIContent("Exclude Group"), isExcluded, () => ToggleGroupExcluded(groupPath));
            menu.AddSeparator("");
            if (selection.Count > 0)
            {
                var selectedGuids = selection.ToList();
                menu.AddItem(new GUIContent($"Add Selected Assets ({selectedGuids.Count})"), false,
                    () => InventoryGroups.AddAssets(groupPath, selectedGuids));
                menu.AddItem(new GUIContent("Remove Selected Assets"), false,
                    () => InventoryGroups.RemoveAssets(groupPath, selectedGuids, includeSubgroups: false));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Add Selected Assets"));
                menu.AddDisabledItem(new GUIContent("Remove Selected Assets"));
            }

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("New Sub-group…"), false, () => StartCreateGroup(groupPath + "/", null));
            menu.AddItem(new GUIContent("Rename…"), false, () => StartRenameGroup(groupPath));
            menu.AddItem(new GUIContent("Clear Assets"), false, () => InventoryGroups.ClearGroup(groupPath, includeSubgroups: false));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete Group…"), false, () =>
            {
                bool hasSub = InventoryGroups.HasSubgroups(groupPath);
                string msg = hasSub
                    ? $"Delete group \"{groupPath}\" and all its sub-groups?\n\n(Project assets will not be modified or deleted.)"
                    : $"Delete group \"{groupPath}\"?\n\n(Project assets will not be modified or deleted.)";
                if (EditorUtility.DisplayDialog("Delete Inventory Group", msg, "Delete", "Cancel"))
                    InventoryGroups.DeleteGroup(groupPath);
            });
            menu.ShowAsContext();
        }

        bool SidebarRow(string label, int count, bool selected, Texture icon)
        {
            var r = GUILayoutUtility.GetRect(0, 18, GUILayout.ExpandWidth(true));
            if (selected) EditorGUI.DrawRect(r, SelectionColor(0.45f));
            else if (r.Contains(Event.current.mousePosition)) EditorGUI.DrawRect(r, new Color(1, 1, 1, 0.05f));

            var x = r.x + 10;
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(x, r.y + 1, 16, 16), icon, ScaleMode.ScaleToFit);
                x += 20;
            }
            GUI.Label(new Rect(x, r.y, r.xMax - x - 44, r.height), label);
            GUI.Label(new Rect(r.xMax - 48, r.y, 44, r.height), count.ToString(), countStyle);

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                e.Use();
                return true;
            }
            return false;
        }

        static Texture CategoryIcon(AssetCategory c)
        {
            Type t = c switch
            {
                AssetCategory.Texture => typeof(Texture2D),
                AssetCategory.Sprite => typeof(Sprite),
                AssetCategory.Model => typeof(Mesh),
                AssetCategory.Prefab => typeof(GameObject),
                AssetCategory.Material => typeof(Material),
                AssetCategory.Shader => typeof(Shader),
                AssetCategory.Audio => typeof(AudioClip),
                AssetCategory.Video => typeof(UnityEngine.Video.VideoClip),
                AssetCategory.Animation => typeof(AnimationClip),
                AssetCategory.Script => typeof(MonoScript),
                AssetCategory.Scene => typeof(SceneAsset),
                AssetCategory.ScriptableObject => typeof(ScriptableObject),
                AssetCategory.Font => typeof(Font),
                AssetCategory.Text => typeof(TextAsset),
                _ => typeof(DefaultAsset)
            };
            return EditorGUIUtility.ObjectContent(null, t).image;
        }

        void ClearFilters()
        {
            searchText = "";
            categoryMask = 0;
            groupFilter = "";
            excludedGroups.Clear();
            folderFilter = "";
            labelFilter = "";
            minSizeIndex = 0;
            unusedOnly = false;
        }

        // ---- filtering ------------------------------------------------------------
        struct Term
        {
            public string Key, Value;
            public bool Negate;
        }

        static List<string> SplitSearchTokens(string input)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return tokens;

            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        void RefreshFilter()
        {
            var terms = new List<Term>();
            foreach (var raw in SplitSearchTokens(searchText))
            {
                var t = new Term { Value = raw };
                if (t.Value.StartsWith("-") && t.Value.Length > 1) { t.Negate = true; t.Value = t.Value.Substring(1); }
                int colon = t.Value.IndexOf(':');
                if (colon > 0 && colon < t.Value.Length - 1)
                {
                    t.Key = t.Value.Substring(0, colon).ToLowerInvariant();
                    t.Value = t.Value.Substring(colon + 1);
                }
                terms.Add(t);
            }

            long minSize = MinSizeValues[Mathf.Clamp(minSizeIndex, 0, MinSizeValues.Length - 1)];
            filtered = InventoryIndex.Items.Where(i => Matches(i, terms, minSize)).ToList();

            Comparison<InventoryItem> cmp = sortMode switch
            {
                SortMode.Type => (a, b) =>
                {
                    int c = a.Category.CompareTo(b.Category);
                    return c != 0 ? c : EditorUtility.NaturalCompare(a.Name, b.Name);
                },
                SortMode.Size => (a, b) => a.Size.CompareTo(b.Size),
                SortMode.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
                SortMode.Path => (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
                SortMode.UsedBy => (a, b) => a.UsedByCount.CompareTo(b.UsedByCount),
                _ => (a, b) => EditorUtility.NaturalCompare(a.Name, b.Name)
            };
            filtered.Sort(cmp);
            if (sortDescending) filtered.Reverse();

            filterDirty = false;
            lastClickIndex = -1;
        }

        bool Matches(InventoryItem i, List<Term> terms, long minSize)
        {
            if (categoryMask != 0 && (categoryMask & (1 << (int)i.Category)) == 0) return false;
            if (!string.IsNullOrEmpty(groupFilter))
            {
                if (groupFilter == UngroupedFilter)
                {
                    if (!InventoryGroups.IsUngrouped(i.Guid)) return false;
                }
                else if (!InventoryGroups.Contains(groupFilter, i.Guid, includeSubgroups: true))
                {
                    return false;
                }
            }
            for (int exIdx = 0; exIdx < excludedGroups.Count; exIdx++)
            {
                if (InventoryGroups.Contains(excludedGroups[exIdx], i.Guid, includeSubgroups: true))
                    return false;
            }
            if (!string.IsNullOrEmpty(folderFilter) && !i.Path.StartsWith(folderFilter + "/", StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(labelFilter) && Array.IndexOf(i.Labels, labelFilter) < 0) return false;
            if (i.Size < minSize) return false;
            if (unusedOnly && (i.UsedByCount != 0 || InventoryIndex.IsImplicitlyUsed(i))) return false;

            const StringComparison oic = StringComparison.OrdinalIgnoreCase;
            foreach (var t in terms)
            {
                bool hit = t.Key switch
                {
                    "t" => i.Category.ToString().IndexOf(t.Value, oic) >= 0 || i.TypeName.IndexOf(t.Value, oic) >= 0,
                    "g" or "group" => InventoryGroups.GetGroupsForAsset(i.Guid).Any(g => g.IndexOf(t.Value, oic) >= 0),
                    "l" => i.Labels.Any(l => l.IndexOf(t.Value, oic) >= 0),
                    "ext" => string.Equals(i.Extension, t.Value.TrimStart('.'), oic),
                    "dir" => i.Folder.IndexOf(t.Value, oic) >= 0,
                    _ => i.Path.IndexOf(t.Key == null ? t.Value : t.Key + ":" + t.Value, oic) >= 0
                };
                if (hit == t.Negate) return false;
            }
            return true;
        }

        // ---- results ------------------------------------------------------------------
        void DrawResults(Rect area)
        {
            if (filtered.Count == 0)
            {
                GUI.Label(area, InventoryIndex.Items.Count == 0 ? "Indexing…" : "No assets match the current filters.",
                    new GUIStyle(EditorStyles.centeredGreyMiniLabel) { fontSize = 12 });
                return;
            }

            if (gridMode) DrawGrid(area);
            else DrawList(area);
        }

        void DrawGrid(Rect area)
        {
            float labelH = showTitles ? 16f : 0f;
            float cellW = tileSize + Spacing;
            float cellH = tileSize + labelH + Spacing;
            float viewW = area.width - 14f;
            int cols = Mathf.Max(1, Mathf.FloorToInt((viewW - Spacing) / cellW));
            int rows = Mathf.CeilToInt(filtered.Count / (float)cols);
            float offsetX = Mathf.Max(0, (viewW - Spacing - cols * cellW) * 0.5f);

            lastCols = cols;
            lastCellHeight = cellH;
            lastViewHeight = area.height;

            var content = new Rect(0, 0, viewW, rows * cellH + Spacing);
            resultsScroll = GUI.BeginScrollView(area, resultsScroll, content, false, true);

            int firstRow = Mathf.Max(0, Mathf.FloorToInt(resultsScroll.y / cellH));
            int lastRow = Mathf.Min(rows - 1, Mathf.CeilToInt((resultsScroll.y + area.height) / cellH));
            int loadBudget = 16; // limit synchronous asset loads per repaint

            for (int r = firstRow; r <= lastRow; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (idx >= filtered.Count) break;
                    var rect = new Rect(Spacing + offsetX + c * cellW, Spacing + r * cellH, tileSize, tileSize + labelH);
                    DrawTile(rect, filtered[idx], idx, labelH, ref loadBudget);
                }
            }

            HandleBackgroundEvents(content);
            GUI.EndScrollView();
        }

        void DrawTile(Rect rect, InventoryItem item, int index, float labelH, ref int budget)
        {
            bool selected = selection.Contains(item.Guid);
            var previewRect = new Rect(rect.x, rect.y, rect.width, rect.width);

            if (Event.current.type == EventType.Repaint)
            {
                if (selected) EditorGUI.DrawRect(new Rect(rect.x - 3, rect.y - 3, rect.width + 6, rect.height + 5), SelectionColor(0.6f));
                EditorGUI.DrawRect(previewRect, new Color(0, 0, 0, 0.18f));

                var tex = GetThumbnail(item, ref budget, out bool isIcon);
                if (tex != null)
                {
                    var r = isIcon ? Center(previewRect, Mathf.Min(64, rect.width * 0.5f)) : previewRect;
                    GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit);
                }

                if (rect.width >= 64 && !string.IsNullOrEmpty(item.Extension))
                {
                    var b = new Rect(previewRect.xMax - 30, previewRect.y + 2, 28, 12);
                    EditorGUI.DrawRect(b, new Color(0, 0, 0, 0.55f));
                    GUI.Label(b, item.Extension.ToUpperInvariant(), badgeStyle);
                }

                if (unusedOnly || (InventoryIndex.HasReferenceData && item.UsedByCount == 0 && !InventoryIndex.IsImplicitlyUsed(item)))
                {
                    var b = new Rect(previewRect.x + 2, previewRect.y + 2, 12, 12);
                    EditorGUI.DrawRect(b, new Color(0.85f, 0.45f, 0.1f, 0.9f));
                }

                if (labelH > 0)
                    GUI.Label(new Rect(rect.x - 2, previewRect.yMax + 1, rect.width + 4, labelH), item.Name, tileLabelStyle);
            }

            GUI.Label(rect, new GUIContent("", $"{item.Path}\n{item.TypeName} · {InventoryItem.FormatSize(item.Size)}"), GUIStyle.none);
            HandleItemEvents(rect, item, index);
        }

        void DrawList(Rect area)
        {
            float viewW = area.width - 14f;
            lastCols = 1;
            lastCellHeight = ListRowHeight;
            lastViewHeight = area.height - ListRowHeight;

            // header
            var header = new Rect(area.x, area.y, area.width, ListRowHeight);
            float nameW;
            float[] widths;
            string[] titles;
            SortMode[] modes;

            if (viewW < 280f)
            {
                float sizeW = 60f;
                nameW = Mathf.Max(80f, viewW - sizeW);
                widths = new[] { nameW, sizeW };
                titles = new[] { "Name", "Size" };
                modes = new[] { SortMode.Name, SortMode.Size };
            }
            else if (viewW < 440f)
            {
                float typeW = 80f;
                float sizeW = 60f;
                nameW = Mathf.Max(90f, viewW - typeW - sizeW);
                widths = new[] { nameW, typeW, sizeW };
                titles = new[] { "Name", "Type", "Size" };
                modes = new[] { SortMode.Name, SortMode.Type, SortMode.Size };
            }
            else
            {
                nameW = Mathf.Max(120f, viewW * 0.35f);
                float typeW = 100f;
                float sizeW = 65f;
                float modW = 110f;
                float folderW = Mathf.Max(50f, viewW - nameW - typeW - sizeW - modW);
                widths = new[] { nameW, typeW, sizeW, modW, folderW };
                titles = new[] { "Name", "Type", "Size", "Modified", "Folder" };
                modes = new[] { SortMode.Name, SortMode.Type, SortMode.Size, SortMode.Modified, SortMode.Path };
            }

            GUI.Box(header, GUIContent.none, EditorStyles.toolbar);
            float hx = header.x + 4;
            for (int i = 0; i < titles.Length; i++)
            {
                string t = titles[i] + (sortMode == modes[i] ? (sortDescending ? " ▼" : " ▲") : "");
                if (GUI.Button(new Rect(hx, header.y, widths[i], header.height), t, EditorStyles.toolbarButton))
                {
                    if (sortMode == modes[i]) sortDescending = !sortDescending;
                    else { sortMode = modes[i]; sortDescending = false; }
                    filterDirty = true;
                }
                hx += widths[i];
            }

            var body = new Rect(area.x, area.y + ListRowHeight, area.width, area.height - ListRowHeight);
            var content = new Rect(0, 0, viewW, filtered.Count * ListRowHeight);
            resultsScroll = GUI.BeginScrollView(body, resultsScroll, content, false, true);

            int first = Mathf.Max(0, Mathf.FloorToInt(resultsScroll.y / ListRowHeight));
            int last = Mathf.Min(filtered.Count - 1, first + Mathf.CeilToInt(body.height / ListRowHeight) + 1);

            for (int idx = first; idx <= last; idx++)
            {
                var item = filtered[idx];
                var row = new Rect(0, idx * ListRowHeight, viewW, ListRowHeight);
                if (Event.current.type == EventType.Repaint)
                {
                    if (selection.Contains(item.Guid)) EditorGUI.DrawRect(row, SelectionColor(0.55f));
                    else if (idx % 2 == 1) EditorGUI.DrawRect(row, new Color(0, 0, 0, 0.06f));

                    float x = row.x + 4;
                    var icon = AssetDatabase.GetCachedIcon(item.Path);
                    if (icon != null) GUI.DrawTexture(new Rect(x, row.y + 2, 16, 16), icon, ScaleMode.ScaleToFit);
                    GUI.Label(new Rect(x + 20, row.y, widths[0] - 22, row.height), item.Name);
                    x += widths[0];

                    if (widths.Length == 2)
                    {
                        GUI.Label(new Rect(x, row.y, widths[1], row.height), InventoryItem.FormatSize(item.Size), EditorStyles.miniLabel);
                    }
                    else if (widths.Length == 3)
                    {
                        GUI.Label(new Rect(x, row.y, widths[1], row.height), ObjectNames.NicifyVariableName(item.Category.ToString()), EditorStyles.miniLabel);
                        x += widths[1];
                        GUI.Label(new Rect(x, row.y, widths[2], row.height), InventoryItem.FormatSize(item.Size), EditorStyles.miniLabel);
                    }
                    else
                    {
                        GUI.Label(new Rect(x, row.y, widths[1], row.height), ObjectNames.NicifyVariableName(item.Category.ToString()), EditorStyles.miniLabel);
                        x += widths[1];
                        GUI.Label(new Rect(x, row.y, widths[2], row.height), InventoryItem.FormatSize(item.Size), EditorStyles.miniLabel);
                        x += widths[2];
                        GUI.Label(new Rect(x, row.y, widths[3], row.height), item.Modified.ToString("yyyy-MM-dd HH:mm"), EditorStyles.miniLabel);
                        x += widths[3];
                        GUI.Label(new Rect(x, row.y, widths[4], row.height), item.Folder, EditorStyles.miniLabel);
                    }
                }
                HandleItemEvents(row, item, idx);
            }

            HandleBackgroundEvents(content);
            GUI.EndScrollView();
        }

        Texture GetThumbnail(InventoryItem item, ref int budget, out bool isIcon)
        {
            isIcon = false;
            if (thumbs.TryGetValue(item.Guid, out var cached) && cached != null) return cached;

            bool canPreview = !noPreview.Contains(item.Guid) && item.Category is AssetCategory.Texture or AssetCategory.Sprite
                or AssetCategory.Model or AssetCategory.Prefab or AssetCategory.Material or AssetCategory.Audio
                or AssetCategory.Font or AssetCategory.Other or AssetCategory.ScriptableObject;

            if (canPreview && budget > 0)
            {
                budget--;
                var obj = AssetDatabase.LoadMainAssetAtPath(item.Path);
                if (obj != null)
                {
                    // Large tiles of plain textures look better drawn directly than from the 128px preview.
                    if (tileSize > 128 && obj is Texture2D t2d && item.Category == AssetCategory.Texture)
                    {
                        thumbs[item.Guid] = t2d;
                        return t2d;
                    }

                    var preview = AssetPreview.GetAssetPreview(obj);
                    if (preview != null)
                    {
                        thumbs[item.Guid] = preview;
                        return preview;
                    }
                    if (AssetPreview.IsLoadingAssetPreviews()) needsRepaint = true;
                    else noPreview.Add(item.Guid);
                }
                else noPreview.Add(item.Guid);
            }
            else if (canPreview) needsRepaint = true;

            isIcon = true;
            return AssetDatabase.GetCachedIcon(item.Path);
        }

        static Rect Center(Rect r, float size) =>
            new Rect(r.center.x - size * 0.5f, r.center.y - size * 0.5f, size, size);

        static Color SelectionColor(float a) =>
            EditorGUIUtility.isProSkin ? new Color(0.17f, 0.36f, 0.53f, a / 0.6f) : new Color(0.23f, 0.45f, 0.83f, a);

        // ---- selection & input ------------------------------------------------------------
        void HandleItemEvents(Rect rect, InventoryItem item, int index)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || !rect.Contains(e.mousePosition)) return;

            if (e.button == 0)
            {
                if (e.shift && lastClickIndex >= 0 && lastClickIndex < filtered.Count)
                {
                    int a = Mathf.Min(lastClickIndex, index), b = Mathf.Max(lastClickIndex, index);
                    if (!EditorGUI.actionKey) selection.Clear();
                    for (int i = a; i <= b; i++)
                        if (!selection.Contains(filtered[i].Guid)) selection.Add(filtered[i].Guid);
                }
                else if (EditorGUI.actionKey)
                {
                    if (!selection.Remove(item.Guid)) selection.Add(item.Guid);
                    lastClickIndex = index;
                }
                else
                {
                    if (selection.Contains(item.Guid)) pendingSingleSelect = item.Guid;
                    else SetSingle(item.Guid);
                    lastClickIndex = index;
                }

                if (e.clickCount == 2) OpenAssets(new[] { item });
                dragCandidate = true;
                dragStart = e.mousePosition;
                OnSelectionChanged();
            }
            else if (e.button == 1)
            {
                if (!selection.Contains(item.Guid)) { SetSingle(item.Guid); lastClickIndex = index; OnSelectionChanged(); }
                ShowContextMenu();
            }
            e.Use();
        }

        void HandleBackgroundEvents(Rect content)
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0 && content.Contains(e.mousePosition):
                    // Clicked empty space (items already consumed their own events).
                    if (!EditorGUI.actionKey && !e.shift) { selection.Clear(); OnSelectionChanged(); }
                    GUIUtility.keyboardControl = 0;
                    e.Use();
                    break;
                case EventType.MouseDrag when dragCandidate && (e.mousePosition - dragStart).sqrMagnitude > 36f:
                    dragCandidate = false;
                    pendingSingleSelect = null;
                    StartDrag();
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (pendingSingleSelect != null)
                    {
                        SetSingle(pendingSingleSelect);
                        OnSelectionChanged();
                    }
                    pendingSingleSelect = null;
                    dragCandidate = false;
                    break;
            }
        }

        void HandleKeyboard()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField || filtered.Count == 0) return;

            int cur = selection.Count > 0 ? filtered.FindIndex(i => i.Guid == selection[selection.Count - 1]) : -1;
            int step = 0;
            switch (e.keyCode)
            {
                case KeyCode.LeftArrow: step = -1; break;
                case KeyCode.RightArrow: step = 1; break;
                case KeyCode.UpArrow: step = -lastCols; break;
                case KeyCode.DownArrow: step = lastCols; break;
                case KeyCode.Home: step = -cur; break;
                case KeyCode.End: step = filtered.Count - 1 - cur; break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    OpenAssets(SelectedItems());
                    e.Use();
                    return;
                case KeyCode.Space:
                    ToggleAudio();
                    e.Use();
                    return;
                case KeyCode.F:
                    PingSelection();
                    e.Use();
                    return;
                case KeyCode.Delete:
                    DeleteSelection();
                    e.Use();
                    return;
                case KeyCode.A when EditorGUI.actionKey:
                    selection = filtered.Select(i => i.Guid).ToList();
                    OnSelectionChanged();
                    e.Use();
                    return;
                default:
                    return;
            }

            int next = Mathf.Clamp(cur < 0 ? 0 : cur + step, 0, filtered.Count - 1);
            SetSingle(filtered[next].Guid);
            lastClickIndex = next;
            ScrollTo(next);
            OnSelectionChanged();
            e.Use();
        }

        void ScrollTo(int index)
        {
            if (index < 0) return;
            float y = (gridMode ? Spacing : 0) + (index / Mathf.Max(1, lastCols)) * lastCellHeight;
            if (y < resultsScroll.y) resultsScroll.y = y;
            else if (y + lastCellHeight > resultsScroll.y + lastViewHeight) resultsScroll.y = y + lastCellHeight - lastViewHeight;
            Repaint();
        }

        void SetSingle(string guid)
        {
            selection.Clear();
            selection.Add(guid);
        }

        void OnSelectionChanged()
        {
            if (syncSelection)
                Selection.objects = SelectedItems().Select(i => AssetDatabase.LoadMainAssetAtPath(i.Path)).Where(o => o != null).ToArray();

            if (autoPlayAudio)
            {
                InventoryAudio.Stop();
                var items = SelectedItems();
                if (items.Count == 1 && items[0].Category == AssetCategory.Audio)
                    InventoryAudio.Play(AssetDatabase.LoadAssetAtPath<AudioClip>(items[0].Path));
            }
            Repaint();
        }

        List<InventoryItem> SelectedItems() =>
            selection.Select(InventoryIndex.Get).Where(i => i != null).ToList();

        Object[] SelectedObjects() =>
            SelectedItems().Select(i => AssetDatabase.LoadMainAssetAtPath(i.Path)).Where(o => o != null).ToArray();

        void StartDrag()
        {
            var items = SelectedItems();
            if (items.Count == 0) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = SelectedObjects();
            DragAndDrop.paths = items.Select(i => i.Path).ToArray();
            DragAndDrop.StartDrag(items.Count == 1 ? items[0].Name : items.Count + " assets");
        }

        // ---- actions ----------------------------------------------------------------
        void ShowContextMenu()
        {
            var items = SelectedItems();
            if (items.Count == 0) return;
            var guids = items.Select(i => i.Guid).ToList();
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Open"), false, () => OpenAssets(items));
            menu.AddItem(new GUIContent("Ping in Project"), false, PingSelection);
            menu.AddItem(new GUIContent("Select in Project"), false, () => Selection.objects = SelectedObjects());
            menu.AddItem(new GUIContent(Application.platform == RuntimePlatform.OSXEditor ? "Reveal in Finder" : "Show in Explorer"),
                false, () => EditorUtility.RevealInFinder(items[0].Path));
            menu.AddSeparator("");

            foreach (var g in InventoryGroups.AllGroups)
            {
                string groupPath = g;
                string menuLabel = "Groups/" + groupPath.Replace("/", " ▸ ");
                bool allInGroup = items.All(i => InventoryGroups.Contains(groupPath, i.Guid, includeSubgroups: false));
                menu.AddItem(new GUIContent(menuLabel), allInGroup, () =>
                {
                    if (allInGroup) InventoryGroups.RemoveAssets(groupPath, guids, includeSubgroups: false);
                    else InventoryGroups.AddAssets(groupPath, guids);
                });
            }
            if (InventoryGroups.GroupCount > 0) menu.AddSeparator("Groups/");
            menu.AddItem(new GUIContent("Groups/New Group with Selection…"), false, () => StartCreateGroup("", guids));

            if (!string.IsNullOrEmpty(groupFilter) && groupFilter != UngroupedFilter)
            {
                string activeGroup = groupFilter;
                menu.AddItem(new GUIContent($"Remove from \"{activeGroup.Replace("/", " ▸ ")}\""), false,
                    () => InventoryGroups.RemoveAssets(activeGroup, guids, includeSubgroups: true));
            }

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Copy Path"), false, () => EditorGUIUtility.systemCopyBuffer = string.Join("\n", items.Select(i => i.Path)));
            menu.AddItem(new GUIContent("Copy GUID"), false, () => EditorGUIUtility.systemCopyBuffer = string.Join("\n", items.Select(i => i.Guid)));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Filter/Same Folder"), false, () => { folderFilter = items[0].Folder; filterDirty = true; });
            menu.AddItem(new GUIContent("Filter/Same Type"), false, () => { categoryMask = 1 << (int)items[0].Category; filterDirty = true; });
            menu.AddItem(new GUIContent("Filter/Same Extension"), false, () => { searchText = "ext:" + items[0].Extension; filterDirty = true; });
            foreach (var g in InventoryGroups.GetGroupsForAsset(items[0].Guid))
            {
                string gp = g;
                string nice = gp.Replace("/", " ▸ ");
                menu.AddItem(new GUIContent("Filter/Group: " + nice), false,
                    () => { excludedGroups.RemoveAll(x => string.Equals(x, gp, StringComparison.OrdinalIgnoreCase)); groupFilter = gp; filterDirty = true; });
                menu.AddItem(new GUIContent("Filter/Exclude Group: " + nice), IsGroupExcluded(gp),
                    () => ToggleGroupExcluded(gp));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete…"), false, DeleteSelection);
            menu.ShowAsContext();
        }

        static void OpenAssets(IEnumerable<InventoryItem> items)
        {
            foreach (var i in items.Take(5))
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(i.Path);
                if (obj != null) AssetDatabase.OpenAsset(obj);
            }
        }

        void PingSelection()
        {
            var items = SelectedItems();
            if (items.Count == 0) return;
            var obj = AssetDatabase.LoadMainAssetAtPath(items[0].Path);
            if (obj != null) EditorGUIUtility.PingObject(obj);
        }

        void ToggleAudio()
        {
            if (InventoryAudio.IsPlaying()) { InventoryAudio.Stop(); return; }
            var item = SelectedItems().FirstOrDefault(i => i.Category == AssetCategory.Audio);
            if (item != null) InventoryAudio.Play(AssetDatabase.LoadAssetAtPath<AudioClip>(item.Path));
        }

        void DeleteSelection()
        {
            var items = SelectedItems();
            if (items.Count == 0) return;
            string list = string.Join("\n", items.Take(10).Select(i => i.Path)) + (items.Count > 10 ? $"\n…and {items.Count - 10} more" : "");
            if (!EditorUtility.DisplayDialog("Delete Assets", $"Move {items.Count} asset(s) to the trash?\n\n{list}", "Delete", "Cancel"))
                return;
            var failed = new List<string>();
            AssetDatabase.MoveAssetsToTrash(items.Select(i => i.Path).ToArray(), failed);
            if (failed.Count > 0) Debug.LogWarning("Project Inventory: could not delete\n" + string.Join("\n", failed));
            selection.Clear();
        }

        void AddLabel(IEnumerable<InventoryItem> items, string label)
        {
            label = label?.Trim();
            if (string.IsNullOrEmpty(label)) return;
            foreach (var i in items)
            {
                var obj = AssetDatabase.LoadMainAssetAtPath(i.Path);
                if (obj == null || i.Labels.Contains(label)) continue;
                AssetDatabase.SetLabels(obj, i.Labels.Append(label).ToArray());
                InventoryIndex.RefreshLabels(i);
            }
            newLabel = "";
        }

        void RemoveLabel(InventoryItem i, string label)
        {
            var obj = AssetDatabase.LoadMainAssetAtPath(i.Path);
            if (obj == null) return;
            AssetDatabase.SetLabels(obj, i.Labels.Where(l => l != label).ToArray());
            InventoryIndex.RefreshLabels(i);
        }

        // ---- details ------------------------------------------------------------------
        void DestroyPreviewEditor()
        {
            if (previewEditor != null) DestroyImmediate(previewEditor);
            previewEditor = null;
        }

        void EnsureDetails(InventoryItem item)
        {
            if (detailsGuid == item.Guid && detailsObject != null) return;
            detailsGuid = item.Guid;
            DestroyPreviewEditor();
            extraInfo.Clear();
            dependencyPaths.Clear();
            previewText = null;
            textPreviewScroll = Vector2.zero;

            detailsObject = AssetDatabase.LoadMainAssetAtPath(item.Path);
            if (detailsObject == null) return;

            if (item.Category is AssetCategory.Script or AssetCategory.Text or AssetCategory.Shader && item.Size < 512 * 1024)
            {
                try { previewText = string.Join("\n", File.ReadLines(item.Path).Take(120)); }
                catch { previewText = null; }
            }
            else if (item.Category != AssetCategory.Scene)
            {
                previewEditor = UnityEditor.Editor.CreateEditor(detailsObject);
                if (previewEditor != null && !previewEditor.HasPreviewGUI()) DestroyPreviewEditor();
            }

            dependencyPaths.AddRange(AssetDatabase.GetDependencies(item.Path, false)
                .Where(p => p != item.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

            CollectExtraInfo(item, detailsObject);
        }

        void Info(string k, string v) => extraInfo.Add(new KeyValuePair<string, string>(k, v));

        void CollectExtraInfo(InventoryItem item, Object obj)
        {
            var importer = AssetImporter.GetAtPath(item.Path);
            switch (obj)
            {
                case Texture tex:
                    Info("Dimensions", $"{tex.width} × {tex.height}");
                    if (tex is Texture2D t2d) Info("Format", t2d.format.ToString());
                    if (importer is TextureImporter ti)
                    {
                        Info("Import Type", ti.textureType.ToString());
                        Info("Max Size", ti.maxTextureSize.ToString());
                        Info("Compression", ti.textureCompression.ToString());
                        Info("Mip Maps", ti.mipmapEnabled ? "Yes" : "No");
                    }
                    break;
                case AudioClip clip:
                    Info("Length", TimeSpan.FromSeconds(clip.length).ToString(@"m\:ss\.ff"));
                    Info("Channels", clip.channels.ToString());
                    Info("Frequency", clip.frequency + " Hz");
                    if (importer is AudioImporter ai) Info("Load Type", ai.defaultSampleSettings.loadType.ToString());
                    break;
                case Material mat:
                    Info("Shader", mat.shader != null ? mat.shader.name : "None");
                    Info("Render Queue", mat.renderQueue.ToString());
                    break;
                case GameObject go:
                    var meshes = new List<Mesh>();
                    if (item.Category == AssetCategory.Model)
                        meshes.AddRange(AssetDatabase.LoadAllAssetsAtPath(item.Path).OfType<Mesh>());
                    else
                    {
                        meshes.AddRange(go.GetComponentsInChildren<MeshFilter>(true).Select(m => m.sharedMesh).Where(m => m));
                        meshes.AddRange(go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Select(m => m.sharedMesh).Where(m => m));
                        Info("Children", (go.GetComponentsInChildren<Transform>(true).Length - 1).ToString());
                        Info("Components", go.GetComponentsInChildren<Component>(true).Length.ToString());
                    }
                    if (meshes.Count > 0)
                    {
                        long verts = meshes.Sum(m => (long)m.vertexCount);
                        long tris = meshes.Sum(m => Enumerable.Range(0, m.subMeshCount).Sum(s => (long)m.GetIndexCount(s)) / 3);
                        Info("Meshes", meshes.Count.ToString());
                        Info("Vertices", verts.ToString("N0"));
                        Info("Triangles", tris.ToString("N0"));
                    }
                    if (item.Category == AssetCategory.Model)
                    {
                        int clips = AssetDatabase.LoadAllAssetsAtPath(item.Path).OfType<AnimationClip>()
                            .Count(c => !c.name.StartsWith("__preview__"));
                        if (clips > 0) Info("Animations", clips.ToString());
                    }
                    break;
                case AnimationClip anim:
                    Info("Length", anim.length.ToString("0.00") + " s");
                    Info("Frame Rate", anim.frameRate.ToString("0.#"));
                    Info("Looping", anim.isLooping ? "Yes" : "No");
                    break;
                case UnityEngine.Video.VideoClip video:
                    Info("Dimensions", $"{video.width} × {video.height}");
                    Info("Length", TimeSpan.FromSeconds(video.length).ToString(@"m\:ss"));
                    break;
                case MonoScript script:
                    var cls = script.GetClass();
                    if (cls != null)
                    {
                        Info("Class", cls.FullName);
                        if (cls.BaseType != null) Info("Base", cls.BaseType.Name);
                    }
                    break;
                case SceneAsset:
                    bool inBuild = EditorBuildSettings.scenes.Any(s => s.path == item.Path && s.enabled);
                    Info("In Build", inBuild ? "Yes" : "No");
                    break;
                case ScriptableObject so:
                    Info("Script", so.GetType().FullName);
                    break;
            }
        }

        void DrawDetails(Rect rect)
        {
            GUILayout.BeginArea(new Rect(rect.x + 6, rect.y + 4, rect.width - 10, rect.height - 6));
            detailsScroll = GUILayout.BeginScrollView(detailsScroll);
            EditorGUIUtility.labelWidth = 90;

            var items = SelectedItems();
            if (items.Count == 0)
            {
                DestroyPreviewEditor();
                detailsGuid = null;
                EditorGUILayout.HelpBox("Select an asset to see its details.\n\nDouble-click opens, drag to drop into the scene or inspector, right-click for more actions.", MessageType.None);
            }
            else if (items.Count == 1) DrawSingleDetails(items[0], rect.width - 30);
            else DrawMultiDetails(items);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawSingleDetails(InventoryItem item, float width)
        {
            EnsureDetails(item);

            using (new EditorGUILayout.HorizontalScope())
            {
                var icon = AssetDatabase.GetCachedIcon(item.Path);
                if (icon != null) GUILayout.Label(icon, GUILayout.Width(32), GUILayout.Height(32));
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(item.Name, headerStyle);
                    GUILayout.Label($"{ObjectNames.NicifyVariableName(item.Category.ToString())} · {item.TypeName}", EditorStyles.miniLabel);
                }
            }
            GUILayout.Space(4);

            // preview
            float size = Mathf.Clamp(width, 100, 360);
            if (previewEditor != null)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.FlexibleSpace();
                    previewEditor.OnPreviewSettings();
                }
                var r = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.12f));
                previewEditor.OnInteractivePreviewGUI(r, GUIStyle.none);
            }
            else if (previewText != null)
            {
                var r = GUILayoutUtility.GetRect(size, size * 0.9f, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(r, new Color(0.13f, 0.13f, 0.13f));
                var content = new GUIContent(previewText);
                var textSize = monoStyle.CalcSize(content);
                textPreviewScroll = GUI.BeginScrollView(r, textPreviewScroll, new Rect(0, 0, Mathf.Max(r.width - 14, textSize.x + 8), textSize.y + 8));
                var prev = monoStyle.normal.textColor;
                monoStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                GUI.Label(new Rect(4, 4, textSize.x, textSize.y), content, monoStyle);
                monoStyle.normal.textColor = prev;
                GUI.EndScrollView();
            }
            else
            {
                var r = GUILayoutUtility.GetRect(size, size * 0.5f, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(r, new Color(0, 0, 0, 0.15f));
                var icon = AssetDatabase.GetCachedIcon(item.Path);
                if (icon != null) GUI.DrawTexture(Center(r, 64), icon, ScaleMode.ScaleToFit);
            }

            if (item.Category == AssetCategory.Audio)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("▶ Play")) InventoryAudio.Play(detailsObject as AudioClip);
                    if (GUILayout.Button("■ Stop")) InventoryAudio.Stop();
                }
            }

            GUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Ping")) EditorGUIUtility.PingObject(detailsObject);
                if (GUILayout.Button("Select")) Selection.activeObject = detailsObject;
                if (GUILayout.Button("Open")) OpenAssets(new[] { item });
                if (GUILayout.Button("Show")) EditorUtility.RevealInFinder(item.Path);
            }

            GUILayout.Space(6);
            SectionLabel("Info");
            SelectableRow("Path", item.Path);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Folder");
                if (GUILayout.Button(new GUIContent(item.Folder, "Filter by this folder"), EditorStyles.linkLabel))
                {
                    folderFilter = item.Folder;
                    filterDirty = true;
                }
            }
            EditorGUILayout.LabelField("Size", InventoryItem.FormatSize(item.Size));
            EditorGUILayout.LabelField("Modified", item.Modified.ToString("yyyy-MM-dd HH:mm"));
            SelectableRow("GUID", item.Guid);
            foreach (var kv in extraInfo) EditorGUILayout.LabelField(kv.Key, kv.Value);

            GUILayout.Space(6);
            SectionLabel("Groups");
            DrawGroupChips(new[] { item });
            DrawAddGroup(new[] { item });

            GUILayout.Space(6);
            SectionLabel("Labels");
            DrawLabelChips(item);
            DrawAddLabel(new[] { item });

            GUILayout.Space(6);
            foldDeps = EditorGUILayout.Foldout(foldDeps, $"Dependencies ({dependencyPaths.Count})", true);
            if (foldDeps)
            {
                if (dependencyPaths.Count == 0) GUILayout.Label("None", EditorStyles.miniLabel);
                foreach (var p in dependencyPaths) AssetLink(p);
            }

            if (InventoryIndex.HasReferenceData)
            {
                var usedBy = InventoryIndex.GetUsedBy(item.Guid).Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => !string.IsNullOrEmpty(p)).OrderBy(p => p).ToList();
                foldUsedBy = EditorGUILayout.Foldout(foldUsedBy, $"Used By ({usedBy.Count})", true);
                if (foldUsedBy)
                {
                    if (usedBy.Count == 0)
                        GUILayout.Label(InventoryIndex.IsImplicitlyUsed(item) ? "No asset references (implicitly used)" : "Not referenced by any asset",
                            EditorStyles.miniLabel);
                    foreach (var p in usedBy) AssetLink(p);
                }
            }
            else
            {
                GUILayout.Space(2);
                if (GUILayout.Button(new GUIContent("Scan References…", "Build a project-wide reverse dependency map to show which assets use this one.")))
                    ScanReferences();
            }
        }

        void DrawMultiDetails(List<InventoryItem> items)
        {
            GUILayout.Label($"{items.Count} assets selected", headerStyle);
            EditorGUILayout.LabelField("Total Size", InventoryItem.FormatSize(items.Sum(i => i.Size)));
            GUILayout.Space(4);
            foreach (var g in items.GroupBy(i => i.Category).OrderByDescending(g => g.Count()))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(CategoryIcon(g.Key), GUILayout.Width(18), GUILayout.Height(18));
                    GUILayout.Label(ObjectNames.NicifyVariableName(g.Key.ToString()));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(g.Count().ToString(), EditorStyles.miniLabel);
                }
            }

            GUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select in Project")) Selection.objects = SelectedObjects();
                if (GUILayout.Button("Delete…")) DeleteSelection();
            }

            GUILayout.Space(6);
            SectionLabel("Groups");
            DrawGroupChips(items);
            DrawAddGroup(items);

            GUILayout.Space(6);
            SectionLabel("Add Label To All");
            DrawAddLabel(items);
        }

        void DrawGroupChips(IReadOnlyList<InventoryItem> items)
        {
            var sharedGroups = items
                .SelectMany(i => InventoryGroups.GetGroupsForAsset(i.Guid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sharedGroups.Count == 0)
            {
                GUILayout.Label("No groups", EditorStyles.miniLabel);
                return;
            }

            string remove = null;
            string filterTo = null;
            string excludeTo = null;
            foreach (var g in sharedGroups)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(g, "Click to filter by this group · Alt-click to exclude"), EditorStyles.miniButtonLeft, GUILayout.ExpandWidth(false)))
                    {
                        if (Event.current.alt) excludeTo = g;
                        else filterTo = g;
                    }
                    if (GUILayout.Button(new GUIContent("×", "Remove from group"), EditorStyles.miniButtonRight, GUILayout.Width(20)))
                        remove = g;
                    GUILayout.FlexibleSpace();
                }
            }
            if (excludeTo != null)
            {
                ToggleGroupExcluded(excludeTo);
            }
            else if (filterTo != null)
            {
                excludedGroups.RemoveAll(x => string.Equals(x, filterTo, StringComparison.OrdinalIgnoreCase));
                groupFilter = filterTo;
                filterDirty = true;
            }
            if (remove != null)
            {
                InventoryGroups.RemoveAssets(remove, items.Select(i => i.Guid), includeSubgroups: false);
            }
        }

        void DrawAddGroup(IEnumerable<InventoryItem> items)
        {
            var list = items as IList<InventoryItem> ?? items.ToList();
            var e = Event.current;
            bool enterPressed = e.type == EventType.KeyDown &&
                                (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) &&
                                GUI.GetNameOfFocusedControl() == "DetailsAddGroupField";

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.SetNextControlName("DetailsAddGroupField");
                newGroup = EditorGUILayout.TextField(newGroup);
                if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(40)) || enterPressed)
                {
                    if (enterPressed) e.Use();
                    AddGroupToItems(list, newGroup);
                }
                if (InventoryGroups.GroupCount > 0 && GUILayout.Button("▾", EditorStyles.miniButton, GUILayout.Width(20)))
                {
                    var menu = new GenericMenu();
                    foreach (var g in InventoryGroups.AllGroups)
                    {
                        string gp = g;
                        menu.AddItem(new GUIContent(gp.Replace("/", " ▸ ")), false, () => AddGroupToItems(list, gp));
                    }
                    menu.ShowAsContext();
                }
            }
        }

        void AddGroupToItems(IEnumerable<InventoryItem> items, string groupPath)
        {
            groupPath = InventoryGroups.NormalizePath(groupPath);
            if (string.IsNullOrEmpty(groupPath)) return;
            InventoryGroups.AddAssets(groupPath, items.Select(i => i.Guid));
            ExpandAncestors(groupPath);
            newGroup = "";
        }

        void DrawLabelChips(InventoryItem item)
        {
            if (item.Labels.Length == 0)
            {
                GUILayout.Label("No labels", EditorStyles.miniLabel);
                return;
            }
            string remove = null;
            using (new EditorGUILayout.HorizontalScope())
            {
                foreach (var l in item.Labels)
                {
                    if (GUILayout.Button(new GUIContent(l + "  ×", "Remove label"), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                        remove = l;
                }
                GUILayout.FlexibleSpace();
            }
            if (remove != null) RemoveLabel(item, remove);
        }

        void DrawAddLabel(IEnumerable<InventoryItem> items)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                newLabel = EditorGUILayout.TextField(newLabel);
                if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(40))) AddLabel(items, newLabel);
                if (labelCounts.Count > 0 && GUILayout.Button("▾", EditorStyles.miniButton, GUILayout.Width(20)))
                {
                    var menu = new GenericMenu();
                    var list = items.ToList();
                    foreach (var l in labelCounts.Keys)
                        menu.AddItem(new GUIContent(l), false, () => AddLabel(list, l));
                    menu.ShowAsContext();
                }
            }
        }

        void AssetLink(string path)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var icon = AssetDatabase.GetCachedIcon(path);
                GUILayout.Label(icon, GUILayout.Width(16), GUILayout.Height(16));
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (GUILayout.Button(new GUIContent(Path.GetFileName(path), path), EditorStyles.linkLabel))
                {
                    if (InventoryIndex.Get(guid) != null)
                    {
                        SetSingle(guid);
                        int idx = filtered.FindIndex(i => i.Guid == guid);
                        if (idx >= 0) ScrollTo(idx);
                        OnSelectionChanged();
                    }
                    else EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path));
                }
            }
        }

        static void SectionLabel(string text) => GUILayout.Label(text, EditorStyles.boldLabel);

        static void SelectableRow(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                EditorGUILayout.SelectableLabel(value, EditorStyles.label, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        // ---- status bar -------------------------------------------------------------
        void DrawStatusBar(Rect rect)
        {
            GUILayout.BeginArea(rect);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                long total = 0;
                foreach (var i in filtered) total += i.Size;

                string fullStats = $"{filtered.Count:N0} of {InventoryIndex.Items.Count:N0} assets · {InventoryItem.FormatSize(total)}";
                string selStats = selection.Count > 0 ? $" · {selection.Count} selected" : "";
                string indexTime = "Indexed " + InventoryIndex.LastScan.ToString("HH:mm:ss");

                if (rect.width >= 460f)
                {
                    GUILayout.Label(fullStats, EditorStyles.miniLabel);
                    if (selection.Count > 0) GUILayout.Label(selStats, EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (InventoryIndex.HasReferenceData && InventoryIndex.ReferencesStale)
                    {
                        if (GUILayout.Button("References outdated – rescan", EditorStyles.toolbarButton)) ScanReferences();
                    }
                    GUILayout.Label(indexTime, EditorStyles.miniLabel);
                }
                else if (rect.width >= 320f)
                {
                    string compactStats = $"{filtered.Count:N0}/{InventoryIndex.Items.Count:N0} · {InventoryItem.FormatSize(total)}";
                    GUILayout.Label(new GUIContent(compactStats, fullStats + "\n" + indexTime), EditorStyles.miniLabel);
                    if (selection.Count > 0) GUILayout.Label($"· {selection.Count} sel", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (InventoryIndex.HasReferenceData && InventoryIndex.ReferencesStale)
                    {
                        if (GUILayout.Button(new GUIContent("Rescan", "References outdated – click to rescan"), EditorStyles.toolbarButton, GUILayout.Width(54)))
                            ScanReferences();
                    }
                    GUILayout.Label(InventoryIndex.LastScan.ToString("HH:mm:ss"), EditorStyles.miniLabel);
                }
                else
                {
                    string tinyStats = $"{filtered.Count:N0}/{InventoryIndex.Items.Count:N0}";
                    if (selection.Count > 0) tinyStats += $" ({selection.Count})";
                    GUILayout.Label(new GUIContent(tinyStats, fullStats + selStats + "\n" + indexTime), EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    if (InventoryIndex.HasReferenceData && InventoryIndex.ReferencesStale)
                    {
                        if (GUILayout.Button(new GUIContent("Rescan", "References outdated – click to rescan"), EditorStyles.toolbarButton, GUILayout.Width(54)))
                            ScanReferences();
                    }
                }
            }
            GUILayout.EndArea();
        }
    }
}
