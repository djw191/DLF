using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The DLF window: shows, the way a git client shows changes, which shared files changed on this machine and which
/// are waiting to be downloaded, whether DLF.txt is committed and whether the bucket has it, and holds every action
/// (download, upload, reset, unlock). A download never replaces a file changed here: only a reset does. The scripts do
/// the work, in the background; this only asks them for the status and starts their commands.
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
	// How many files are in both lists: changed here and listed differently by DLF.txt
	private int m_Conflicts;
	// While the status is being asked for
	private bool m_Checking;
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

	// Asks the scripts for the status, in the background: "name=value" lines, with one "changed" or "incoming" line per file
	private void Refresh() {
		if (m_Checking) {
			return;
		}
		m_Checking = true;
		DLF.RunInBackground("status", null, result => {
			if (!this) {
				return;
			}
			m_Checking = false;
			Show(result);
		});
	}

	private void Show((int code, string output, string messages) result) {
		var lines = result.output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('=')).Select(l => (name: l.Substring(0, l.IndexOf('=')), value: l.Substring(l.IndexOf('=') + 1))).ToList();
		m_Changed = lines.Where(l => l.name == "changed").Select(l => l.value).ToList();
		m_Incoming = lines.Where(l => l.name == "incoming").Select(l => l.value).ToList();
		m_Status = lines.Where(l => l.name != "changed" && l.name != "incoming").GroupBy(l => l.name).ToDictionary(g => g.Key, g => g.Last().value);
		var changed = new HashSet<string>(m_Changed.Select(c => c.Substring(2)));
		m_Picked.IntersectWith(changed);
		m_Conflicts = m_Incoming.Count(i => changed.Contains(i.Substring(2)));
		if (result.code != 0) {
			m_Message = result.messages;
		}
		Repaint();
	}

	private string Status(string name) {
		return m_Status.TryGetValue(name, out var value) ? value : "";
	}

	// Starts an action in the background; Unity's Background Tasks show how far it is and can cancel it
	private void Do(string command, IEnumerable<string> arguments, string title) {
		var started = DLF.RunInBackground(command, arguments, result => {
			AssetDatabase.Refresh();
			DLFOverlay.Refresh();
			if (!this) {
				return;
			}
			m_Message = result.messages.Length > 0 ? result.messages : result.code == 0 ? "Done." : "Stopped.";
			Refresh();
		}, title);
		m_Message = started ? $"{title}... Background Tasks, at the bottom right of the Editor, shows how far it is and can cancel it." : "DLF is still busy with something else. Try again when it is done.";
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
		if (m_Status.Count == 0 && m_Checking) {
			GUILayout.Label("Checking...", EditorStyles.wordWrappedLabel);
		}
		else if (m_Changed.Count == 0 && m_Incoming.Count == 0) {
			GUILayout.Label(Status("files").Length == 0 ? "Nothing is shared yet. Set the patterns in Settings; on a machine with a write key, Upload then starts the list." : $"Nothing changed and nothing to download: the {Status("files")} shared files are the ones DLF.txt lists.", EditorStyles.wordWrappedLabel);
		}
		if (m_Changed.Count > 0) {
			GUILayout.Label($"Changed or missing on this machine ({m_Changed.Count})", EditorStyles.boldLabel);
			GUILayout.Label(canUpload ? "Upload shares them. Reset throws them away and puts back the files DLF.txt lists: all, or the ticked ones." : "Not shared with anyone. Reset throws them away and puts back the files DLF.txt lists: all, or the ticked ones.", EditorStyles.miniLabel);
			Rows(m_Changed, true);
		}
		if (m_Incoming.Count > 0) {
			GUILayout.Label($"To download ({m_Incoming.Count})", EditorStyles.boldLabel);
			GUILayout.Label(m_Conflicts == 0 ? "DLF.txt lists these differently from what this machine last took. Files changed here are not touched." : $"{m_Conflicts} of these also changed here, and Download leaves them alone. Upload shares yours; tick them and reset to take the listed ones.", EditorStyles.miniLabel);
			Rows(m_Incoming, false);
		}
		EditorGUILayout.EndScrollView();

		if (m_Message.Length > 0) {
			EditorGUILayout.HelpBox(m_Message, MessageType.None);
		}
		// Three rows of buttons, every button of a row the same width
		var half = GUILayout.Width((position.width - 14) / 2);
		using (new EditorGUILayout.HorizontalScope()) {
			using (new EditorGUI.DisabledScope(DLF.busy || m_Incoming.Count == 0)) {
				if (GUILayout.Button(new GUIContent("Download", "Takes what is new in DLF.txt. Files changed on this machine are left alone."), half, GUILayout.Height(26))) {
					Do("download", null, "Downloading the shared large files");
				}
			}
			using (new EditorGUI.DisabledScope(DLF.busy || !canUpload || (m_Changed.Count == 0 && Status("uploaded") == "yes"))) {
				if (GUILayout.Button(new GUIContent("Upload", "Lists the shared files as they are now in DLF.txt and puts them in the bucket. Commit DLF.txt afterwards."), half, GUILayout.Height(26))) {
					Do("upload", null, "Uploading the shared large files");
				}
			}
		}
		using (new EditorGUILayout.HorizontalScope()) {
			using (new EditorGUI.DisabledScope(DLF.busy || m_Changed.Count == 0)) {
				if (GUILayout.Button(new GUIContent("Reset All", "Throws the changes made on this machine away: every shared file becomes the one DLF.txt lists."), half) &&
					EditorUtility.DisplayDialog("DLF", $"Replace the {m_Changed.Count} files changed or deleted on this machine with the ones DLF.txt lists? The changes are lost.", "Reset", "Cancel")) {
					Do("reset", null, "Resetting the shared large files");
				}
			}
			using (new EditorGUI.DisabledScope(DLF.busy || m_Picked.Count == 0)) {
				if (GUILayout.Button(new GUIContent($"Reset Ticked ({m_Picked.Count})", "The same for the ticked files only."), half) &&
					EditorUtility.DisplayDialog("DLF", $"Replace the {m_Picked.Count} ticked files with the ones DLF.txt lists? The changes made to them here are lost.", "Reset", "Cancel")) {
					Do("reset", m_Picked.ToList(), "Resetting the ticked large files");
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
				Do("unlock", null, "Unlocking the bucket");
			}
		}
	}

	private static void Row(string icon, string text) {
		using (new EditorGUILayout.HorizontalScope()) {
			GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20), GUILayout.Height(18));
			GUILayout.Label(text, EditorStyles.wordWrappedLabel);
		}
	}

	// A list of files inside the scroll view. It takes the room of all its rows, and only the rows in view are drawn
	private void Rows(List<string> changes, bool pickable) {
		var area = GUILayoutUtility.GetRect(0, 100000, changes.Count * k_RowHeight, changes.Count * k_RowHeight);
		var first = Mathf.Max(0, (int)((m_Scroll.y - area.y) / k_RowHeight));
		var last = Mathf.Min(changes.Count - 1, (int)((m_Scroll.y + position.height - area.y) / k_RowHeight));
		for (var i = first; i <= last; i++) {
			FileRow(new Rect(area.x, area.y + i * k_RowHeight, area.width, k_RowHeight), changes[i], pickable);
		}
	}

	// One file: a tick box where it can be reset, a coloured letter (Added, Modified, Deleted), its name and its folder
	private void FileRow(Rect row, string change, bool pickable) {
		var path = change.Substring(2);
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