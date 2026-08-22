#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace JustUnitypackageImporter2Folder
{
    [Serializable]
    internal sealed class DestinationPreset
    {
        public string DisplayName = "";
        public string DestinationPath = "Assets/JUIImport";
    }

    [Serializable]
    internal sealed class DestinationPresetCollection
    {
        public List<DestinationPreset> Items = new List<DestinationPreset>();
    }

    internal sealed class PackageMetrics
    {
        private const double BytesPerMegabyte = 1024d * 1024d;

        public readonly long CompressedBytes;
        public readonly long ExpandedBytes;

        public double ExpandedMegabytes => ExpandedBytes / BytesPerMegabyte;
        public double CompressionRatio => CompressedBytes > 0
            ? (double)ExpandedBytes / CompressedBytes
            : 0d;

        public PackageMetrics(long compressedBytes, long expandedBytes)
        {
            CompressedBytes = compressedBytes;
            ExpandedBytes = expandedBytes;
        }
    }

    internal sealed class UnityPackageReadCanceledException : OperationCanceledException
    {
        public UnityPackageReadCanceledException()
            : base("UnityPackageの読み込みがユーザーによってキャンセルされました。")
        {
        }
    }

    /// <summary>
    /// JustUnitypackageImporter2Folder (JUI)
    ///
    /// Prototype implementation for Unity 2022.3-era Editor APIs.
    /// Place this file under Assets/Editor/JUI/JUI.cs
    ///
    /// Design note:
    /// JUI does not rebuild a temporary .unitypackage.
    /// It reads the .unitypackage (tar.gz), writes selected asset/meta files
    /// directly into Assets/, then calls AssetDatabase.Refresh().
    /// This preserves package GUIDs while making destination remapping simple.
    /// </summary>
    public sealed class JUIWindow : EditorWindow
    {
        private const string PrefDefaultDestination = "JUI.DefaultDestination";
        private const string PrefWarnStandardImport = "JUI.WarnStandardImport";
        private const string PrefExpandedSizeWarningMb = "JUI.ExpandedSizeWarningMb";
        private const string PrefCompressionRatioWarning = "JUI.CompressionRatioWarning";
        private const string PrefDestinationPresets = "JUI.DestinationPresets";

        private string _packagePath = "";
        private string _destination = "Assets/JUIImport";
        private bool _remapDestination = true;
        private bool _groupTopLevelItems;
        private string _groupFolderName = "";
        private int _expandedSizeWarningMb = 512;
        private int _compressionRatioWarning = 10;
        private PackageMetrics _packageMetrics;
        private List<DestinationPreset> _destinationPresets = new List<DestinationPreset>();
        private int _selectedPresetIndex = -1;
        private bool _showPresetEditor;
        private Vector2 _presetScroll;
        private Vector2 _scroll;

        private List<PackageEntry> _entries = new List<PackageEntry>();
        private PackageTreeNode _treeRoot;
        private string _loadError = "";

        [MenuItem("Tools/JUI")]
        public static void Open()
        {
            var window = GetWindow<JUIWindow>("JUI");
            window.minSize = new Vector2(650, 420);
            window.Show();
        }

        private void OnEnable()
        {
            _destination = JUISettings.GetString(PrefDefaultDestination, "Assets/JUIImport");
            _expandedSizeWarningMb = JUISettings.GetInt(PrefExpandedSizeWarningMb, 512);
            _compressionRatioWarning = JUISettings.GetInt(PrefCompressionRatioWarning, 10);
            LoadDestinationPresets();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("JustUnitypackageImporter2Folder", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawPackagePicker();
            HandleDragAndDrop();
            DrawPackageMetrics();

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_entries.Count == 0))
            {
                _remapDestination = EditorGUILayout.ToggleLeft("インポート先を変更する", _remapDestination);

                using (new EditorGUI.DisabledScope(!_remapDestination))
                {
                    EditorGUILayout.BeginHorizontal();
                    _destination = EditorGUILayout.TextField("インポート先", _destination);
                    if (GUILayout.Button("選択", GUILayout.Width(64)))
                        SelectDestinationFolder();
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("現在の場所をDefaultに設定", GUILayout.Width(190)))
                    {
                        if (ValidateAssetFolder(_destination, out string normalized, out string error))
                        {
                            _destination = normalized;
                            JUISettings.SetString(PrefDefaultDestination, _destination);
                            JUILog.Info($"Defaultインポート先を保存しました: {_destination}");
                            ShowNotification(new GUIContent("Defaultを保存しました"));
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("JUI", error, "OK");
                        }
                    }

                    if (GUILayout.Button("Defaultを読込", GUILayout.Width(110)))
                    {
                        _destination = JUISettings.GetString(PrefDefaultDestination, "Assets/JUIImport");
                        JUILog.Info($"Defaultインポート先を読み込みました: {_destination}");
                    }
                    EditorGUILayout.EndHorizontal();
                }

                if (_groupTopLevelItems)
                {
                    _groupFolderName = EditorGUILayout.TextField(
                        "まとめ先フォルダ名",
                        _groupFolderName);

                    if ((_groupFolderName ?? "").Trim().Length >= 10)
                    {
                        EditorGUILayout.HelpBox(
                            "まとめ先フォルダ名が10文字以上です。フォルダ名が長くなっています。",
                            MessageType.Warning);
                    }
                }
            }

            DrawDestinationPresets();

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("バックアップフォルダを開く", GUILayout.Width(180)))
                OpenBackupFolder();
            if (GUILayout.Button("バックアップから復元する", GUILayout.Width(180)))
                RestoreFromBackup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("JUI Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            int expandedSizeWarningMb = EditorGUILayout.IntField(
                "展開後サイズ警告値 (MB)",
                _expandedSizeWarningMb);
            int compressionRatioWarning = EditorGUILayout.IntField(
                "展開倍率警告値 (倍)",
                _compressionRatioWarning);
            if (EditorGUI.EndChangeCheck())
            {
                _expandedSizeWarningMb = Math.Max(1, expandedSizeWarningMb);
                _compressionRatioWarning = Math.Max(1, compressionRatioWarning);
                JUISettings.SetInt(PrefExpandedSizeWarningMb, _expandedSizeWarningMb);
                JUISettings.SetInt(PrefCompressionRatioWarning, _compressionRatioWarning);
                JUILog.Info(
                    $"Package警告閾値を変更しました: " +
                    $"展開後サイズ {_expandedSizeWarningMb} MB, 展開倍率 {_compressionRatioWarning} 倍");
                LogPackageMetricWarnings();
            }

            bool warn = JUISettings.GetBool(PrefWarnStandardImport, true);
            bool newWarn = EditorGUILayout.ToggleLeft(
                "JUIを使用しないUnityPackage Import時に通知する",
                warn);
            if (newWarn != warn)
            {
                JUISettings.SetBool(PrefWarnStandardImport, newWarn);
                JUILog.Info($"通常UnityPackage Import時の通知を{(newWarn ? "有効" : "無効")}にしました。");
            }

            EditorGUILayout.Space(8);

            if (!string.IsNullOrEmpty(_loadError))
                EditorGUILayout.HelpBox(_loadError, MessageType.Error);

            DrawEntryList();

        }

        private void DrawPackagePicker()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("UnityPackage");

            string displayPath = string.IsNullOrEmpty(_packagePath) ? "以下へD&D、または参照..." : _packagePath;
            EditorGUILayout.SelectableLabel(displayPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (GUILayout.Button("参照", GUILayout.Width(64)))
            {
                string path = EditorUtility.OpenFilePanel("UnityPackageを選択", "", "unitypackage");
                if (!string.IsNullOrEmpty(path))
                    LoadPackage(path);
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_packagePath) && _entries.Count == 0))
            {
                if (GUILayout.Button("クリア", GUILayout.Width(64)))
                    ClearPackageInput();
            }

            bool canImport = CanImport();
            Color previousBackgroundColor = GUI.backgroundColor;
            if (canImport)
                GUI.backgroundColor = new Color(0.22f, 0.52f, 1f, 1f);

            using (new EditorGUI.DisabledScope(!canImport))
            {
                if (GUILayout.Button("これでインポートする", GUILayout.Width(150)))
                    ImportSelected();
            }

            GUI.backgroundColor = previousBackgroundColor;

            EditorGUILayout.EndHorizontal();
        }

        private bool CanImport()
        {
            if (string.IsNullOrEmpty(_packagePath) ||
                !File.Exists(_packagePath) ||
                !_entries.Any(x => x.Selected))
            {
                return false;
            }

            if (_remapDestination &&
                !ValidateAssetFolder(_destination, out _, out _))
            {
                return false;
            }

            if (_groupTopLevelItems &&
                !ValidateFolderName(_groupFolderName, out _, out _))
            {
                return false;
            }

            return true;
        }

        private void ClearPackageInput()
        {
            string clearedPackagePath = _packagePath;

            _packagePath = "";
            _entries.Clear();
            _treeRoot = null;
            _loadError = "";
            _groupTopLevelItems = false;
            _groupFolderName = "";
            _packageMetrics = null;
            _scroll = Vector2.zero;

            JUILog.Info(string.IsNullOrEmpty(clearedPackagePath)
                ? "UnityPackage入力をクリアしました。"
                : $"UnityPackage入力をクリアしました: {clearedPackagePath}");
            Repaint();
        }

        private void DrawPackageMetrics()
        {
            if (_packageMetrics == null)
                return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"展開後サイズ: {_packageMetrics.ExpandedMegabytes:F2} MB    " +
                $"圧縮倍率: {_packageMetrics.CompressionRatio:F2} 倍");

            var warningStyle = new GUIStyle(EditorStyles.label);
            warningStyle.normal.textColor = new Color(1f, 0.28f, 0.22f, 1f);
            warningStyle.fontStyle = FontStyle.Bold;
            warningStyle.wordWrap = true;

            if (_packageMetrics.ExpandedMegabytes >= _expandedSizeWarningMb)
            {
                EditorGUILayout.LabelField(
                    $"⚠ 展開後サイズが警告値 {_expandedSizeWarningMb} MB以上です。" +
                    $"（{_packageMetrics.ExpandedMegabytes:F2} MB）",
                    warningStyle);
            }

            if (_packageMetrics.CompressionRatio >= _compressionRatioWarning)
            {
                EditorGUILayout.LabelField(
                    $"⚠ 圧縮倍率が警告値 {_compressionRatioWarning} 倍以上です。" +
                    $"（{_packageMetrics.CompressionRatio:F2} 倍）",
                    warningStyle);
            }
        }

        private void DrawDestinationPresets()
        {
            EditorGUILayout.Space(4);

            string[] options = new string[_destinationPresets.Count + 1];
            options[0] = "プリセットを選択...";
            for (int i = 0; i < _destinationPresets.Count; i++)
            {
                string displayName = (_destinationPresets[i].DisplayName ?? "").Trim();
                options[i + 1] = string.IsNullOrEmpty(displayName)
                    ? $"(名称未設定 {i + 1})"
                    : displayName;
            }

            int popupValue = _selectedPresetIndex >= 0 &&
                             _selectedPresetIndex < _destinationPresets.Count
                ? _selectedPresetIndex + 1
                : 0;
            int newPopupValue = EditorGUILayout.Popup(
                "Import先プリセット",
                popupValue,
                options);

            if (newPopupValue != popupValue)
            {
                _selectedPresetIndex = newPopupValue - 1;
                if (_selectedPresetIndex >= 0)
                    ApplyDestinationPreset(_selectedPresetIndex);
            }

            _showPresetEditor = EditorGUILayout.Foldout(
                _showPresetEditor,
                "プリセットを管理",
                true);
            if (!_showPresetEditor)
                return;

            _presetScroll = EditorGUILayout.BeginScrollView(
                _presetScroll,
                GUI.skin.box,
                GUILayout.MaxHeight(220f));

            int deleteIndex = -1;
            int moveFrom = -1;
            int moveTo = -1;
            bool changed = false;

            for (int i = 0; i < _destinationPresets.Count; i++)
            {
                DestinationPreset preset = _destinationPresets[i];
                EditorGUILayout.BeginVertical(GUI.skin.box);

                EditorGUILayout.BeginHorizontal();
                string displayName = EditorGUILayout.TextField("表示名", preset.DisplayName ?? "");
                if (displayName != preset.DisplayName)
                {
                    preset.DisplayName = displayName;
                    changed = true;
                }

                using (new EditorGUI.DisabledScope(i == 0))
                {
                    if (GUILayout.Button("▲", GUILayout.Width(28)))
                    {
                        moveFrom = i;
                        moveTo = i - 1;
                    }
                }

                using (new EditorGUI.DisabledScope(i == _destinationPresets.Count - 1))
                {
                    if (GUILayout.Button("▼", GUILayout.Width(28)))
                    {
                        moveFrom = i;
                        moveTo = i + 1;
                    }
                }

                if (GUILayout.Button("削除", GUILayout.Width(52)))
                    deleteIndex = i;
                EditorGUILayout.EndHorizontal();

                string destinationPath = EditorGUILayout.TextField(
                    "インポート先パス",
                    preset.DestinationPath ?? "");
                if (destinationPath != preset.DestinationPath)
                {
                    preset.DestinationPath = destinationPath;
                    changed = true;
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndScrollView();

            if (moveFrom >= 0)
            {
                DestinationPreset moving = _destinationPresets[moveFrom];
                _destinationPresets.RemoveAt(moveFrom);
                _destinationPresets.Insert(moveTo, moving);

                if (_selectedPresetIndex == moveFrom)
                    _selectedPresetIndex = moveTo;
                else if (_selectedPresetIndex == moveTo)
                    _selectedPresetIndex = moveFrom;
                changed = true;
            }

            if (deleteIndex >= 0)
            {
                string deletedName = _destinationPresets[deleteIndex].DisplayName;
                _destinationPresets.RemoveAt(deleteIndex);
                if (_selectedPresetIndex == deleteIndex)
                    _selectedPresetIndex = -1;
                else if (_selectedPresetIndex > deleteIndex)
                    _selectedPresetIndex--;
                changed = true;
                JUILog.Info($"Import先プリセットを削除しました: {deletedName}");
            }

            if (GUILayout.Button("現在のImport先をプリセットに追加"))
            {
                if (ValidateAssetFolder(_destination, out string normalized, out string error))
                {
                    _destinationPresets.Add(new DestinationPreset
                    {
                        DisplayName = $"プリセット {_destinationPresets.Count + 1}",
                        DestinationPath = normalized
                    });
                    _selectedPresetIndex = _destinationPresets.Count - 1;
                    changed = true;
                    JUILog.Info($"Import先プリセットを追加しました: {normalized}");
                }
                else
                {
                    EditorUtility.DisplayDialog("JUI", error, "OK");
                }
            }

            if (changed)
                SaveDestinationPresets();
        }

        private void ApplyDestinationPreset(int index)
        {
            if (index < 0 || index >= _destinationPresets.Count)
                return;

            DestinationPreset preset = _destinationPresets[index];
            if (!ValidateAssetFolder(
                    preset.DestinationPath,
                    out string normalized,
                    out string error))
            {
                EditorUtility.DisplayDialog(
                    "JUI - プリセットエラー",
                    $"プリセット「{preset.DisplayName}」を適用できません。\n\n{error}",
                    "OK");
                JUILog.Warning($"無効なImport先プリセットです: {preset.DisplayName} / {preset.DestinationPath}");
                return;
            }

            preset.DestinationPath = normalized;
            _destination = normalized;
            _remapDestination = true;
            SaveDestinationPresets();
            JUILog.Info($"Import先プリセットを適用しました: {preset.DisplayName} / {_destination}");
            Repaint();
        }

        private void LoadDestinationPresets()
        {
            string json = JUISettings.GetString(PrefDestinationPresets, "");
            _destinationPresets = new List<DestinationPreset>();

            if (string.IsNullOrEmpty(json))
                return;

            try
            {
                DestinationPresetCollection collection =
                    JsonUtility.FromJson<DestinationPresetCollection>(json);
                if (collection != null && collection.Items != null)
                    _destinationPresets = collection.Items;
            }
            catch (Exception ex)
            {
                JUILog.Error("Import先プリセットの読み込みに失敗しました。", ex);
            }
        }

        private void SaveDestinationPresets()
        {
            var collection = new DestinationPresetCollection
            {
                Items = _destinationPresets
            };
            JUISettings.SetString(
                PrefDestinationPresets,
                JsonUtility.ToJson(collection));
        }

        private void LogPackageMetricWarnings()
        {
            if (_packageMetrics == null)
                return;

            if (_packageMetrics.ExpandedMegabytes >= _expandedSizeWarningMb)
            {
                JUILog.Warning(
                    $"[展開後サイズ警告] {_packageMetrics.ExpandedMegabytes:F2} MB " +
                    $"(警告値: {_expandedSizeWarningMb} MB)");
            }

            if (_packageMetrics.CompressionRatio >= _compressionRatioWarning)
            {
                JUILog.Warning(
                    $"[圧縮倍率警告] {_packageMetrics.CompressionRatio:F2} 倍 " +
                    $"(警告値: {_compressionRatioWarning} 倍)");
            }
        }

        private void HandleDragAndDrop()
        {
            Rect dropArea = GUILayoutUtility.GetRect(0, 46, GUILayout.ExpandWidth(true));
            Event e = Event.current;
            bool isPointerInside = dropArea.Contains(e.mousePosition);
            bool isDragEvent = e.type == EventType.DragUpdated || e.type == EventType.DragPerform;
            bool hasValidPackage = DragAndDrop.paths.Any(p =>
                string.Equals(Path.GetExtension(p), ".unitypackage", StringComparison.OrdinalIgnoreCase));
            bool isValidDragOver = isPointerInside && isDragEvent && hasValidPackage;

            Color borderColor = isValidDragOver
                ? new Color(0.48f, 0.72f, 1f, 1f)
                : new Color(0.16f, 0.16f, 0.16f, 1f);
            Color backgroundColor = isValidDragOver
                ? new Color(0.24f, 0.43f, 0.64f, 1f)
                : new Color(0.08f, 0.08f, 0.08f, 1f);

            EditorGUI.DrawRect(dropArea, borderColor);
            EditorGUI.DrawRect(
                new Rect(dropArea.x + 1f, dropArea.y + 1f, dropArea.width - 2f, dropArea.height - 2f),
                backgroundColor);

            var dropLabelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter
            };
            dropLabelStyle.normal.textColor = isValidDragOver
                ? Color.white
                : new Color(0.72f, 0.72f, 0.72f, 1f);
            GUI.Label(
                dropArea,
                isValidDragOver
                    ? "ここにドロップして読み込む"
                    : "UnityPackageをここへドラッグ＆ドロップ",
                dropLabelStyle);

            if (!isPointerInside)
                return;

            if (isDragEvent)
            {
                DragAndDrop.visualMode = hasValidPackage
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;

                if (e.type == EventType.DragPerform && hasValidPackage)
                {
                    DragAndDrop.AcceptDrag();
                    string path = DragAndDrop.paths.First(p =>
                        string.Equals(Path.GetExtension(p), ".unitypackage", StringComparison.OrdinalIgnoreCase));
                    LoadPackage(path);
                }

                Repaint();
                e.Use();
            }
        }

        private void DrawEntryList()
        {
            EditorGUILayout.LabelField($"インポート内容 ({_entries.Count})", EditorStyles.boldLabel);

            if (_entries.Count == 0)
            {
                EditorGUILayout.HelpBox("UnityPackageを読み込むと内容が表示されます。", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("すべてON", GUILayout.Width(90)))
                SetAll(true);
            if (GUILayout.Button("すべてOFF", GUILayout.Width(90)))
                SetAll(false);
            if (GUILayout.Button("すべて展開", GUILayout.Width(90)))
                SetExpanded(_treeRoot, true);
            if (GUILayout.Button("すべて折りたたむ", GUILayout.Width(120)))
                SetExpanded(_treeRoot, false);
            GUILayout.FlexibleSpace();
            int selected = _entries.Count(x => x.Selected);
            EditorGUILayout.LabelField($"{selected} / {_entries.Count} 選択", GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUI.skin.box);

            if (_treeRoot == null)
                _treeRoot = PackageTreeNode.Build(_entries);

            _treeRoot.UpdateSelectionCounts();
            DrawTreeNode(_treeRoot, 0, new List<bool>(), true);

            EditorGUILayout.EndScrollView();
        }

        private void DrawTreeNode(
            PackageTreeNode node,
            int depth,
            List<bool> ancestorHasNextSibling,
            bool isLastSibling)
        {
            bool isFolder = node.IsFolder;
            EditorGUILayout.BeginHorizontal();
            Rect indentRect = GUILayoutUtility.GetRect(
                depth * 16f,
                EditorGUIUtility.singleLineHeight,
                GUILayout.Width(depth * 16f));

            bool hasGuidConflict = !isFolder && node.Entry != null && node.Entry.HasGuidConflict;
            if (hasGuidConflict && Event.current.type == EventType.Repaint)
            {
                Color highlightColor = EditorGUIUtility.isProSkin
                    ? new Color(1f, 0.78f, 0.18f, 0.18f)
                    : new Color(1f, 0.82f, 0.24f, 0.32f);
                float highlightWidth = Math.Max(
                    0f,
                    EditorGUIUtility.currentViewWidth - indentRect.x - 24f);
                EditorGUI.DrawRect(
                    new Rect(indentRect.x, indentRect.y, highlightWidth, indentRect.height),
                    highlightColor);
            }

            if (isFolder)
            {
                bool anySelected = node.SelectedEntryCount > 0;
                bool allSelected = node.TotalEntryCount > 0 &&
                    node.SelectedEntryCount == node.TotalEntryCount;

                EditorGUI.showMixedValue = anySelected && !allSelected;
                EditorGUI.BeginChangeCheck();
                bool selected = EditorGUILayout.Toggle(allSelected, GUILayout.Width(18));
                if (EditorGUI.EndChangeCheck())
                    SetNodeSelected(node, selected);
                EditorGUI.showMixedValue = false;

                node.Expanded = EditorGUILayout.Foldout(
                    node.Expanded,
                    node.Name,
                    true,
                    EditorStyles.foldoutHeader);
            }
            else
            {
                PackageEntry entry = node.Entry;
                bool selected = EditorGUILayout.Toggle(entry.Selected, GUILayout.Width(18));
                if (selected != entry.Selected)
                    entry.Selected = selected;
                var label = new GUIContent(
                    hasGuidConflict ? "⚠ " + node.Name : node.Name,
                    hasGuidConflict
                        ? "GUID競合: " + entry.GuidConflictPath
                        : "");
                EditorGUILayout.LabelField(label, EditorStyles.label);
            }

            EditorGUILayout.EndHorizontal();
            DrawTreeLines(indentRect, depth, ancestorHasNextSibling, isLastSibling);

            if (!isFolder || !node.Expanded)
                return;

            List<PackageTreeNode> children = node.Children
                .OrderByDescending(x => x.IsFolder)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var childAncestors = new List<bool>(ancestorHasNextSibling);
            if (depth > 0)
                childAncestors.Add(!isLastSibling);

            for (int index = 0; index < children.Count; index++)
            {
                DrawTreeNode(
                    children[index],
                    depth + 1,
                    childAncestors,
                    index == children.Count - 1);
            }
        }

        private static void DrawTreeLines(
            Rect indentRect,
            int depth,
            List<bool> ancestorHasNextSibling,
            bool isLastSibling)
        {
            if (Event.current.type != EventType.Repaint || depth == 0)
                return;

            const float indentWidth = 16f;
            const float lineWidth = 1f;
            Color lineColor = EditorGUIUtility.isProSkin
                ? new Color(0.48f, 0.48f, 0.48f, 0.8f)
                : new Color(0.42f, 0.42f, 0.42f, 0.8f);
            float connectedHeight = indentRect.height + EditorGUIUtility.standardVerticalSpacing;

            for (int level = 0; level < ancestorHasNextSibling.Count; level++)
            {
                if (!ancestorHasNextSibling[level])
                    continue;

                float ancestorX = indentRect.x + (level * indentWidth) + (indentWidth * 0.5f);
                EditorGUI.DrawRect(
                    new Rect(ancestorX, indentRect.y, lineWidth, connectedHeight),
                    lineColor);
            }

            float branchX = indentRect.x + ((depth - 1) * indentWidth) + (indentWidth * 0.5f);
            float middleY = indentRect.y + (indentRect.height * 0.5f);
            float verticalHeight = isLastSibling
                ? indentRect.height * 0.5f
                : connectedHeight;

            EditorGUI.DrawRect(
                new Rect(branchX, indentRect.y, lineWidth, verticalHeight),
                lineColor);
            EditorGUI.DrawRect(
                new Rect(branchX, middleY, indentWidth * 0.5f, lineWidth),
                lineColor);
        }

        private static void SetNodeSelected(PackageTreeNode node, bool value)
        {
            foreach (PackageEntry entry in node.GetEntriesRecursive())
                entry.Selected = value;
        }

        private static void SetExpanded(PackageTreeNode node, bool value)
        {
            if (node == null)
                return;

            node.Expanded = value;
            foreach (PackageTreeNode child in node.Children)
                SetExpanded(child, value);
        }

        private void LoadPackage(string path)
        {
            _packagePath = path;
            _loadError = "";
            _entries.Clear();
            _treeRoot = null;
            _groupTopLevelItems = false;
            _groupFolderName = Path.GetFileNameWithoutExtension(path);
            _packageMetrics = null;

            try
            {
                long warningBytes = checked((long)_expandedSizeWarningMb * 1024L * 1024L);
                _entries = UnityPackageReader.Read(
                        path,
                        warningBytes,
                        estimatedBytes => EditorUtility.DisplayDialog(
                            "JUI - 展開後サイズ警告",
                            $"展開後サイズが警告値 {_expandedSizeWarningMb} MB を超えています。\n" +
                            $"現在の推定展開後サイズ: {estimatedBytes / (1024d * 1024d):F1} MB\n\n" +
                            "このままUnityPackageを読み込みますか？",
                            "読み込みを続行",
                            "キャンセル"),
                        out PackageMetrics metrics)
                    .OrderBy(x => x.Pathname, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _packageMetrics = metrics;
                int guidConflictCount = JUIConflictChecker.MarkGuidConflicts(_entries);
                _treeRoot = PackageTreeNode.Build(_entries);

                if (_entries.Count == 0)
                    _loadError = "UnityPackage内にインポート可能なAssetが見つかりませんでした.";
                else
                {
                    JUILog.Info($"UnityPackageを読み込みました: {path} ({_entries.Count} 項目)");
                    JUILog.Info(
                        $"Packageサイズ: 展開後 {_packageMetrics.ExpandedMegabytes:F2} MB, " +
                        $"圧縮倍率 {_packageMetrics.CompressionRatio:F2} 倍");
                    LogPackageMetricWarnings();

                    if (guidConflictCount > 0)
                    {
                        JUILog.Warning(
                            $"GUID競合のため {guidConflictCount} ファイルをImport対象から除外しました。");
                    }

                    int topLevelCount = _treeRoot.Children.Count;
                    if (topLevelCount >= 2)
                    {
                        JUILog.Warning(
                            $"UnityPackage内に複数のトップレベル項目があります: {topLevelCount} 件");
                        _groupTopLevelItems = EditorUtility.DisplayDialog(
                            "JUI - 複数項目の確認",
                            "UnityPackage内に複数のフォルダ・ファイルがあります。\n" +
                            "一つのフォルダにまとめますか？",
                            "まとめる",
                            "そのまま");

                        JUILog.Info(_groupTopLevelItems
                            ? $"トップレベル項目をフォルダへまとめます: {_groupFolderName}"
                            : "トップレベル項目を元の構成のままImportします。");
                    }
                }
            }
            catch (UnityPackageReadCanceledException)
            {
                _packagePath = "";
                _entries.Clear();
                _treeRoot = null;
                _groupTopLevelItems = false;
                _groupFolderName = "";
                _packageMetrics = null;
                _loadError = "";
                _scroll = Vector2.zero;
                JUILog.Info("展開後サイズ警告でUnityPackageの読み込みをキャンセルしました。");
            }
            catch (Exception ex)
            {
                _entries.Clear();
                _treeRoot = null;
                _groupTopLevelItems = false;
                _packageMetrics = null;
                _loadError = "UnityPackageの解析に失敗しました。\n" + ex.Message;
                JUILog.Error("UnityPackageの解析に失敗しました。", ex);
                Debug.LogException(ex);
            }

            Repaint();
        }

        private void OpenBackupFolder()
        {
            string backupRoot = JUIBackupManager.BackupRoot;
            Directory.CreateDirectory(backupRoot);
            EditorUtility.RevealInFinder(backupRoot);
            JUILog.Info($"バックアップフォルダを開きました: {backupRoot}");
        }

        private void RestoreFromBackup()
        {
            string backupRoot = JUIBackupManager.BackupRoot;
            if (!Directory.Exists(backupRoot))
            {
                EditorUtility.DisplayDialog("JUI", "バックアップフォルダがありません。", "OK");
                JUILog.Warning($"復元を開始できませんでした。バックアップフォルダがありません: {backupRoot}");
                return;
            }

            string selected = EditorUtility.OpenFolderPanel(
                "復元するバックアップを選択",
                backupRoot,
                "");
            if (string.IsNullOrEmpty(selected))
                return;

            selected = Path.GetFullPath(selected);
            if (!JUIWindow.IsPathInside(selected, backupRoot) ||
                !Directory.Exists(Path.Combine(selected, "Assets")))
            {
                EditorUtility.DisplayDialog(
                    "JUI",
                    "JUI_BAK内の有効なバックアップフォルダを選択してください。",
                    "OK");
                JUILog.Warning($"無効なバックアップフォルダが選択されました: {selected}");
                return;
            }

            int fileCount = Directory.GetFiles(selected, "*", SearchOption.AllDirectories).Length;
            bool confirmed = EditorUtility.DisplayDialog(
                "JUI - バックアップから復元",
                $"{fileCount} ファイルをバックアップから復元します。\n\n本当に復元しますか？",
                "復元する",
                "キャンセル");
            if (!confirmed)
                return;

            try
            {
                int restored = JUIBackupManager.Restore(selected);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                JUILog.Info($"バックアップから復元しました: {selected} ({restored} ファイル)");
                EditorUtility.DisplayDialog("JUI", $"復元が完了しました。\n{restored} ファイル", "OK");
            }
            catch (Exception ex)
            {
                JUILog.Error("バックアップからの復元に失敗しました。", ex);
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("JUI", "復元中にエラーが発生しました。\n\n" + ex.Message, "OK");
            }
        }

        private void SetAll(bool value)
        {
            foreach (PackageEntry entry in _entries)
                entry.Selected = value;
        }

        private void SelectDestinationFolder()
        {
            string projectRoot = ProjectRoot;
            string absoluteCurrent = Path.GetFullPath(Path.Combine(projectRoot, _destination));

            string selected = EditorUtility.OpenFolderPanel(
                "インポート先フォルダを選択",
                Directory.Exists(absoluteCurrent) ? absoluteCurrent : Path.Combine(projectRoot, "Assets"),
                "");

            if (string.IsNullOrEmpty(selected))
                return;

            selected = Path.GetFullPath(selected);

            string assetsRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets"));
            if (!IsPathInside(selected, assetsRoot))
            {
                EditorUtility.DisplayDialog("JUI", "インポート先はこのUnityプロジェクトのAssetsフォルダ内を指定してください。", "OK");
                return;
            }

            string rel = "Assets" + selected.Substring(assetsRoot.Length).Replace('\\', '/');
            _destination = rel.TrimEnd('/');
        }

        private void ImportSelected()
        {
            if (string.IsNullOrEmpty(_packagePath) || !File.Exists(_packagePath))
            {
                EditorUtility.DisplayDialog("JUI", "UnityPackageが指定されていません。", "OK");
                return;
            }

            string destination = _destination;
            if (_remapDestination)
            {
                if (!ValidateAssetFolder(destination, out destination, out string destinationError))
                {
                    EditorUtility.DisplayDialog("JUI", destinationError, "OK");
                    return;
                }
            }

            string groupFolderName = _groupFolderName;
            if (_groupTopLevelItems &&
                !ValidateFolderName(groupFolderName, out groupFolderName, out string groupNameError))
            {
                EditorUtility.DisplayDialog("JUI", groupNameError, "OK");
                return;
            }

            List<PackageEntry> selected = _entries.Where(x => x.Selected).ToList();
            if (selected.Count == 0)
            {
                EditorUtility.DisplayDialog("JUI", "インポート対象が選択されていません。", "OK");
                return;
            }

            // Keep metadata for ancestor folders when package contains it.
            AddRequiredParentFolderEntries(selected);

            List<ImportPlan> plans;
            try
            {
                plans = BuildImportPlans(
                    selected,
                    _remapDestination,
                    destination,
                    _groupTopLevelItems,
                    groupFolderName);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("JUI", "Import計画の作成に失敗しました。\n" + ex.Message, "OK");
                return;
            }

            PathValidationResult pathValidation = JUIPathValidator.Check(plans);
            if (pathValidation.Errors.Count > 0)
            {
                foreach (string error in pathValidation.Errors)
                    JUILog.Error(error);

                EditorUtility.DisplayDialog(
                    "JUI - パスエラー",
                    $"Importできないパスを {pathValidation.Errors.Count} 件検出しました。\n" +
                    "詳細はConsoleを確認してください。",
                    "OK");
                return;
            }

            List<string> warnings = JUIConflictChecker.Check(plans);
            warnings.AddRange(pathValidation.Warnings);
            warnings = warnings.Distinct().ToList();
            if (warnings.Count > 0)
            {
                foreach (string warning in warnings)
                    JUILog.Warning(warning);

                bool proceed = EditorUtility.DisplayDialog(
                    "JUI - 競合警告",
                    $"インポート先に {warnings.Count} 件の競合または注意事項があります。\n" +
                    "詳細はConsoleを確認してください。" +
                    "\n\n処理を続行しますか？",
                    "続行",
                    "キャンセル");

                if (!proceed)
                    return;
            }

            List<string> overwritePaths = JUIImporter.GetOverwritePaths(plans);
            bool createBackup = false;
            if (overwritePaths.Count > 0)
            {
                foreach (string overwritePath in overwritePaths)
                    JUILog.Warning($"[上書き対象] {overwritePath}");

                int overwriteChoice = EditorUtility.DisplayDialogComplex(
                    "JUI - 上書き確認",
                    $"{overwritePaths.Count} 件の既存ファイルを上書きします。\n" +
                    "詳細はConsoleを確認してください。",
                    "バックアップして上書き",
                    "キャンセル",
                    "バックアップしないでインポートする");

                if (overwriteChoice == 1)
                    return;

                createBackup = overwriteChoice == 0;
                JUILog.Info(createBackup
                    ? "バックアップして上書きする操作が選択されました。"
                    : "バックアップせずに上書きする操作が選択されました。");
            }

            bool finalConfirm = EditorUtility.DisplayDialog(
                "JUI",
                $"{plans.Count} 項目をインポートします。\n\n" +
                (_remapDestination
                    ? $"保存先: {destination}"
                    : "保存先: UnityPackage内の設定に従います") +
                (_groupTopLevelItems
                    ? $"\nまとめ先フォルダ: {groupFolderName}"
                    : "") +
                "\n\n実行しますか？",
                "インポート",
                "キャンセル");

            if (!finalConfirm)
                return;

            try
            {
                JUILog.Info($"インポートを開始します: {_packagePath} ({plans.Count} 項目)");
                string backupDirectory = JUIImporter.Execute(plans, createBackup);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                JUILog.Info($"インポートが完了しました: {_packagePath} ({plans.Count} 項目)");

                _packagePath = "";
                _entries.Clear();
                _treeRoot = null;
                _groupTopLevelItems = false;
                _groupFolderName = "";
                _packageMetrics = null;
                _loadError = "";
                _scroll = Vector2.zero;

                EditorUtility.DisplayDialog(
                    "JUI",
                    $"インポートが完了しました。\n{plans.Count} 項目" +
                    (string.IsNullOrEmpty(backupDirectory)
                        ? ""
                        : $"\n\n上書き前のファイルを保存しました。\n{backupDirectory}"),
                    "OK");
            }
            catch (Exception ex)
            {
                JUILog.Error("インポートに失敗しました。", ex);
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(
                    "JUI",
                    "インポート中にエラーが発生しました。\n\n" + ex.Message,
                    "OK");
            }
        }

        private void AddRequiredParentFolderEntries(List<PackageEntry> selected)
        {
            var selectedSet = new HashSet<PackageEntry>(selected);
            var byPath = _entries.ToDictionary(x => NormalizeAssetPath(x.Pathname), x => x,
                StringComparer.OrdinalIgnoreCase);

            foreach (PackageEntry entry in selected.ToList())
            {
                string path = NormalizeAssetPath(entry.Pathname);
                string parent = GetParentAssetPath(path);

                while (!string.IsNullOrEmpty(parent) && parent != "Assets")
                {
                    if (byPath.TryGetValue(parent, out PackageEntry folderEntry) &&
                        folderEntry.IsDirectory &&
                        selectedSet.Add(folderEntry))
                    {
                        selected.Add(folderEntry);
                    }

                    parent = GetParentAssetPath(parent);
                }
            }
        }

        private static List<ImportPlan> BuildImportPlans(
            List<PackageEntry> selected,
            bool remap,
            string destination,
            bool groupTopLevelItems,
            string groupFolderName)
        {
            var plans = new List<ImportPlan>(selected.Count);

            foreach (PackageEntry entry in selected)
            {
                string original = NormalizeAssetPath(entry.Pathname);

                if (!original.StartsWith("Assets", StringComparison.Ordinal))
                    throw new InvalidDataException($"Assets配下ではないpathnameには対応していません: {original}");

                string relative = original.Length == "Assets".Length
                    ? ""
                    : original.Substring("Assets".Length).TrimStart('/');

                string target;
                if (groupTopLevelItems)
                {
                    string basePath = remap ? destination : "Assets";
                    string groupRoot = basePath.TrimEnd('/') + "/" + groupFolderName;

                    target = string.IsNullOrEmpty(relative)
                        ? groupRoot
                        : groupRoot + "/" + relative;
                }
                else if (remap)
                {
                    target = string.IsNullOrEmpty(relative)
                        ? destination
                        : destination.TrimEnd('/') + "/" + relative;
                }
                else
                {
                    target = original;
                }

                string normalizedTarget = NormalizeAssetPath(target);
                ResolveAssetPathOrThrow(normalizedTarget);
                plans.Add(new ImportPlan(entry, normalizedTarget));
            }

            return plans
                .OrderBy(x => x.Entry.IsDirectory ? 0 : 1)
                .ThenBy(x => x.TargetPath.Count(c => c == '/'))
                .ToList();
        }

        private static bool ValidateFolderName(
            string input,
            out string normalized,
            out string error)
        {
            normalized = (input ?? "").Trim();
            error = "";

            if (string.IsNullOrEmpty(normalized))
            {
                error = "まとめ先フォルダ名を入力してください。";
                return false;
            }

            if (normalized == "." || normalized == ".." ||
                normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                normalized.Contains("/") || normalized.Contains("\\"))
            {
                error = "まとめ先フォルダ名に使用できない文字が含まれています。";
                return false;
            }

            return true;
        }

        private static bool ValidateAssetFolder(string input, out string normalized, out string error)
        {
            normalized = NormalizeAssetPath(input).TrimEnd('/');
            error = "";

            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "インポート先を指定してください。";
                return false;
            }

            if (normalized == "Assets")
                return true;

            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                error = "インポート先はAssetsまたはAssets配下を指定してください。";
                return false;
            }

            if (normalized.Contains(".."))
            {
                error = "インポート先に '..' は使用できません。";
                return false;
            }

            return true;
        }

        private static string GetParentAssetPath(string path)
        {
            int index = path.LastIndexOf('/');
            return index <= 0 ? "" : path.Substring(0, index);
        }

        internal static string NormalizeAssetPath(string path)
        {
            if (path == null)
                return "";

            return path
                .Replace('\\', '/')
                .Trim()
                .TrimEnd('\0')
                .TrimEnd('/');
        }

        internal static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        internal static bool IsPathInside(string candidate, string root)
        {
            candidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ResolveAssetPathOrThrow(string assetPath)
        {
            string projectRoot = ProjectRoot;
            string assetsRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets"));
            string absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));

            if (!IsPathInside(absolute, assetsRoot))
                throw new InvalidOperationException($"Assets外のパスを拒否しました: {assetPath}");

            return absolute;
        }
    }

    internal sealed class PackageTreeNode
    {
        public readonly string Name;
        public readonly string FullPath;
        public readonly List<PackageTreeNode> Children = new List<PackageTreeNode>();
        public PackageEntry Entry;
        public bool Expanded = true;
        public int TotalEntryCount { get; private set; }
        public int SelectedEntryCount { get; private set; }

        public bool IsFolder => Children.Count > 0 || (Entry != null && Entry.IsDirectory);

        private PackageTreeNode(string name, string fullPath)
        {
            Name = name;
            FullPath = fullPath;
        }

        public static PackageTreeNode Build(IEnumerable<PackageEntry> entries)
        {
            var root = new PackageTreeNode("Assets", "Assets");

            foreach (PackageEntry entry in entries)
            {
                string pathname = JUIWindow.NormalizeAssetPath(entry.Pathname);
                if (pathname == "Assets")
                {
                    root.Entry = entry;
                    continue;
                }

                if (!pathname.StartsWith("Assets/", StringComparison.Ordinal))
                    continue;

                string relative = pathname.Substring("Assets/".Length);
                string[] parts = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                PackageTreeNode current = root;
                string currentPath = "Assets";

                foreach (string part in parts)
                {
                    currentPath += "/" + part;
                    PackageTreeNode child = current.Children.FirstOrDefault(x =>
                        string.Equals(x.Name, part, StringComparison.OrdinalIgnoreCase));

                    if (child == null)
                    {
                        child = new PackageTreeNode(part, currentPath);
                        current.Children.Add(child);
                    }

                    current = child;
                }

                current.Entry = entry;
            }

            return root;
        }

        public IEnumerable<PackageEntry> GetEntriesRecursive()
        {
            if (Entry != null)
                yield return Entry;

            foreach (PackageTreeNode child in Children)
            {
                foreach (PackageEntry entry in child.GetEntriesRecursive())
                    yield return entry;
            }
        }

        public void UpdateSelectionCounts()
        {
            TotalEntryCount = Entry == null ? 0 : 1;
            SelectedEntryCount = Entry != null && Entry.Selected ? 1 : 0;

            foreach (PackageTreeNode child in Children)
            {
                child.UpdateSelectionCounts();
                TotalEntryCount += child.TotalEntryCount;
                SelectedEntryCount += child.SelectedEntryCount;
            }
        }
    }

    internal sealed class PackageEntry
    {
        public string Guid;
        public string Pathname;
        public byte[] AssetBytes;
        public byte[] MetaBytes;
        public bool IsDirectory;
        public bool Selected = true;
        public bool HasGuidConflict;
        public string GuidConflictPath;
    }

    internal sealed class ImportPlan
    {
        public readonly PackageEntry Entry;
        public readonly string TargetPath;

        public ImportPlan(PackageEntry entry, string targetPath)
        {
            Entry = entry;
            TargetPath = targetPath;
        }
    }

    /// <summary>
    /// Minimal reader for Unity's .unitypackage tar.gz structure.
    /// Each package GUID directory usually contains:
    /// asset, asset.meta, pathname
    /// </summary>
    internal static class UnityPackageReader
    {
        private sealed class RawGroup
        {
            public string Guid;
            public byte[] Asset;
            public byte[] Meta;
            public byte[] Pathname;
        }

        public static List<PackageEntry> Read(
            string unityPackagePath,
            long expandedSizeWarningBytes,
            Func<long, bool> confirmContinueAfterSizeWarning,
            out PackageMetrics metrics)
        {
            if (!File.Exists(unityPackagePath))
                throw new FileNotFoundException("UnityPackageが見つかりません。", unityPackagePath);

            var groups = new Dictionary<string, RawGroup>(StringComparer.OrdinalIgnoreCase);
            long compressedBytes = new FileInfo(unityPackagePath).Length;
            long expandedBytes = 0;
            bool sizeWarningHandled = false;

            using (FileStream fs = File.OpenRead(unityPackagePath))
            using (var gzip = new GZipStream(fs, CompressionMode.Decompress))
            {
                TarReader.Read(
                    gzip,
                    (name, bytes, typeFlag) =>
                    {
                        string normalized = name.Replace('\\', '/');
                        while (normalized.StartsWith("./", StringComparison.Ordinal))
                            normalized = normalized.Substring(2);
                        normalized = normalized.TrimStart('/');
                        string[] parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                            return;

                        string guid = parts[0];
                        string leaf = parts[parts.Length - 1];

                        if (!groups.TryGetValue(guid, out RawGroup group))
                        {
                            group = new RawGroup { Guid = guid };
                            groups.Add(guid, group);
                        }

                        switch (leaf)
                        {
                            case "asset":
                                group.Asset = bytes;
                                break;
                            case "asset.meta":
                                group.Meta = bytes;
                                break;
                            case "pathname":
                                group.Pathname = bytes;
                                break;
                        }
                    },
                    (name, size, typeFlag) =>
                    {
                        if (!IsRegularTarFile(typeFlag))
                            return true;

                        checked { expandedBytes += size; }

                        if (!sizeWarningHandled && expandedBytes > expandedSizeWarningBytes)
                        {
                            sizeWarningHandled = true;
                            bool continueReading = confirmContinueAfterSizeWarning == null ||
                                confirmContinueAfterSizeWarning(expandedBytes);
                            if (!continueReading)
                                throw new UnityPackageReadCanceledException();
                        }

                        return true;
                    });
            }

            metrics = new PackageMetrics(compressedBytes, expandedBytes);

            var result = new List<PackageEntry>();

            foreach (RawGroup group in groups.Values)
            {
                if (group.Pathname == null)
                    continue;

                string pathname = DecodeText(group.Pathname)
                    .Replace('\\', '/')
                    .Trim()
                    .TrimEnd('\0')
                    .TrimEnd('/');

                if (string.IsNullOrEmpty(pathname))
                    continue;

                // JUI intentionally supports Asset payloads only.
                if (!(pathname == "Assets" || pathname.StartsWith("Assets/", StringComparison.Ordinal)))
                    continue;

                try
                {
                    JUIWindow.ResolveAssetPathOrThrow(pathname);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        $"Assets外を指す不正なpathnameを検出しました: {pathname}",
                        ex);
                }

                bool isDirectory = group.Asset == null;

                result.Add(new PackageEntry
                {
                    Guid = group.Guid,
                    Pathname = pathname,
                    AssetBytes = group.Asset,
                    MetaBytes = group.Meta,
                    IsDirectory = isDirectory,
                    Selected = true
                });
            }

            return result;
        }

        private static bool IsRegularTarFile(byte typeFlag)
        {
            return typeFlag == 0 || typeFlag == (byte)'0' || typeFlag == (byte)'7';
        }

        private static string DecodeText(byte[] bytes)
        {
            // pathname is normally UTF-8. Strip UTF-8 BOM if present.
            string text = Encoding.UTF8.GetString(bytes);
            return text.TrimStart('\uFEFF');
        }
    }

    internal sealed class PathValidationResult
    {
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> Errors = new List<string>();
    }

    internal static class JUIPathValidator
    {
        private const int WindowsLegacyPathLimit = 260;
        private const int WindowsExtendedPathLimit = 32767;
        private const int UnixPathByteLimit = 4096;
        private const int FileNameByteLimit = 255;
        private const int UnityCompatibilityWarningLength = 1024;

        public static PathValidationResult Check(IEnumerable<ImportPlan> plans)
        {
            var result = new PathValidationResult();
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;

            foreach (ImportPlan plan in plans)
            {
                string assetPath = JUIWindow.NormalizeAssetPath(plan.TargetPath);
                string absolute;

                try
                {
                    absolute = JUIWindow.ResolveAssetPathOrThrow(assetPath);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"[パスエラー] {assetPath}\n  {ex.Message}");
                    continue;
                }

                string[] segments = assetPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string segment in segments.Skip(1))
                {
                    if (segment == "." || segment == ".." ||
                        segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        result.Errors.Add($"[使用不可文字] {assetPath}\n  対象: {segment}");
                        break;
                    }

                    int segmentBytes = Encoding.UTF8.GetByteCount(segment);
                    if (segmentBytes > FileNameByteLimit || (isWindows && segment.Length > 255))
                    {
                        result.Errors.Add(
                            $"[ファイル名が長すぎます] {assetPath}\n" +
                            $"  対象: {segment} ({segmentBytes} UTF-8 bytes)");
                        break;
                    }

                    if (isWindows && (segment.EndsWith(" ", StringComparison.Ordinal) ||
                                      segment.EndsWith(".", StringComparison.Ordinal)))
                    {
                        result.Errors.Add($"[Windowsで使用不可の末尾文字] {assetPath}\n  対象: {segment}");
                        break;
                    }
                }

                if (isWindows)
                {
                    if (absolute.Length >= WindowsExtendedPathLimit)
                    {
                        result.Errors.Add(
                            $"[OSパス上限超過] {assetPath}\n" +
                            $"  絶対パス長: {absolute.Length}");
                    }
                    else if (absolute.Length >= WindowsLegacyPathLimit)
                    {
                        result.Warnings.Add(
                            $"[長いパス] {assetPath}\n" +
                            $"  絶対パス長: {absolute.Length}。UnityまたはWindows設定によってはImportできません。");
                    }
                }
                else
                {
                    int absoluteBytes = Encoding.UTF8.GetByteCount(absolute);
                    if (absoluteBytes >= UnixPathByteLimit)
                    {
                        result.Errors.Add(
                            $"[OSパス上限超過] {assetPath}\n" +
                            $"  絶対パス長: {absoluteBytes} UTF-8 bytes");
                    }
                }

                if (assetPath.Length >= UnityCompatibilityWarningLength)
                {
                    result.Warnings.Add(
                        $"[Unity互換性の注意] Assetパスが非常に長くなっています: {assetPath.Length} 文字\n" +
                        $"  {assetPath}");
                }
            }

            result.Warnings.Sort(StringComparer.OrdinalIgnoreCase);
            result.Errors.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }

    internal static class JUIConflictChecker
    {
        public static int MarkGuidConflicts(IEnumerable<PackageEntry> entries)
        {
            int conflictCount = 0;

            foreach (PackageEntry entry in entries)
            {
                entry.HasGuidConflict = false;
                entry.GuidConflictPath = "";

                if (entry.IsDirectory || entry.MetaBytes == null)
                    continue;

                string packageGuid = ExtractGuidFromMeta(entry.MetaBytes);
                if (string.IsNullOrEmpty(packageGuid))
                    continue;

                string existingPath = AssetDatabase.GUIDToAssetPath(packageGuid);
                if (string.IsNullOrEmpty(existingPath))
                    continue;

                entry.HasGuidConflict = true;
                entry.GuidConflictPath = JUIWindow.NormalizeAssetPath(existingPath);
                entry.Selected = false;
                conflictCount++;

                JUILog.Warning(
                    $"[GUID競合・自動除外] {entry.Pathname}\n" +
                    $"  GUID: {packageGuid}\n" +
                    $"  既存: {entry.GuidConflictPath}");
            }

            return conflictCount;
        }

        public static List<string> Check(List<ImportPlan> plans)
        {
            var warnings = new List<string>();

            foreach (ImportPlan plan in plans)
            {
                string absolute = JUIWindow.ResolveAssetPathOrThrow(plan.TargetPath);

                if (plan.Entry.IsDirectory)
                {
                    if (File.Exists(absolute))
                        warnings.Add($"[種別競合] フォルダ予定位置にファイルがあります: {plan.TargetPath}");
                }
                else
                {
                    if (File.Exists(absolute))
                        warnings.Add($"[同名ファイル] {plan.TargetPath}");

                    if (Directory.Exists(absolute))
                        warnings.Add($"[種別競合] ファイル予定位置にフォルダがあります: {plan.TargetPath}");
                }

                if (plan.Entry.MetaBytes != null)
                {
                    string packageGuid = ExtractGuidFromMeta(plan.Entry.MetaBytes);
                    if (!string.IsNullOrEmpty(packageGuid))
                    {
                        string existingPath = AssetDatabase.GUIDToAssetPath(packageGuid);
                        if (!string.IsNullOrEmpty(existingPath) &&
                            !string.Equals(
                                JUIWindow.NormalizeAssetPath(existingPath),
                                JUIWindow.NormalizeAssetPath(plan.TargetPath),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add(
                                $"[GUID競合] {packageGuid}\n" +
                                $"  既存: {existingPath}\n" +
                                $"  予定: {plan.TargetPath}");
                        }
                    }
                }
            }

            return warnings.Distinct().ToList();
        }

        internal static string ExtractGuidFromMeta(byte[] metaBytes)
        {
            string text = Encoding.UTF8.GetString(metaBytes);
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("guid:", StringComparison.Ordinal))
                        return trimmed.Substring("guid:".Length).Trim();
                }
            }

            return "";
        }
    }

    internal static class JUIImporter
    {
        public static List<string> GetOverwritePaths(List<ImportPlan> plans)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ImportPlan plan in plans)
            {
                string absolute = JUIWindow.ResolveAssetPathOrThrow(plan.TargetPath);

                if (!plan.Entry.IsDirectory && File.Exists(absolute))
                    paths.Add(JUIWindow.NormalizeAssetPath(plan.TargetPath));

                string metaPath = absolute + ".meta";
                if (plan.Entry.MetaBytes != null && File.Exists(metaPath))
                    paths.Add(JUIWindow.NormalizeAssetPath(plan.TargetPath) + ".meta");
            }

            return paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string Execute(List<ImportPlan> plans, bool createBackup)
        {
            List<string> overwritePaths = GetOverwritePaths(plans);
            string backupDirectory = createBackup
                ? JUIBackupManager.Create(overwritePaths)
                : "";
            ImportTransaction transaction = ImportTransaction.Prepare(plans);
            bool assetEditingStarted = false;

            try
            {
                AssetDatabase.StartAssetEditing();
                assetEditingStarted = true;

                foreach (ImportPlan plan in plans)
                {
                    string absolute = JUIWindow.ResolveAssetPathOrThrow(plan.TargetPath);

                    if (plan.Entry.IsDirectory)
                    {
                        if (File.Exists(absolute))
                            throw new IOException($"フォルダ作成先に同名ファイルがあります: {plan.TargetPath}");

                        Directory.CreateDirectory(absolute);

                        if (plan.Entry.MetaBytes != null)
                            File.WriteAllBytes(absolute + ".meta", plan.Entry.MetaBytes);
                    }
                    else
                    {
                        if (Directory.Exists(absolute))
                            throw new IOException($"ファイル作成先に同名フォルダがあります: {plan.TargetPath}");

                        string parent = Path.GetDirectoryName(absolute);
                        if (!string.IsNullOrEmpty(parent))
                            Directory.CreateDirectory(parent);

                        if (plan.Entry.AssetBytes == null)
                            throw new InvalidDataException($"assetデータがありません: {plan.Entry.Pathname}");

                        File.WriteAllBytes(absolute, plan.Entry.AssetBytes);

                        if (plan.Entry.MetaBytes != null)
                            File.WriteAllBytes(absolute + ".meta", plan.Entry.MetaBytes);
                    }
                }

                AssetDatabase.StopAssetEditing();
                assetEditingStarted = false;
                transaction.Commit();
            }
            catch (Exception importException)
            {
                if (assetEditingStarted)
                {
                    try
                    {
                        AssetDatabase.StopAssetEditing();
                    }
                    catch (Exception stopException)
                    {
                        JUILog.Error("AssetDatabaseの編集終了処理に失敗しました。", stopException);
                    }
                    finally
                    {
                        assetEditingStarted = false;
                    }
                }

                try
                {
                    transaction.Rollback();
                    JUILog.Warning("Import中のエラーにより、ファイル変更をロールバックしました。");
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Importに失敗し、ロールバックも完了できませんでした。",
                        importException,
                        rollbackException);
                }

                throw;
            }

            return backupDirectory;
        }

        private sealed class ImportTransaction
        {
            private readonly string _projectRoot;
            private readonly string _transactionRoot;
            private readonly List<string> _existingFiles;
            private readonly List<string> _newFiles;
            private readonly List<string> _newDirectories;

            private ImportTransaction(
                string projectRoot,
                string transactionRoot,
                List<string> existingFiles,
                List<string> newFiles,
                List<string> newDirectories)
            {
                _projectRoot = projectRoot;
                _transactionRoot = transactionRoot;
                _existingFiles = existingFiles;
                _newFiles = newFiles;
                _newDirectories = newDirectories;
            }

            public static ImportTransaction Prepare(List<ImportPlan> plans)
            {
                string projectRoot = JUIWindow.ProjectRoot;
                string transactionRoot = Path.Combine(
                    JUIBackupManager.BackupRoot,
                    ".transaction_" + Guid.NewGuid().ToString("N"));
                var writeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var newDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ImportPlan plan in plans)
                {
                    string absolute = JUIWindow.ResolveAssetPathOrThrow(plan.TargetPath);

                    if (plan.Entry.IsDirectory)
                    {
                        AddMissingDirectories(absolute, newDirectories);
                    }
                    else
                    {
                        writeTargets.Add(absolute);
                        AddMissingDirectories(Path.GetDirectoryName(absolute), newDirectories);
                    }

                    if (plan.Entry.MetaBytes != null)
                        writeTargets.Add(absolute + ".meta");
                }

                var existingFiles = new List<string>();
                var newFiles = new List<string>();

                foreach (string target in writeTargets)
                {
                    if (!JUIWindow.IsPathInside(target, Path.Combine(projectRoot, "Assets")))
                        throw new InvalidOperationException($"Assets外のTransaction対象を拒否しました: {target}");

                    if (File.Exists(target))
                    {
                        string relative = target.Substring(projectRoot.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        string backup = Path.Combine(transactionRoot, relative);
                        string parent = Path.GetDirectoryName(backup);
                        if (!string.IsNullOrEmpty(parent))
                            Directory.CreateDirectory(parent);
                        File.Copy(target, backup, false);
                        existingFiles.Add(target);
                    }
                    else
                    {
                        newFiles.Add(target);
                    }
                }

                return new ImportTransaction(
                    projectRoot,
                    transactionRoot,
                    existingFiles,
                    newFiles,
                    newDirectories
                        .OrderByDescending(x => x.Length)
                        .ToList());
            }

            public void Commit()
            {
                DeleteTransactionDirectory();
            }

            public void Rollback()
            {
                foreach (string path in _newFiles)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }

                foreach (string original in _existingFiles)
                {
                    string relative = original.Substring(_projectRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string backup = Path.Combine(_transactionRoot, relative);
                    string parent = Path.GetDirectoryName(original);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);
                    File.Copy(backup, original, true);
                }

                foreach (string directory in _newDirectories)
                {
                    if (Directory.Exists(directory) &&
                        !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory, false);
                    }
                }

                DeleteTransactionDirectory();
            }

            private static void AddMissingDirectories(
                string directory,
                HashSet<string> newDirectories)
            {
                string assetsRoot = Path.GetFullPath(
                    Path.Combine(JUIWindow.ProjectRoot, "Assets"));

                while (!string.IsNullOrEmpty(directory) &&
                       JUIWindow.IsPathInside(directory, assetsRoot) &&
                       !directory.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(directory))
                        newDirectories.Add(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }

            private void DeleteTransactionDirectory()
            {
                if (Directory.Exists(_transactionRoot))
                    Directory.Delete(_transactionRoot, true);
            }
        }
    }

    internal static class JUIBackupManager
    {
        public static string BackupRoot =>
            Path.GetFullPath(Path.Combine(JUIWindow.ProjectRoot, "JUI_BAK"));

        public static string Create(List<string> overwritePaths)
        {
            if (overwritePaths.Count == 0)
                return "";

            string projectRoot = JUIWindow.ProjectRoot;
            string backupRoot = BackupRoot;
            string backupDirectory = Path.Combine(
                backupRoot,
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));

            int suffix = 1;
            while (Directory.Exists(backupDirectory))
            {
                backupDirectory = Path.Combine(
                    backupRoot,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + suffix);
                suffix++;
            }

            foreach (string assetPath in overwritePaths)
            {
                string source = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
                if (!JUIWindow.IsPathInside(source, Path.Combine(projectRoot, "Assets")))
                    throw new InvalidOperationException($"Assets外のファイルはバックアップできません: {assetPath}");

                string destination = Path.GetFullPath(Path.Combine(backupDirectory, assetPath));
                if (!JUIWindow.IsPathInside(destination, backupDirectory))
                    throw new InvalidOperationException($"不正なバックアップ先です: {assetPath}");

                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                File.Copy(source, destination, false);
            }

            JUILog.Info($"上書き前のファイルをバックアップしました: {backupDirectory} ({overwritePaths.Count} ファイル)");
            return backupDirectory;
        }

        public static int Restore(string backupDirectory)
        {
            string projectRoot = JUIWindow.ProjectRoot;
            string backupRoot = BackupRoot;
            backupDirectory = Path.GetFullPath(backupDirectory);

            if (!JUIWindow.IsPathInside(backupDirectory, backupRoot))
                throw new InvalidOperationException("JUI_BAK外のフォルダからは復元できません。");

            string backedUpAssets = Path.Combine(backupDirectory, "Assets");
            if (!Directory.Exists(backedUpAssets))
                throw new DirectoryNotFoundException("バックアップ内にAssetsフォルダがありません。");

            string[] files = Directory.GetFiles(backedUpAssets, "*", SearchOption.AllDirectories);

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (string source in files)
                {
                    string relative = source.Substring(backupDirectory.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string destination = Path.GetFullPath(Path.Combine(projectRoot, relative));

                    if (!JUIWindow.IsPathInside(destination, Path.Combine(projectRoot, "Assets")))
                        throw new InvalidOperationException($"Assets外への復元を拒否しました: {relative}");

                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    File.Copy(source, destination, true);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            return files.Length;
        }
    }

    internal static class JUILog
    {
        private const string Prefix = "[JUI] ";

        public static void Info(string message)
        {
            Debug.Log(Prefix + message);
        }

        public static void Warning(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }

        public static void Error(string message, Exception exception)
        {
            Debug.LogError(Prefix + message + "\n" + exception.Message);
        }
    }

    internal static class JUISettings
    {
        public static string GetString(string key, string defaultValue)
        {
            string value = EditorUserSettings.GetConfigValue(key);
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }

        public static void SetString(string key, string value)
        {
            EditorUserSettings.SetConfigValue(key, value ?? "");
        }

        public static bool GetBool(string key, bool defaultValue)
        {
            string value = EditorUserSettings.GetConfigValue(key);
            return bool.TryParse(value, out bool parsed) ? parsed : defaultValue;
        }

        public static void SetBool(string key, bool value)
        {
            EditorUserSettings.SetConfigValue(key, value.ToString());
        }

        public static int GetInt(string key, int defaultValue)
        {
            string value = EditorUserSettings.GetConfigValue(key);
            return int.TryParse(value, out int parsed) && parsed > 0
                ? parsed
                : defaultValue;
        }

        public static void SetInt(string key, int value)
        {
            EditorUserSettings.SetConfigValue(key, Math.Max(1, value).ToString());
        }
    }

    /// <summary>
    /// Warns when a normal UnityPackage import is started outside JUI.
    /// JUI itself uses direct extraction + AssetDatabase.Refresh(), so its own
    /// imports do not fire this callback.
    /// </summary>
    [InitializeOnLoad]
    internal static class JUIStandardImportWatcher
    {
        private const string PrefWarnStandardImport = "JUI.WarnStandardImport";
        private static bool _dialogQueued;

        static JUIStandardImportWatcher()
        {
            AssetDatabase.importPackageStarted += OnImportPackageStarted;
        }

        private static void OnImportPackageStarted(string packageName)
        {
            if (!JUISettings.GetBool(PrefWarnStandardImport, true))
                return;

            if (_dialogQueued)
                return;

            _dialogQueued = true;
            JUILog.Warning(
                "JUIを使用していないためインポート先は変更されません。 " +
                $"Package: {packageName}");

            // Delay the modal dialog so it does not execute directly inside
            // Unity's import-package event callback.
            EditorApplication.delayCall += () =>
            {
                _dialogQueued = false;
                EditorUtility.DisplayDialog(
                    "JUI",
                    "JUIを使用していないためインポート先は変更されません。\n\n" +
                    $"Package: {packageName}",
                    "OK");
            };
        }
    }

    /// <summary>
    /// TAR reader for UnityPackage archives.
    /// Supports USTAR prefix, GNU LongLink names and PAX path records.
    /// </summary>
    internal static class TarReader
    {
        private const int BlockSize = 512;

        public static void Read(
            Stream stream,
            Action<string, byte[], byte> onEntry,
            Func<string, long, byte, bool> onHeader = null)
        {
            byte[] header = new byte[BlockSize];
            string pendingLongName = null;
            string pendingPaxPath = null;

            while (true)
            {
                int read = ReadExactlyOrLess(stream, header, 0, BlockSize);
                if (read == 0)
                    break;
                if (read != BlockSize)
                    throw new InvalidDataException("TARヘッダーが途中で終了しています。");

                if (IsAllZero(header))
                    break;

                string headerName = ReadHeaderName(header);
                long size = ParseOctal(header, 124, 12);
                byte typeFlag = header[156];

                if (size < 0)
                    throw new InvalidDataException($"不正なTARエントリサイズです: {size}");

                bool isExtendedNameHeader = typeFlag == (byte)'L' || typeFlag == (byte)'x';
                string name = !string.IsNullOrEmpty(pendingPaxPath)
                    ? pendingPaxPath
                    : !string.IsNullOrEmpty(pendingLongName)
                        ? pendingLongName
                        : headerName;

                // Report the size before allocating or reading the entry payload.
                if (!isExtendedNameHeader && onHeader != null &&
                    !onHeader(name, size, typeFlag))
                {
                    return;
                }

                if (size > int.MaxValue)
                    throw new InvalidDataException($"未対応のTARエントリサイズです: {size}");

                byte[] data = new byte[(int)size];
                if (size > 0)
                {
                    int contentRead = ReadExactlyOrLess(stream, data, 0, (int)size);
                    if (contentRead != (int)size)
                        throw new InvalidDataException($"TARエントリが途中で終了しています: {headerName}");
                }

                long padded = ((size + BlockSize - 1) / BlockSize) * BlockSize;
                long skip = padded - size;
                Skip(stream, skip);

                // GNU LongLink: the payload is the full name for the next header.
                if (typeFlag == (byte)'L')
                {
                    pendingLongName = DecodeExtendedName(data);
                    continue;
                }

                // POSIX PAX extended header: "path" overrides the next header name.
                if (typeFlag == (byte)'x')
                {
                    pendingPaxPath = ReadPaxPath(data);
                    continue;
                }

                pendingLongName = null;
                pendingPaxPath = null;

                // '0' or NUL = regular file. '7' = contiguous file. '5' = directory.
                if (typeFlag == 0 || typeFlag == (byte)'0' ||
                    typeFlag == (byte)'7' || typeFlag == (byte)'5')
                    onEntry(name, data, typeFlag);
            }
        }

        private static string ReadHeaderName(byte[] header)
        {
            string name = ReadNullTerminatedString(header, 0, 100);
            string prefix = ReadNullTerminatedString(header, 345, 155);
            return string.IsNullOrEmpty(prefix) ? name : prefix.TrimEnd('/') + "/" + name;
        }

        private static string DecodeExtendedName(byte[] data)
        {
            return Encoding.UTF8.GetString(data).TrimEnd('\0', '\r', '\n');
        }

        private static string ReadPaxPath(byte[] data)
        {
            int position = 0;
            string path = null;

            while (position < data.Length)
            {
                int space = Array.IndexOf(data, (byte)' ', position);
                if (space < 0)
                    throw new InvalidDataException("PAXヘッダーのレコード長が不正です。");

                string lengthText = Encoding.ASCII.GetString(data, position, space - position);
                if (!int.TryParse(lengthText, out int recordLength) || recordLength <= 0)
                    throw new InvalidDataException("PAXヘッダーのレコード長を解析できません。");

                int recordEnd = checked(position + recordLength);
                if (recordEnd > data.Length || space + 1 >= recordEnd)
                    throw new InvalidDataException("PAXヘッダーが途中で終了しています。");

                int valueLength = recordEnd - (space + 1);
                if (valueLength > 0 && data[recordEnd - 1] == (byte)'\n')
                    valueLength--;

                string record = Encoding.UTF8.GetString(data, space + 1, valueLength);
                int equals = record.IndexOf('=');
                if (equals > 0 && record.Substring(0, equals) == "path")
                    path = record.Substring(equals + 1);

                position = recordEnd;
            }

            return path;
        }

        private static int ReadExactlyOrLess(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0)
                    break;
                total += n;
            }
            return total;
        }

        private static bool IsAllZero(byte[] data)
        {
            for (int i = 0; i < data.Length; i++)
                if (data[i] != 0)
                    return false;
            return true;
        }

        private static string ReadNullTerminatedString(byte[] data, int offset, int length)
        {
            int end = offset;
            int max = offset + length;
            while (end < max && data[end] != 0)
                end++;

            return Encoding.UTF8.GetString(data, offset, end - offset);
        }

        private static long ParseOctal(byte[] data, int offset, int length)
        {
            string s = ReadNullTerminatedString(data, offset, length).Trim();
            if (string.IsNullOrEmpty(s))
                return 0;

            long value = 0;
            foreach (char c in s)
            {
                if (c < '0' || c > '7')
                    continue;
                checked { value = (value * 8) + (c - '0'); }
            }
            return value;
        }

        private static void Skip(Stream stream, long bytes)
        {
            byte[] buffer = new byte[4096];
            while (bytes > 0)
            {
                int request = (int)Math.Min(buffer.Length, bytes);
                int n = stream.Read(buffer, 0, request);
                if (n <= 0)
                    throw new EndOfStreamException();
                bytes -= n;
            }
        }
    }
}
#endif
