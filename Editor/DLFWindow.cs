using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The DLF window: shows, the way a git client shows changes, which shared files changed on this machine and which
/// are waiting to be downloaded, whether DLF.txt is committed and whether the bucket has it, and holds every action
/// (download, upload, reset, unlock). The scripts do the work; this only asks them for the status and runs their commands.
/// </summary>
public sealed class DLFWindow : EditorWindow {
	private const float k_RowHeight = 18;
	// The colours a git client uses for added, modified and deleted
	private static readonly Dictionary<char, Color> k_Colors = new() {
		['A'] = new Color(0.25f, 0.73f, 0.31f), ['M'] = new Color(0.82f, 0.60f, 0.13f), ['D'] = new Color(0.97f, 0.32f, 0.29f)
	};

	private Dictionary<string, string> m_Status = new();
	// "A path", "M path" or "D path": changed on this machine, and what a download would bring
	private List<string> m_Changed = new(), m_Incoming = new();
	private readonly HashSet<string> m_Picked = new();
	private string m_Message = "";
	private Vector2 m_Scroll;
	private static GUIStyle s_Badge, s_Folder;

	[MenuItem("Tools/DLF/DLF")]
	private static void Open() {
		GetWindow<DLFWindow>("DLF").minSize = new Vector2(400, 420);
	}

	private void OnEnable() {
		Refresh();
	}

