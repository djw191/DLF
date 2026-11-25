using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Edits DLF.cfg (which files are shared, the bucket and the read key everyone uses, all committed) and this machine's
/// write key, stored outside the repository. Shows the files the patterns match, and keeps them ignored by git.
/// </summary>
public sealed class DLFSettingsWindow : EditorWindow {
	private const float k_RowHeight = 18;
	private const float k_ListHeight = 290;
	// The lines of DLF.cfg this window writes; any other line in it is kept as it is
	private static readonly string[] k_Names = { "endpoint", "bucket", "project", "region", "keepOldVersions", "oneSetForAllBranches", "readAccessKeyId", "readSecretAccessKey", "pattern" };

	private string m_Patterns, m_Endpoint, m_Bucket, m_Project, m_Region, m_ReadKeyId, m_ReadKeySecret, m_WriteKeyId, m_WriteKeySecret;
	// Every file the patterns match now
	private List<(string path, long size)> m_Files;
	private Vector2 m_Scroll, m_ListScroll;
	private bool m_ShowKeys, m_KeepOldVersions, m_OneSet;
	private static GUIStyle s_Size, s_Folder;

	// Opened from the gear of the DLF window: settings are rare, so they have no menu item of their own
	internal static void Open() {
		var window = GetWindow<DLFSettingsWindow>("DLF Settings");
		window.minSize = new Vector2(420, 300);
		// By default just tall enough to end below the collapsed Keys header; opening Keys scrolls
		var pattern = Mathf.Clamp(window.m_Patterns.Split('\n').Length + 1, 3, 10) * 15 + 6;
		window.position = new Rect(window.position.x, window.position.y, Mathf.Max(window.position.width, 520), k_ListHeight + pattern + 166);
	}

	private static string Shared(string name) {
		return DLF.Values(DLF.At(DLF.k_ConfigName), name).FirstOrDefault() ?? "";
	}

	private void OnEnable() {
		m_Endpoint = Shared("endpoint");
		m_Bucket = Shared("bucket");
		m_Project = Shared("project");
		m_Region = Shared("region");
		m_KeepOldVersions = Shared("keepOldVersions") == "true";
		m_OneSet = Shared("oneSetForAllBranches") == "true";
		m_ReadKeyId = Shared("readAccessKeyId");
		m_ReadKeySecret = Shared("readSecretAccessKey");
		m_Patterns = string.Join("\n", DLF.Values(DLF.At(DLF.k_ConfigName), "pattern"));
		m_WriteKeyId = DLF.Values(DLF.keyPath, "accessKeyId").FirstOrDefault() ?? "";
		m_WriteKeySecret = DLF.Values(DLF.keyPath, "secretAccessKey").FirstOrDefault() ?? "";
		m_Files = null;
	}

