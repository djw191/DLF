using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The DLF window: shows, the way a git client shows changes, which shared files changed on this machine and which are
/// waiting to be downloaded, whether DLF.txt is committed and whether the bucket has it, and holds every action
/// (download, upload, reset, unlock). The ticked changes are what Upload shares and Reset throws away; a change is ticked
/// when it shows up. A download never replaces a file changed here: only a reset does. The scripts do the work, in the
/// background; this only asks them for the status and starts their commands.
/// </summary>
public sealed class DLFWindow : EditorWindow {
	private const float k_RowHeight = 18;
	private const float k_ButtonHeight = 26;
	private const float k_TopHeight = 22;
	// The colours a git client uses for added, modified and deleted
	private static readonly Dictionary<char, Color> k_Colors = new() {
		['A'] = new Color(0.25f, 0.73f, 0.31f), ['M'] = new Color(0.82f, 0.60f, 0.13f), ['D'] = new Color(0.97f, 0.32f, 0.29f)
	};

	private Dictionary<string, string> m_Status = new();
	// "A path", "M path" or "D path": changed on this machine, and what a download would bring
	private List<string> m_Changed = new(), m_Incoming = new();
	// The ticked changes, and every change seen so far: one that shows up for the first time is ticked
	private readonly HashSet<string> m_Picked = new();
	private HashSet<string> m_Known = new();
	// How many files are in both lists: changed here and listed differently by DLF.txt
	private int m_Conflicts;
	// While the status is being asked for
	private bool m_Checking;
	private double m_LastSpin;
	private string m_Message = "";
	private Vector2 m_Scroll;
	// Where the file box is: the buttons below take its edges
	private Rect m_Box;
	private static GUIStyle s_Badge, s_Folder, s_Title, s_RowText;

	[MenuItem("Tools/DLF")]
	internal static void Open() {
		var window = GetWindow<DLFWindow>("DLF");
		window.minSize = new Vector2(400, 420);
		window.Refresh();
	}

	// Every open DLF window asks again: after files were added, deleted or changed in the project
	internal static void RefreshAll() {
		foreach (var window in Resources.FindObjectsOfTypeAll<DLFWindow>()) {
			window.Refresh();
		}
	}

	private void OnFocus() {
		Refresh();
	}

	private void OnEnable() {
		// What the status line found last, at once; the full status follows
		if (DLFOverlay.lastStatus.Length > 0 && m_Status.Count == 0) {
			Show(DLFOverlay.lastStatus);
		}
		Refresh();
	}