	// Asks the scripts for the status: "name=value" lines, with one "changed" or "incoming" line per file
	private void Refresh() {
		var result = DLF.Run("status");
		var lines = result.output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('=')).Select(l => (name: l.Substring(0, l.IndexOf('=')), value: l.Substring(l.IndexOf('=') + 1))).ToList();
		m_Changed = lines.Where(l => l.name == "changed").Select(l => l.value).ToList();
		m_Incoming = lines.Where(l => l.name == "incoming").Select(l => l.value).ToList();
		m_Status = lines.Where(l => l.name != "changed" && l.name != "incoming").GroupBy(l => l.name).ToDictionary(g => g.Key, g => g.Last().value);
		m_Picked.IntersectWith(m_Changed.Select(c => c.Substring(2)));
		if (result.code != 0) {
			m_Message = result.messages;
		}
		Repaint();
	}

	private string Status(string name) {
		return m_Status.TryGetValue(name, out var value) ? value : "";
	}

	private void Do(string command, IEnumerable<string> arguments = null) {
		var result = DLF.Run(command, arguments);
		AssetDatabase.Refresh();
		m_Message = result.messages.Length > 0 ? result.messages : result.code == 0 ? "Done." : "Stopped.";
		DLFOverlay.Refresh();
		Refresh();
	}

	private void OnGUI() {
		s_Badge ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
		s_Folder ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } };
		var branch = Status("branch").Length > 0 ? Status("branch") : "no branch";
		var canUpload = Status("key") == "write";

		EditorGUILayout.LabelField($"{Status("project")} - {branch}", EditorStyles.boldLabel);
		EditorGUILayout.LabelField(canUpload ? "This machine can upload." : Status("key") == "read" ? "This machine downloads only: it has no write key." : "No key is set up yet. Open Settings.", EditorStyles.miniLabel);
		if (Status("committed") == "no") {
			Row("console.warnicon.sml", "DLF.txt changed and is not committed. Others get the new files once it is committed and they have that commit.");
		}
		if (Status("bucket") == "unreachable") {
			Row("console.erroricon.sml", "The bucket could not be reached.");
		}
		else if (Status("lock").Length > 0) {
			Row("console.warnicon.sml", $"{Status("lock")} right now.");
		}
		else if (Status("uploaded") == "yes") {
			Row("TestPassed", $"The bucket has the files DLF.txt lists, as the set of {branch}.");
		}
		else if (Status("uploaded") == "no") {
			Row("console.warnicon.sml", canUpload ? $"The bucket does not have this list for {branch}. Upload it." : $"The bucket does not have this list for {branch}. Whoever changed the files has to upload them.");
		}

		m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, EditorStyles.helpBox);
		if (m_Changed.Count == 0 && m_Incoming.Count == 0) {
			GUILayout.Label(Status("files").Length == 0 ? "Nothing is shared yet. Set the patterns in Settings; on a machine with a write key, Upload then starts the list." : $"Nothing changed and nothing to download: the {Status("files")} shared files are the ones DLF.txt lists.", EditorStyles.wordWrappedLabel);
		}
		if (m_Changed.Count > 0) {
			GUILayout.Label($"Changed on this machine ({m_Changed.Count})", EditorStyles.boldLabel);
			GUILayout.Label(canUpload ? "Upload to share them. Tick files to reset only those." : "Not shared with anyone. Tick files to reset only those.", EditorStyles.miniLabel);
			foreach (var change in m_Changed) {
				FileRow(change, true);
			}
		}
		if (m_Incoming.Count > 0) {
			GUILayout.Label($"To download ({m_Incoming.Count})", EditorStyles.boldLabel);
			GUILayout.Label("DLF.txt lists these differently from what this machine last took.", EditorStyles.miniLabel);
			foreach (var change in m_Incoming) {
				FileRow(change, false);
			}
		}
		EditorGUILayout.EndScrollView();

		if (m_Message.Length > 0) {
			EditorGUILayout.HelpBox(m_Message, MessageType.None);
		}
		// Three rows of buttons, every button of a row the same width
		var half = GUILayout.Width((position.width - 14) / 2);
		using (new EditorGUILayout.HorizontalScope()) {
			using (new EditorGUI.DisabledScope(m_Incoming.Count == 0)) {
				// With changes here that were not uploaded, downloading replaces them: that needs a yes
				if (GUILayout.Button(new GUIContent("Download", "Makes the shared files the ones DLF.txt lists."), half, GUILayout.Height(26)) &&
					(m_Changed.Count == 0 || EditorUtility.DisplayDialog("DLF", $"{m_Changed.Count} shared files were changed on this machine and not uploaded. Downloading replaces them with the ones DLF.txt lists.", "Download", "Cancel"))) {
					Do("download");
				}
			}
			using (new EditorGUI.DisabledScope(!canUpload || (m_Changed.Count == 0 && Status("uploaded") == "yes"))) {
				if (GUILayout.Button(new GUIContent("Upload", "Lists the shared files as they are now in DLF.txt and puts them in the bucket. Commit DLF.txt afterwards."), half, GUILayout.Height(26))) {
					Do("upload");
				}
			}
		}
		using (new EditorGUILayout.HorizontalScope()) {
			using (new EditorGUI.DisabledScope(m_Changed.Count == 0)) {
				if (GUILayout.Button(new GUIContent("Reset All", "Throws local changes away: every shared file becomes the one the checked-out commit lists."), half) &&
					EditorUtility.DisplayDialog("DLF", "Replace all shared files on this machine with the ones the checked-out commit lists? Local changes to them are lost.", "Reset", "Cancel")) {
					Do("reset");
				}
			}
			using (new EditorGUI.DisabledScope(m_Picked.Count == 0)) {
				if (GUILayout.Button(new GUIContent($"Reset Ticked ({m_Picked.Count})", "The same for the ticked files only."), half) &&
					EditorUtility.DisplayDialog("DLF", $"Replace the {m_Picked.Count} ticked files with the ones the checked-out commit lists? Local changes to them are lost.", "Reset", "Cancel")) {
					Do("reset", m_Picked.ToList());
				}
			}
		}
		var unlock = canUpload && Status("lock").Length > 0;
		var third = unlock ? GUILayout.Width((position.width - 18) / 3) : half;
		using (new EditorGUILayout.HorizontalScope()) {
			if (GUILayout.Button("Refresh", third)) {
				m_Message = "";
				Refresh();
			}
			if (GUILayout.Button("Settings", third)) {
				EditorApplication.ExecuteMenuItem("Tools/DLF/Settings");
			}
			if (unlock && GUILayout.Button(new GUIContent("Unlock", "Clears a lock that an upload which died left behind."), third)) {
				Do("unlock");
			}
		}
	}

	private static void Row(string icon, string text) {
		using (new EditorGUILayout.HorizontalScope()) {
			GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20), GUILayout.Height(18));
			GUILayout.Label(text, EditorStyles.wordWrappedLabel);
		}
	}

	// One file: a tick box where it can be reset, a coloured letter (Added, Modified, Deleted), its name and its folder
	private void FileRow(string change, bool pickable) {
		var path = change.Substring(2);
		var row = EditorGUILayout.GetControlRect(false, k_RowHeight);
		if (pickable) {
			var picked = GUI.Toggle(new Rect(row.x, row.y, 16, row.height), m_Picked.Contains(path), GUIContent.none);
			if (picked != m_Picked.Contains(path) && !m_Picked.Remove(path)) {
				m_Picked.Add(path);
			}
		}
		var badge = new Rect(row.x + 20, row.y + 2, 16, row.height - 4);
		EditorGUI.DrawRect(badge, k_Colors.TryGetValue(change[0], out var color) ? color : Color.gray);
		GUI.Label(badge, change.Substring(0, 1), s_Badge);
		var name = new GUIContent(Path.GetFileName(path), path);
		var width = Mathf.Min(EditorStyles.label.CalcSize(name).x, row.width - 60);
		GUI.Label(new Rect(row.x + 42, row.y, width, row.height), name);
		GUI.Label(new Rect(row.x + 46 + width, row.y, row.width - 46 - width, row.height), Path.GetDirectoryName(path)?.Replace('\\', '/'), s_Folder);
	}
}