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

        private string _packagePath = "";
        private string _destination = "Assets/JUIImport";
        private bool _remapDestination = true;
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
            _destination = EditorPrefs.GetString(PrefDefaultDestination, "Assets/JUIImport");
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("JustUnitypackageImporter2Folder", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            DrawPackagePicker();
            HandleDragAndDrop();

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
                            EditorPrefs.SetString(PrefDefaultDestination, _destination);
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
                        _destination = EditorPrefs.GetString(PrefDefaultDestination, "Assets/JUIImport");
                        JUILog.Info($"Defaultインポート先を読み込みました: {_destination}");
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("バックアップフォルダを開く", GUILayout.Width(180)))
                OpenBackupFolder();
            if (GUILayout.Button("バックアップから復元する", GUILayout.Width(180)))
                RestoreFromBackup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            bool warn = EditorPrefs.GetBool(PrefWarnStandardImport, true);
            bool newWarn = EditorGUILayout.ToggleLeft(
                "JUIを使用しないUnityPackage Import時に通知する",
                warn);
            if (newWarn != warn)
            {
                EditorPrefs.SetBool(PrefWarnStandardImport, newWarn);
                JUILog.Info($"通常UnityPackage Import時の通知を{(newWarn ? "有効" : "無効")}にしました。");
            }

            EditorGUILayout.Space(8);

            if (!string.IsNullOrEmpty(_loadError))
                EditorGUILayout.HelpBox(_loadError, MessageType.Error);

            DrawEntryList();

            EditorGUILayout.Space(6);

            using (new EditorGUI.DisabledScope(_entries.Count == 0))
            {
                if (GUILayout.Button("これでインポートする", GUILayout.Height(34)))
                    ImportSelected();
            }
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

            EditorGUILayout.EndHorizontal();
        }

        private void HandleDragAndDrop()
        {
            Rect dropArea = GUILayoutUtility.GetRect(0, 46, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "UnityPackageをここへドラッグ＆ドロップ");

            Event e = Event.current;
            if (!dropArea.Contains(e.mousePosition))
                return;

            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                bool valid = DragAndDrop.paths.Any(p =>
                    string.Equals(Path.GetExtension(p), ".unitypackage", StringComparison.OrdinalIgnoreCase));

                DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

                if (e.type == EventType.DragPerform && valid)
                {
                    DragAndDrop.AcceptDrag();
                    string path = DragAndDrop.paths.First(p =>
                        string.Equals(Path.GetExtension(p), ".unitypackage", StringComparison.OrdinalIgnoreCase));
                    LoadPackage(path);
                }

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

            if (isFolder)
            {
                List<PackageEntry> entries = node.GetEntriesRecursive().ToList();
                bool anySelected = entries.Any(x => x.Selected);
                bool allSelected = entries.Count > 0 && entries.All(x => x.Selected);

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
                EditorGUILayout.LabelField(node.Name, EditorStyles.label);
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

            try
            {
                _entries = UnityPackageReader.Read(path)
                    .OrderBy(x => x.Pathname, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _treeRoot = PackageTreeNode.Build(_entries);

                if (_entries.Count == 0)
                    _loadError = "UnityPackage内にインポート可能なAssetが見つかりませんでした.";
                else
                    JUILog.Info($"UnityPackageを読み込みました: {path} ({_entries.Count} 項目)");
            }
            catch (Exception ex)
            {
                _entries.Clear();
                _treeRoot = null;
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

        private void ToggleEntry(PackageEntry entry, bool value)
        {
            entry.Selected = value;

            if (!entry.IsDirectory)
                return;

            string prefix = entry.Pathname.TrimEnd('/') + "/";
            foreach (PackageEntry child in _entries)
            {
                if (child.Pathname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    child.Selected = value;
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
                plans = BuildImportPlans(selected, _remapDestination, destination);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("JUI", "Import計画の作成に失敗しました。\n" + ex.Message, "OK");
                return;
            }

            List<string> warnings = JUIConflictChecker.Check(plans);
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
            string destination)
        {
            var plans = new List<ImportPlan>(selected.Count);

            foreach (PackageEntry entry in selected)
            {
                string original = NormalizeAssetPath(entry.Pathname);

                if (!original.StartsWith("Assets", StringComparison.Ordinal))
                    throw new InvalidDataException($"Assets配下ではないpathnameには対応していません: {original}");

                string target;
                if (remap)
                {
                    string relative = original.Length == "Assets".Length
                        ? ""
                        : original.Substring("Assets".Length).TrimStart('/');

                    target = string.IsNullOrEmpty(relative)
                        ? destination
                        : destination.TrimEnd('/') + "/" + relative;
                }
                else
                {
                    target = original;
                }

                plans.Add(new ImportPlan(entry, NormalizeAssetPath(target)));
            }

            return plans
                .OrderBy(x => x.Entry.IsDirectory ? 0 : 1)
                .ThenBy(x => x.TargetPath.Count(c => c == '/'))
                .ToList();
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
    }

    internal sealed class PackageTreeNode
    {
        public readonly string Name;
        public readonly string FullPath;
        public readonly List<PackageTreeNode> Children = new List<PackageTreeNode>();
        public PackageEntry Entry;
        public bool Expanded = true;

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
    }

    internal sealed class PackageEntry
    {
        public string Guid;
        public string Pathname;
        public byte[] AssetBytes;
        public byte[] MetaBytes;
        public bool IsDirectory;
        public bool Selected = true;
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

        public static List<PackageEntry> Read(string unityPackagePath)
        {
            if (!File.Exists(unityPackagePath))
                throw new FileNotFoundException("UnityPackageが見つかりません。", unityPackagePath);

            var groups = new Dictionary<string, RawGroup>(StringComparer.OrdinalIgnoreCase);

            using (FileStream fs = File.OpenRead(unityPackagePath))
            using (var gzip = new GZipStream(fs, CompressionMode.Decompress))
            {
                TarReader.Read(gzip, (name, bytes, typeFlag) =>
                {
                    string normalized = name.Replace('\\', '/').TrimStart('.', '/');
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
                });
            }

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

        private static string DecodeText(byte[] bytes)
        {
            // pathname is normally UTF-8. Strip UTF-8 BOM if present.
            string text = Encoding.UTF8.GetString(bytes);
            return text.TrimStart('\uFEFF');
        }
    }

    internal static class JUIConflictChecker
    {
        public static List<string> Check(List<ImportPlan> plans)
        {
            var warnings = new List<string>();
            string projectRoot = JUIWindow.ProjectRoot;

            foreach (ImportPlan plan in plans)
            {
                string absolute = Path.GetFullPath(Path.Combine(projectRoot, plan.TargetPath));

                if (!JUIWindow.IsPathInside(absolute, Path.Combine(projectRoot, "Assets")))
                {
                    warnings.Add($"[危険] Assets外への出力: {plan.TargetPath}");
                    continue;
                }

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

        private static string ExtractGuidFromMeta(byte[] metaBytes)
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
            string projectRoot = JUIWindow.ProjectRoot;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ImportPlan plan in plans)
            {
                string absolute = Path.GetFullPath(Path.Combine(projectRoot, plan.TargetPath));

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
            string projectRoot = JUIWindow.ProjectRoot;
            List<string> overwritePaths = GetOverwritePaths(plans);
            string backupDirectory = createBackup
                ? JUIBackupManager.Create(overwritePaths)
                : "";

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (ImportPlan plan in plans)
                {
                    string absolute = Path.GetFullPath(Path.Combine(projectRoot, plan.TargetPath));
                    string assetsRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets"));

                    if (!JUIWindow.IsPathInside(absolute, assetsRoot))
                        throw new InvalidOperationException($"Assets外への書き込みを拒否しました: {plan.TargetPath}");

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
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            return backupDirectory;
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

        public static void Error(string message, Exception exception)
        {
            Debug.LogError(Prefix + message + "\n" + exception.Message);
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
            if (!EditorPrefs.GetBool(PrefWarnStandardImport, true))
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
    /// Small TAR reader sufficient for UnityPackage archives.
    /// Supports regular files and ignores directory records.
    /// </summary>
    internal static class TarReader
    {
        private const int BlockSize = 512;

        public static void Read(Stream stream, Action<string, byte[], byte> onEntry)
        {
            byte[] header = new byte[BlockSize];

            while (true)
            {
                int read = ReadExactlyOrLess(stream, header, 0, BlockSize);
                if (read == 0)
                    break;
                if (read != BlockSize)
                    throw new InvalidDataException("TARヘッダーが途中で終了しています。");

                if (IsAllZero(header))
                    break;

                string name = ReadNullTerminatedString(header, 0, 100);
                long size = ParseOctal(header, 124, 12);
                byte typeFlag = header[156];

                if (size < 0 || size > int.MaxValue)
                    throw new InvalidDataException($"未対応のTARエントリサイズです: {size}");

                byte[] data = new byte[(int)size];
                if (size > 0)
                {
                    int contentRead = ReadExactlyOrLess(stream, data, 0, (int)size);
                    if (contentRead != (int)size)
                        throw new InvalidDataException($"TARエントリが途中で終了しています: {name}");
                }

                long padded = ((size + BlockSize - 1) / BlockSize) * BlockSize;
                long skip = padded - size;
                Skip(stream, skip);

                // '0' or NUL = regular file. '5' = directory.
                if (typeFlag == 0 || typeFlag == (byte)'0' || typeFlag == (byte)'5')
                    onEntry(name, data, typeFlag);
            }
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