	// Keeps the throbber turning while something runs
	private void Update() {
		if ((m_Checking || DLF.busy) && EditorApplication.timeSinceStartup - m_LastSpin > 0.08) {
			m_LastSpin = EditorApplication.timeSinceStartup;
			Repaint();
		}
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
			if (result.code != 0) {
				m_Message = result.messages;
			}
			Show(result.output);
		});
	}

	private void Show(string output) {
		var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('=')).Select(l => (name: l.Substring(0, l.IndexOf('=')), value: l.Substring(l.IndexOf('=') + 1))).ToList();
		m_Changed = lines.Where(l => l.name == "changed").Select(l => l.value).ToList();
		m_Incoming = lines.Where(l => l.name == "incoming").Select(l => l.value).ToList();
		m_Status = lines.Where(l => l.name != "changed" && l.name != "incoming").GroupBy(l => l.name).ToDictionary(g => g.Key, g => g.Last().value);
		var changed = new HashSet<string>(m_Changed.Select(c => c.Substring(2)));
		// A change that shows up is ticked, but not a deleted file: uploading that would delete it for the whole team
		m_Picked.UnionWith(m_Changed.Where(c => c[0] != 'D').Select(c => c.Substring(2)).Except(m_Known));
		m_Picked.IntersectWith(changed);
		m_Known = changed;
		m_Conflicts = m_Incoming.Count(i => changed.Contains(i.Substring(2)));
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
		m_Message = started ? $"{title}... The progress bar at the bottom right of the Editor shows each step and can cancel it." : "DLF is still busy with another download or upload. Try again when it is done.";
	}

	// The ticked files for a command: none when all are ticked, which means every change
	private List<string> Picked() {
		return m_Picked.Count == m_Changed.Count ? null : m_Picked.ToList();
	}

	// "name@machine is uploading branch", as a person would say it
	private static string Uploader(string lockStatus) {
		var owner = lockStatus.Split(new[] { " is uploading " }, 2, System.StringSplitOptions.None)[0];
		var at = owner.LastIndexOf('@');
		return at < 0 ? owner : $"{owner.Substring(0, at)} (on {owner.Substring(at + 1)})";
	}

	// The frame of Unity's turning wait icon for now
	private static GUIContent Spinner() {
		return EditorGUIUtility.IconContent($"WaitSpin{(int)(EditorApplication.timeSinceStartup * 12) % 12:00}");
	}

	private static GUIContent Icon(string text, string icon, string tooltip) {
		return new GUIContent(text, EditorGUIUtility.IconContent(icon).image, tooltip);
	}

	private void OnGUI() {
		s_Badge ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
		s_Folder ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } };
		s_Title ??= new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft, fontSize = 13 };
		// No margin above, and its first line as high as the icon square beside it, so that the two line up
		s_RowText ??= new GUIStyle(EditorStyles.wordWrappedLabel) { margin = new RectOffset(4, 4, 0, 0), padding = new RectOffset(2, 2, 1, 1), alignment = TextAnchor.UpperLeft };
		var branch = Status("branch").Length > 0 ? Status("branch") : "no branch";
		var canUpload = Status("key") == "write";

		// The top: project/branch the way git names it, a throbber while something runs, and the window's own buttons,
		// all on one line of the same height
		GUILayout.Space(6);
		using (new EditorGUILayout.HorizontalScope()) {
			GUILayout.Space(6);
			// Empty until the status arrives, the line keeping its height
			GUILayout.Label(m_Status.Count == 0 ? "" : $"{Status("project")}/{branch}", s_Title, GUILayout.Height(k_TopHeight));
			GUILayout.FlexibleSpace();
			// While the file box shows its own large one, no second throbber up here
			if ((m_Checking || DLF.busy) && m_Status.Count > 0) {
				GUILayout.Label(Spinner(), GUILayout.Width(20), GUILayout.Height(k_TopHeight));
			}
			using (new EditorGUI.DisabledScope(m_Checking)) {
				if (GUILayout.Button(Icon("", "Refresh", "Check again"), GUILayout.Width(28), GUILayout.Height(k_TopHeight))) {
					m_Message = "";
					Refresh();
				}
			}
			if (GUILayout.Button(Icon("", "SettingsIcon", "Settings: which files are shared, the server and the keys"), GUILayout.Width(28), GUILayout.Height(k_TopHeight))) {
				DLFSettingsWindow.Open();
			}
			GUILayout.Space(2);
		}
		GUILayout.Space(4);
		if (Status("key") == "none") {
			Row("console.warnicon.sml", "DLF is not set up on this computer yet. Open Settings (the gear).");
		}
		// How the large files stand on the server
		if (Status("bucket") == "unreachable") {
			Row("console.erroricon.sml", "Can't reach the server. Check your internet connection.");
		}
		else if (Status("lock").Length > 0) {
			Row("console.warnicon.sml", $"{Uploader(Status("lock"))} is uploading large files right now. Downloads and uploads wait until they finish.");
		}
		else if (Status("uploaded") == "no") {
			Row("console.warnicon.sml", "Your large files don't match the server.");
		}
		// The list matches the server's: what is on this computer decides whether the files do
		else if (Status("uploaded") == "yes") {
			var missing = m_Changed.Count(c => c[0] == 'D');
			if (missing > 0) {
				Row("console.warnicon.sml", $"{missing} large files missing on your computer.");
			}
			else if (m_Incoming.Count > 0) {
				Row("console.warnicon.sml", "Server has newer large files.");
			}
			else if (m_Changed.Count > 0) {
				Row("console.warnicon.sml", canUpload ? "You have local changed large files. Upload to share." : "You have local changed large files. Reset will download the server's version.");
			}
			else {
				Row("GreenCheckmark", "Large files match the server.");
			}
		}
		// And in git, through DLF.txt, the list of large files each commit needs
		if (Status("behind") == "yes") {
			Row("console.warnicon.sml", "Someone pushed new large files. Pull to get them.");
		}
		if (Status("committed") == "no") {
			Row("console.warnicon.sml", Status("uploaded") == "no" ? "DLF.txt (the list of large files) changed, but isn't committed yet." : "Large files uploaded, but DLF is not synced with git yet: commit DLF.txt and push.");
		}
		else if (Status("pushed") == "no") {
			Row("console.warnicon.sml", "Large files uploaded and committed, but not pushed yet: push so the team gets them.");
		}
		else if (Status("files").Length > 0 && Status("behind") != "yes") {
			Row("GreenCheckmark", "DLF is synced with git.");
		}

		GUILayout.Space(4);
		m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, EditorStyles.helpBox);
		if (m_Status.Count > 0 && m_Changed.Count == 0 && m_Incoming.Count == 0) {
			GUILayout.Label(Status("files").Length == 0 ? "No large files are shared yet. Pick which files to share in Settings, then Upload." : $"All {Status("files")} large files are up to date.", EditorStyles.wordWrappedLabel);
		}
		if (m_Changed.Count > 0) {
			// The box above the list ticks or unticks all of it
			var header = EditorGUILayout.GetControlRect(false, k_RowHeight);
			var all = m_Picked.Count == m_Changed.Count;
			if (GUI.Toggle(new Rect(header.x, header.y, 16, header.height), all, GUIContent.none) != all) {
				m_Picked.Clear();
				if (!all) {
					m_Picked.UnionWith(m_Changed.Select(c => c.Substring(2)));
				}
			}
			GUI.Label(new Rect(header.x + 20, header.y, header.width - 20, header.height), $"Changed on your computer ({m_Picked.Count} of {m_Changed.Count} ticked)", EditorStyles.boldLabel);
			GUILayout.Label(canUpload ? "Upload shares the ticked files with the team. Reset throws away your changes to them and gets the team's version back." : "Only on your computer. Reset throws away your changes to the ticked files and gets the team's version back.", EditorStyles.wordWrappedMiniLabel);
			if (m_Changed.Any(c => c[0] == 'D')) {
				GUILayout.Label("Files marked D are missing here: Download gets them back. Tick them only to delete them for the whole team.", EditorStyles.wordWrappedMiniLabel);
			}
			Rows(m_Changed, true);
		}
		if (m_Incoming.Count > 0) {
			GUILayout.Label($"New from the team ({m_Incoming.Count})", EditorStyles.boldLabel);
			GUILayout.Label(m_Conflicts == 0 ? "Download gets these. Files you changed yourself are not touched." : $"You changed {m_Conflicts} of these too, so Download leaves yours alone. Upload to keep yours, or tick them and Reset to take the team's.", EditorStyles.wordWrappedMiniLabel);
			Rows(m_Incoming, false);
		}
		EditorGUILayout.EndScrollView();
		if (Event.current.type == EventType.Repaint) {
			m_Box = GUILayoutUtility.GetLastRect();
		}
		// Until the first status arrives, a large throbber in the middle of the empty file box
		if (m_Status.Count == 0 && m_Box.width > 0) {
			GUI.DrawTexture(new Rect(m_Box.center.x - 16, m_Box.center.y - 16, 32, 32), Spinner().image, ScaleMode.ScaleToFit);
		}

		if (m_Message.Length > 0) {
			EditorGUILayout.HelpBox(m_Message, MessageType.None);
		}
		// The actions, three buttons of the same width, lined up with the edges of the file box above
		var row = GUILayoutUtility.GetRect(0, 100000, k_ButtonHeight, k_ButtonHeight, EditorStyles.helpBox);
		if (m_Box.width > 0) {
			row = new Rect(m_Box.x, row.y, m_Box.width, row.height);
		}
		var width = (row.width - 8) / 3;
		Rect Cell(int i) => new(row.x + i * (width + 4), row.y, width, row.height);
		{
			using (new EditorGUI.DisabledScope(DLF.busy || (m_Incoming.Count == 0 && !m_Changed.Any(c => c[0] == 'D')))) {
				if (GUI.Button(Cell(0), Icon(" Download", "Download-Available", "Gets the team's newer large files, and any that are missing here. Files you changed yourself are left alone."))) {
					Do("download", new[] { "--missing" }, "Downloading large files");
				}
			}
			using (new EditorGUI.DisabledScope(DLF.busy || !canUpload || (m_Changed.Count > 0 ? m_Picked.Count == 0 : Status("uploaded") != "no"))) {
				if (GUI.Button(Cell(1), Icon(" Upload", "Update-Available", "Shares the ticked files with the team. Commit and push DLF.txt afterwards."))) {
					Do("upload", Picked(), "Uploading large files");
				}
			}
			using (new EditorGUI.DisabledScope(DLF.busy || m_Picked.Count == 0)) {
				if (GUI.Button(Cell(2), Icon(" Reset", "UndoHistory", "Throws away your changes to the ticked files and gets the team's version back.")) &&
					EditorUtility.DisplayDialog("DLF", $"Throw away your changes to the {m_Picked.Count} ticked files and get the team's version back? Your changes are lost.", "Reset", "Cancel")) {
					Do("reset", Picked(), "Resetting large files");
				}
			}
		}
		if (canUpload && Status("lock").Length > 0) {
			var unlock = GUILayoutUtility.GetRect(0, 100000, k_ButtonHeight, k_ButtonHeight, EditorStyles.helpBox);
			if (GUI.Button(m_Box.width > 0 ? new Rect(m_Box.x, unlock.y, m_Box.width, unlock.height) : unlock, Icon(" Unlock", "LockIcon-On", "Clears the lock of an upload that was cut off, so others can upload again."))) {
				Do("unlock", null, "Unlocking the server");
			}
		}
	}

	// A status line: an icon in a fixed square, so that every icon sits in the same place whatever its own size, and the text
	private static void Row(string icon, string text) {
		using (new EditorGUILayout.HorizontalScope()) {
			GUILayout.Space(4);
			var spot = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(18), GUILayout.Height(18));
			GUI.DrawTexture(new Rect(spot.x + 1, spot.y + 1, 16, 16), EditorGUIUtility.IconContent(icon).image, ScaleMode.ScaleToFit);
			GUILayout.Label(text, s_RowText, GUILayout.MinHeight(18));
		}
		GUILayout.Space(2);
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

	// One file: a tick box where it is one of the changes here, a coloured letter (Added, Modified, Deleted), its name and its folder
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