	private IEnumerable<string> Patterns() {
		return m_Patterns.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).Distinct();
	}

	private void OnGUI() {
		s_Size ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
		s_Folder ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } };
		m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

		EditorGUILayout.LabelField("DLF Patterns", EditorStyles.boldLabel);
		EditorGUI.BeginChangeCheck();
		m_Patterns = EditorGUILayout.TextArea(m_Patterns, GUILayout.Height(Mathf.Clamp(m_Patterns.Split('\n').Length + 1, 3, 10) * 15 + 6));
		if (EditorGUI.EndChangeCheck() || m_Files == null) {
			m_Files = DLF.Matches(Patterns()).Select(p => (p, new FileInfo(DLF.At(p)).Length)).ToList();
		}
		EditorGUILayout.LabelField($"{m_Files.Count} files, {EditorUtility.FormatBytes(m_Files.Sum(f => f.size))}", EditorStyles.miniBoldLabel);
		Files(GUILayoutUtility.GetRect(0, 100000, k_ListHeight, k_ListHeight));
		EditorGUILayout.HelpBox("Saving also lists the patterns in .gitignore, so these files stay out of git.", MessageType.Info);

		EditorGUILayout.Space();
		m_ShowKeys = EditorGUILayout.Foldout(m_ShowKeys, "Keys", true, EditorStyles.foldoutHeader);
		if (m_ShowKeys) {
			EditorGUI.indentLevel++;
			// Wide enough for the longest label
			EditorGUIUtility.labelWidth = 180;
			EditorGUILayout.LabelField("Bucket", EditorStyles.boldLabel);
			m_Endpoint = EditorGUILayout.TextField("S3 Endpoint", m_Endpoint).Trim();
			m_Bucket = EditorGUILayout.TextField("Bucket Name", m_Bucket).Trim();
			m_Project = EditorGUILayout.TextField(new GUIContent("Project", "The folder in the bucket that holds this project's files, so that several projects can share one bucket."), m_Project).Trim();
			m_Region = EditorGUILayout.TextField(new GUIContent("Region", "Leave empty for Cloudflare R2 (\"auto\"). Other S3 services need theirs."), m_Region).Trim();
			m_KeepOldVersions = EditorGUILayout.Toggle(new GUIContent("Keep Old Versions", "Off: a file is removed from the bucket once no branch lists it, so the bucket stays small and older commits cannot fetch their files. On: nothing is ever removed, so every commit can fetch, and the bucket grows with every change."), m_KeepOldVersions);
			m_OneSet = EditorGUILayout.Toggle(new GUIContent("One Set For All Branches", "Off: each branch has its own set of files in the bucket, so branches with different files can all fetch. On: all branches share one set, the one published last; another branch's files are replaced by it."), m_OneSet);
			EditorGUILayout.HelpBox("Where the files are kept, and how. Committed in DLF.cfg.", MessageType.Info);
			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Read key", EditorStyles.boldLabel);
			m_ReadKeyId = EditorGUILayout.TextField("Access Key ID", m_ReadKeyId).Trim();
			m_ReadKeySecret = EditorGUILayout.TextField("Secret Access Key", m_ReadKeySecret).Trim();
			EditorGUILayout.HelpBox("Fetches the files. It is committed in DLF.cfg, so everyone with the project can see it: use a read-only token.", MessageType.Info);
			EditorGUILayout.Space();
			EditorGUILayout.LabelField("Write key", EditorStyles.boldLabel);
			m_WriteKeyId = EditorGUILayout.TextField("Access Key ID", m_WriteKeyId).Trim();
			m_WriteKeySecret = EditorGUILayout.PasswordField("Secret Access Key", m_WriteKeySecret).Trim();
			EditorGUILayout.HelpBox("Lets this machine upload. Optional. It stays on this machine and is never stored in the project.", MessageType.Info);
			EditorGUIUtility.labelWidth = 0;
			EditorGUI.indentLevel--;
		}

		EditorGUILayout.EndScrollView();
		if (GUILayout.Button("Save", GUILayout.Height(28))) {
			Save();
			Close();
		}
	}

	// A file list of fixed height: one row per file, only the rows in view are drawn
	private void Files(Rect view) {
		GUI.Box(view, GUIContent.none, EditorStyles.helpBox);
		view = new Rect(view.x + 1, view.y + 1, view.width - 2, view.height - 2);
		var content = new Rect(0, 0, view.width - 16, m_Files.Count * k_RowHeight);
		m_ListScroll = GUI.BeginScrollView(view, m_ListScroll, content);
		var first = Mathf.Max(0, (int)(m_ListScroll.y / k_RowHeight));
		var last = Mathf.Min(m_Files.Count - 1, (int)((m_ListScroll.y + view.height) / k_RowHeight));
		for (var i = first; i <= last; i++) {
			var (path, size) = m_Files[i];
			var row = new Rect(0, i * k_RowHeight, content.width, k_RowHeight);
			if (i % 2 == 1) {
				EditorGUI.DrawRect(row, new Color(1, 1, 1, 0.03f));
			}
			var icon = AssetDatabase.GetCachedIcon(path) ?? EditorGUIUtility.IconContent("TextAsset Icon").image;
			GUI.DrawTexture(new Rect(row.x + 4, row.y + 1, 16, 16), icon, ScaleMode.ScaleToFit);
			var name = new GUIContent(Path.GetFileName(path), path);
			var nameWidth = Mathf.Min(EditorStyles.label.CalcSize(name).x, row.width - 100);
			// A click shows the file in the Project window
			if (GUI.Button(new Rect(row.x + 24, row.y, nameWidth, row.height), name, EditorStyles.label)) {
				EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path));
			}
			GUI.Label(new Rect(row.x + 28 + nameWidth, row.y, row.width - 100 - nameWidth, row.height), Path.GetDirectoryName(path)?.Replace('\\', '/'), s_Folder);
			GUI.Label(new Rect(row.xMax - 72, row.y, 68, row.height), EditorUtility.FormatBytes(size), s_Size);
		}
		GUI.EndScrollView();
	}

	private void Save() {
		var patterns = Patterns().ToList();
		var config = DLF.At(DLF.k_ConfigName);
		var kept = File.Exists(config) ? File.ReadAllLines(config).Where(l => !k_Names.Any(n => l.StartsWith(n + "="))) : Enumerable.Empty<string>();
		var lines = new List<string> { $"endpoint={m_Endpoint}", $"bucket={m_Bucket}", $"project={m_Project}" };
		if (m_Region.Length > 0) {
			lines.Add($"region={m_Region}");
		}
		if (m_KeepOldVersions) {
			lines.Add("keepOldVersions=true");
		}
		if (m_OneSet) {
			lines.Add("oneSetForAllBranches=true");
		}
		lines.Add($"readAccessKeyId={m_ReadKeyId}");
		lines.Add($"readSecretAccessKey={m_ReadKeySecret}");
		File.WriteAllText(config, string.Concat(kept.Concat(lines).Concat(patterns.Select(p => $"pattern={p}")).Select(l => l + "\n")));

		// The scripts list the patterns in .gitignore too, so that these files stay out of git
		Note(DLF.Run("ignore"));

		// The key's place depends on the bucket, which may just have changed
		DLF.keyPath = null;
		if (DLF.keyPath.Length == 0) {
			Debug.LogWarning("DLF: the write key was not saved, because git could not be run in this project.");
		}
		else if (m_WriteKeySecret.Length == 0) {
			File.Delete(DLF.keyPath);
		}
		else {
			Directory.CreateDirectory(Path.GetDirectoryName(DLF.keyPath));
			File.WriteAllText(DLF.keyPath, $"accessKeyId={m_WriteKeyId}\nsecretAccessKey={m_WriteKeySecret}\n");
			// With a write key, the reminders in the git hooks
			Note(DLF.Run("install"));
		}
	}

	private static void Note((int code, string output, string messages) result) {
		if (result.messages.Length > 0) {
			Debug.LogWarning(result.messages);
		}
	}
}