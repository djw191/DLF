using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
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
	private const float k_BarHeight = 20;
	private const float k_HeaderHeight = 20;
	// The icon of each status: Unity's own version control icons, one for each
	private static readonly Dictionary<string, string> k_Icons = new() {
		["Added locally"] = "P4_AddedLocal", ["Changed locally"] = "CollabEdit Icon", ["Missing locally"] = "P4_DeletedLocal",
		["Changed locally and on server"] = "CollabConflict Icon", ["Added on server"] = "P4_AddedRemote",
		["Changed on server"] = "Download-Available", ["Removed on server"] = "P4_DeletedRemote", ["Up to date"] = "GreenCheckmark"
	};

	private const string k_UpToDateStatus = "Up to date";
	private Dictionary<string, string> m_Status = new();
	// "A path", "M path" or "D path": changed on this machine, and what a download would bring
	private List<string> m_Changed = new(), m_Incoming = new();
	// Which kinds of files the box shows, any of them (k_Changed, k_New, k_UpToDate), whether .meta files have rows of their
	// own (k_Meta: otherwise a file's row stands for its .meta too); and only those whose path holds the
	// search text
	private const int k_Changed = 1, k_New = 2, k_UpToDate = 4, k_Meta = 8, k_AllKinds = 15, k_DefaultKinds = k_Changed | k_New;
	private int m_Filter = k_DefaultKinds;
	// The filter and the sort order are kept in this user's Unity preferences: closing the window or Unity keeps them
	private const string k_FilterKey = "DLF.Window.Kinds", k_SortKey = "DLF.Window.SortColumn", k_AscendingKey = "DLF.Window.SortAscending";
	private string m_Search = "";
	private SearchField m_SearchField;
	// Every shared file as one row: a change here, something new on the server, or up to date
	private sealed class Entry {
		public string path, name, folder, status;
		// A change on this computer: it can be ticked for Upload and Reset
		public bool local;
		// The order of its status, for sorting
		public int rank;
		// -1 when the file is not on this computer
		public long size;
	}
	private List<Entry> m_Entries = new();
	// The same with each .meta folded into its file's row
	private List<Entry> m_Folded = new();
	// The paths changed on this computer
	private HashSet<string> m_ChangedPaths = new();
	// The selected rows, by path, and the row a Shift click selects from
	private readonly HashSet<string> m_Selected = new();
	private string m_Anchor;
	// The columns: tick, status (an icon, untitled), name, directory, size. Their widths are the window's to set, not the
	// user's: Name and Directory share what the others leave. A click on a title sorts by that column, again the other way
	private static readonly string[] k_Titles = { "", "", "Name", "Directory", "Size" };
	private static readonly float[] k_Widths = { 24, 26, 0, 0, 62 };
	private readonly float[] m_Widths = { 24, 26, 210, 130, 62 };
	private float m_ListWidth;
	// Which columns show, one bit each (a right click on the titles picks them); the tick boxes and the name always do.
	// Kept in this user's preferences, like the filter and the sort
	private const int k_AllColumns = 31;
	private int m_Columns = k_AllColumns;
	private const string k_ColumnsKey = "DLF.Window.Columns";
	private int m_SortColumn = k_StatusColumn;
	private bool m_SortAscending = true;
	private const int k_TickColumn = 0, k_StatusColumn = 1, k_NameColumn = 2, k_FolderColumn = 3, k_SizeColumn = 4;
	// The ticked changes, and every change seen so far: one that shows up for the first time is ticked
	private readonly HashSet<string> m_Picked = new();
	private Dictionary<string, char> m_Known = new();
	// How many files are in both lists: changed here and listed differently by DLF.txt
	private int m_Conflicts;
	// While the status is being asked for. Not kept across a script reload: the run it waits for does not survive one
	[System.NonSerialized] private bool m_Checking;
	// A refresh was asked for while one ran, and whether it wanted the server too
	[System.NonSerialized] private bool m_Again, m_AgainServer;
	// The two halves of the last status: this computer's (quick) and the server's (slower, kept until a newer one comes)
	[System.NonSerialized] private string m_LocalStatus = "", m_ServerStatus = "";
	private double m_LastSpin;
	private string m_Message = "";
	private Vector2 m_Scroll;
	// Where the file box is: the buttons below take its edges
	private Rect m_Box;
	private static GUIStyle s_ColumnTitle, s_TitleRight, s_Folder, s_Title, s_RowText, s_Size, s_Box, s_Bar, s_Dropdown;

	[MenuItem("Tools/DLF")]
	internal static void Open() {
		var window = GetWindow<DLFWindow>("DLF");
		window.minSize = new Vector2(400, 420);
		window.Refresh();
	}

	// Every open DLF window asks again, after files were added, deleted or changed in the project: this computer only, as
	// an import says nothing about the server
	internal static void RefreshAll() {
		foreach (var window in Resources.FindObjectsOfTypeAll<DLFWindow>()) {
			window.Refresh(false);
		}
	}

	private void OnFocus() {
		Refresh();
	}

	private void OnEnable() {
		m_Filter = EditorPrefs.GetInt(k_FilterKey, k_DefaultKinds);
		m_SortColumn = Mathf.Clamp(EditorPrefs.GetInt(k_SortKey, k_StatusColumn), k_StatusColumn, k_SizeColumn);
		m_SortAscending = EditorPrefs.GetBool(k_AscendingKey, true);
		m_Columns = EditorPrefs.GetInt(k_ColumnsKey, k_AllColumns);
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
	// A refresh asked for while one runs is not dropped: it runs as soon as that one is done, as files may have changed since
	private void Refresh(bool server = true) {
		if (m_Checking) {
			m_Again = true;
			m_AgainServer |= server;
			return;
		}
		m_Checking = true;
		m_Again = m_AgainServer = false;
		// This computer first, which shows the files at once; then what the server says
		DLF.RunInBackground("status", new[] { "local" }, local => {
			if (!this) {
				return;
			}
			m_LocalStatus = local.output;
			Show(m_LocalStatus + m_ServerStatus);
			if (!server) {
				m_Checking = false;
				if (local.code != 0) {
					m_Message = local.messages;
				}
				if (m_Again) {
					Refresh(m_AgainServer);
				}
				return;
			}
			DLF.RunInBackground("status", new[] { "server" }, server => {
				if (!this) {
					return;
				}
				m_Checking = false;
				m_ServerStatus = server.output;
				if (local.code != 0 || server.code != 0) {
					m_Message = local.code != 0 ? local.messages : server.messages;
				}
				Show(m_LocalStatus + m_ServerStatus);
				if (m_Again) {
					Refresh(m_AgainServer);
				}
			});
		});
	}

	private void Show(string output) {
		var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains('=')).Select(l => (name: l.Substring(0, l.IndexOf('=')), value: l.Substring(l.IndexOf('=') + 1))).ToList();
		m_Changed = lines.Where(l => l.name == "changed").Select(l => l.value).ToList();
		m_Incoming = lines.Where(l => l.name == "incoming").Select(l => l.value).ToList();
		m_Status = lines.Where(l => l.name != "changed" && l.name != "incoming").GroupBy(l => l.name).ToDictionary(g => g.Key, g => g.Last().value);
		var changed = new HashSet<string>(m_Changed.Select(c => c.Substring(2)));
		m_ChangedPaths = changed;
		// A change that shows up is ticked, but never a deleted file: uploading that would delete it for the whole team.
		// A ticked change whose file is then deleted loses its tick too
		foreach (var change in m_Changed) {
			var path = change.Substring(2);
			var before = m_Known.TryGetValue(path, out var kind) ? kind : ' ';
			if (change[0] == 'D' && before != 'D') {
				m_Picked.Remove(path);
			}
			else if (change[0] != 'D' && before == ' ') {
				m_Picked.Add(path);
			}
		}
		m_Picked.IntersectWith(changed);
		// A file and its .meta are ticked together, or neither: a pair the rules above split is unticked
		foreach (var path in changed) {
			var pair = Pair(path);
			if (pair.Any(m_Picked.Contains) && !pair.All(m_Picked.Contains)) {
				m_Picked.ExceptWith(pair);
			}
		}
		m_Known = m_Changed.ToDictionary(c => c.Substring(2), c => c[0]);
		m_Conflicts = m_Incoming.Count(i => changed.Contains(i.Substring(2)));
		var list = DLF.At("DLF.txt");
		var busy = new HashSet<string>(changed.Concat(m_Incoming.Select(i => i.Substring(2))));
		// A line of DLF.txt is a 64 character hash, a space and the path
		var upToDate = File.Exists(list) ? File.ReadAllLines(list).Where(l => l.Length > 65).Select(l => l.Substring(65).TrimEnd('\r')).Where(path => !busy.Contains(path)).Distinct() : Enumerable.Empty<string>();
		var incoming = m_Incoming.GroupBy(i => i.Substring(2)).ToDictionary(g => g.Key, g => g.First()[0]);
		m_Entries = m_Changed.Select(c => {
			var both = incoming.ContainsKey(c.Substring(2));
			return MakeEntry(c.Substring(2), true, both ? "Changed locally and on server" : c[0] == 'A' ? "Added locally" : c[0] == 'D' ? "Missing locally" : "Changed locally", both ? 0 : c[0] == 'D' ? 1 : c[0] == 'M' ? 2 : 3);
		}).Concat(m_Incoming.Where(i => !changed.Contains(i.Substring(2))).Select(i => MakeEntry(i.Substring(2), false, i[0] == 'D' ? "Removed on server" : i[0] == 'A' ? "Added on server" : "Changed on server", 4)))
			.Concat(upToDate.Select(path => MakeEntry(path, false, k_UpToDateStatus, 5))).ToList();
		Fold();
		Repaint();
	}

	// With .meta files hidden, a file's row also stands for its .meta: it shows the .meta's change when the file has none
	private void Fold() {
		var byPath = new Dictionary<string, Entry>();
		foreach (var entry in m_Entries) {
			byPath.TryAdd(entry.path, entry);
		}
		m_Folded = m_Entries.Where(e => !e.path.EndsWith(".meta") || !byPath.ContainsKey(Main(e.path))).Select(e => {
			if (!byPath.TryGetValue(e.path + ".meta", out var meta) || meta.rank >= e.rank) {
				return e;
			}
			return new Entry { path = e.path, name = e.name, folder = e.folder, status = meta.status, local = meta.local || e.local, rank = meta.rank, size = e.size };
		}).ToList();
	}

	// The file a .meta belongs to, or the path itself
	private static string Main(string path) {
		return path.EndsWith(".meta") ? path.Substring(0, path.Length - 5) : path;
	}

	// A file and its .meta, those of the two changed on this computer: ticking one always ticks the other
	private List<string> Pair(string path) {
		return new[] { Main(path), Main(path) + ".meta" }.Where(m_ChangedPaths.Contains).ToList();
	}

	private void Tick(IEnumerable<string> paths, bool on) {
		var all = paths.SelectMany(Pair).ToList();
		if (on) {
			m_Picked.UnionWith(all);
		}
		else {
			m_Picked.ExceptWith(all);
		}
	}

	// Whether a row's changes are all ticked, its .meta's included
	private bool Ticked(string path) {
		var pair = Pair(path);
		return pair.Count > 0 && pair.All(m_Picked.Contains);
	}

	private static Entry MakeEntry(string path, bool local, string status, int rank) {
		var file = new FileInfo(DLF.At(path));
		return new Entry {
			path = path, name = Path.GetFileName(path), folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "", status = status,
			local = local, rank = rank, size = file.Exists ? file.Length : -1
		};
	}

	private string Status(string name) {
		return m_Status.TryGetValue(name, out var value) ? value : "";
	}

	// Starts an action in the background; Unity's Background Tasks show how far it is and can cancel it
	private void Do(string command, string[] arguments, string title) {
		// A file of picked paths is the command's to read, then gone
		var picks = arguments != null && arguments.Length == 2 && arguments[0] == "--paths-from" ? arguments[1] : null;
		var started = DLF.RunInBackground(command, arguments, result => {
			if (picks != null) {
				File.Delete(picks);
			}
			AssetDatabase.Refresh();
			DLFOverlay.Refresh();
			if (!this) {
				return;
			}
			m_Message = result.messages.Length > 0 ? result.messages : result.code == 0 ? "Done." : "Stopped.";
			Refresh();
		}, title);
		if (!started && picks != null) {
			File.Delete(picks);
		}
		m_Message = started ? "" : "DLF is still busy with another download or upload. Try again when it is done.";
	}

	// The ticked files for Upload and Reset, always named one by one: only what was shown and ticked is acted on, never a
	// change the window has not seen yet. They go in a file, as a command line has a length limit
	private string[] Picked() {
		var file = Path.Combine(Path.GetTempPath(), $"dlf-picks-{System.Guid.NewGuid():N}.txt");
		File.WriteAllText(file, string.Concat(m_Picked.Select(p => p + "\n")));
		return new[] { "--paths-from", file.Replace('\\', '/') };
	}

	// "name@machine is uploading branch", as a person would say it
	private static string Uploader(string lockStatus) {
		var owner = lockStatus.Split(new[] { " is uploading " }, 2, System.StringSplitOptions.None)[0];
		var at = owner.LastIndexOf('@');
		return at < 0 ? owner : $"{owner.Substring(0, at)} (on {owner.Substring(at + 1)})";
	}

	// A large throbber drawn as lines, sharp at any size (Unity's wait icon only comes at 16 pixels), in the same look: twelve
	// spokes around the middle, one bright and the ones behind it fading, stepping round as Unity's does
	private static void DrawSpinner(Vector2 center, float radius) {
		if (Event.current.type != EventType.Repaint) {
			return;
		}
		var head = (int)(EditorApplication.timeSinceStartup * 12) % 12;
		var color = EditorGUIUtility.isProSkin ? new Color(0.85f, 0.85f, 0.85f) : new Color(0.25f, 0.25f, 0.25f);
		for (var i = 0; i < 12; i++) {
			var behind = (head - i + 12) % 12;
			Handles.color = new Color(color.r, color.g, color.b, 1 - behind / 12f * 0.85f);
			var direction = new Vector2(Mathf.Sin(i * Mathf.PI / 6), -Mathf.Cos(i * Mathf.PI / 6));
			Handles.DrawAAPolyLine(radius * 0.22f, center + direction * radius * 0.5f, center + direction * radius);
		}
		Handles.color = Color.white;
	}

	// The frame of Unity's turning wait icon for now
	private static GUIContent Spinner() {
		return EditorGUIUtility.IconContent($"WaitSpin{(int)(EditorApplication.timeSinceStartup * 12) % 12:00}");
	}

	private static GUIContent Icon(string text, string icon, string tooltip) {
		return new GUIContent(text, EditorGUIUtility.IconContent(icon).image, tooltip);
	}

	private void OnGUI() {
		s_Folder ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.gray } };
		s_ColumnTitle ??= new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0) };
		s_TitleRight ??= new GUIStyle(s_ColumnTitle) { alignment = TextAnchor.MiddleRight };
		s_Size ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight, normal = { textColor = Color.gray } };
		// The file box sits right under the filter bar: no gap between them
		s_Box ??= new GUIStyle(EditorStyles.helpBox) { margin = new RectOffset(EditorStyles.helpBox.margin.left, EditorStyles.helpBox.margin.right, 0, EditorStyles.helpBox.margin.bottom) };
		// Unity's toolbar dropdown, free to take the search field's height
		s_Dropdown ??= new GUIStyle(EditorStyles.toolbarDropDown) { fixedHeight = 0 };
		s_Bar ??= new GUIStyle { margin = new RectOffset(EditorStyles.helpBox.margin.left, EditorStyles.helpBox.margin.right, 0, 0) };
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
		if (m_Conflicts > 0) {
			Row("console.warnicon.sml", $"{m_Conflicts} large files changed both on your computer and on the server. Upload keeps yours; Reset takes the server's.");
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
		// Which files to show, as a Unity window's toolbar along the top edge of the file box: a dropdown of the kinds, with
		// how many there are, and a search on the path
		var bar = GUILayoutUtility.GetRect(0, 100000, k_BarHeight, k_BarHeight, s_Bar);
		if (m_Box.width > 0) {
			bar = new Rect(m_Box.x, bar.y, m_Box.width, bar.height);
		}
		GUI.Box(bar, GUIContent.none, EditorStyles.toolbar);
		// The dropdown and the search field the same height (the search field's own) and in the middle of the bar
		var height = EditorStyles.toolbarSearchField.fixedHeight > 0 ? EditorStyles.toolbarSearchField.fixedHeight : bar.height - 4;
		var top = bar.y + (bar.height - height) / 2;
		// A fixed width, so that the search field keeps its room: the kinds that do not fit become "..."
		var dropdown = new Rect(bar.x + 2, top, 170, height);
		var filterName = new GUIContent(FilterName(dropdown.width - 22), FilterName(float.MaxValue));
		if (EditorGUI.DropdownButton(dropdown, filterName, FocusType.Passive, s_Dropdown)) {
			PopupWindow.Show(dropdown, new FilterPopup(this));
		}
		m_SearchField ??= new SearchField();
		m_Search = m_SearchField.OnToolbarGUI(new Rect(dropdown.xMax + 6, top, bar.xMax - dropdown.xMax - 10, height), m_Search);
		var shown = Sorted(((m_Filter & k_Meta) != 0 ? m_Entries : m_Folded).Where(e => (m_Filter & (e.local ? k_Changed : e.status == k_UpToDateStatus ? k_UpToDate : k_New)) != 0 && (m_Search.Length == 0 || e.path.IndexOf(m_Search, System.StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
		var box = GUILayoutUtility.GetRect(0, 100000, 80, 100000, s_Box);
		if (Event.current.type == EventType.Repaint) {
			m_Box = box;
		}
		GUI.Box(box, GUIContent.none, EditorStyles.helpBox);
		var headerRect = new Rect(box.x + 1, box.y + 1, box.width - 2, k_HeaderHeight);
		var list = new Rect(box.x + 1, headerRect.yMax, box.width - 2, box.yMax - headerRect.yMax - 1);
		var message = m_Status.Count == 0 ? "" : Status("files").Length == 0 && m_Changed.Count == 0 ? "No large files are shared yet. Pick which files to share in Settings, then Upload." : shown.Count == 0 ? "No files match the filter criteria." : "";
		var contentHeight = message.Length > 0 ? 40 : shown.Count * k_RowHeight;
		var width = list.width - (contentHeight > list.height ? GUI.skin.verticalScrollbar.fixedWidth : 0);
		// When the window's width changes, Name and Directory share the room (six to four), the other columns keep theirs
		// A hidden column takes no room; Name takes it all when Directory is hidden
		if (Mathf.Abs(width - m_ListWidth) > 0.5f) {
			m_ListWidth = width;
			for (var i = 0; i < k_Widths.Length; i++) {
				m_Widths[i] = Shown(i) ? k_Widths[i] : 0;
			}
			var room = Mathf.Max(80, width - m_Widths[k_TickColumn] - m_Widths[k_StatusColumn] - m_Widths[k_SizeColumn]);
			m_Widths[k_NameColumn] = Shown(k_FolderColumn) ? Mathf.Floor(room * 0.6f) : room;
			m_Widths[k_FolderColumn] = Shown(k_FolderColumn) ? room - m_Widths[k_NameColumn] : 0;
		}
		DrawHeader(new Rect(headerRect.x, headerRect.y, width, headerRect.height));
		// The tick box in the header ticks or unticks every change the list shows
		var tickable = shown.Where(e => e.local).Select(e => e.path).ToList();
		if (tickable.Count > 0) {
			var all = tickable.All(Ticked);
			if (GUI.Toggle(new Rect(headerRect.x + 4, headerRect.y + (headerRect.height - 16) / 2, 16, 16), all, GUIContent.none) != all) {
				Tick(tickable, !all);
			}
		}
		m_Scroll = GUI.BeginScrollView(list, m_Scroll, new Rect(0, 0, width, contentHeight));
		if (message.Length > 0) {
			GUI.Label(new Rect(4, 4, width - 8, contentHeight - 8), message, EditorStyles.wordWrappedLabel);
		}
		// Only the rows in view are drawn
		var first = Mathf.Max(0, (int)(m_Scroll.y / k_RowHeight));
		var last = Mathf.Min(shown.Count - 1, (int)((m_Scroll.y + list.height) / k_RowHeight));
		for (var i = first; i <= last; i++) {
			var line = new Rect(0, i * k_RowHeight, width, k_RowHeight);
			// A selected row in the selection colour; every other row a little lighter, so that a long row is easy to follow
			if (m_Selected.Contains(shown[i].path)) {
				EditorGUI.DrawRect(line, EditorGUIUtility.isProSkin ? new Color(0.17f, 0.36f, 0.53f) : new Color(0.24f, 0.48f, 0.9f, 0.6f));
			}
			else if (i % 2 == 1) {
				EditorGUI.DrawRect(line, new Color(1, 1, 1, 0.035f));
			}
			DrawRow(line, shown[i]);
			Click(line, i, shown);
		}
		GUI.EndScrollView();
		// Until the first status arrives, a large throbber in the middle of the empty file box
		if (m_Status.Count == 0 && m_Box.width > 0) {
			DrawSpinner(m_Box.center, 18);
		}

		if (m_Message.Length > 0) {
			EditorGUILayout.HelpBox(m_Message, MessageType.None);
		}
		// While a download, upload or reset runs: how far it is, the step it is at, and a way to stop it. Unity's Background
		// Tasks show the same run
		if (DLF.busy) {
			var progressRow = GUILayoutUtility.GetRect(0, 100000, 20, 20, EditorStyles.helpBox);
			if (m_Box.width > 0) {
				progressRow = new Rect(m_Box.x, progressRow.y, m_Box.width, progressRow.height);
			}
			var step = DLF.runStep.Length > 0 && DLF.runStep != "Starting" ? DLF.runStep : DLF.runTitle;
			EditorGUI.ProgressBar(new Rect(progressRow.x, progressRow.y, progressRow.width - 74, progressRow.height), DLF.runFraction, step);
			if (GUI.Button(new Rect(progressRow.xMax - 70, progressRow.y, 70, progressRow.height), "Cancel")) {
				DLF.CancelRun();
			}
		}
		// The actions, three buttons of the same width, lined up with the edges of the file box above
		var row = GUILayoutUtility.GetRect(0, 100000, k_ButtonHeight, k_ButtonHeight, EditorStyles.helpBox);
		if (m_Box.width > 0) {
			row = new Rect(m_Box.x, row.y, m_Box.width, row.height);
		}
		// Download is the everyday action: it fills the row. Upload and Reset are smaller, at its right
		const float small = 90, smallHeight = 22;
		var downloadCell = new Rect(row.x, row.y, row.width - 2 * (small + 4), row.height);
		Rect Small(int i) => new(downloadCell.xMax + 4 + i * (small + 4), row.y + (row.height - smallHeight) / 2, small, smallHeight);
		{
			using (new EditorGUI.DisabledScope(DLF.busy || (m_Incoming.Count == 0 && !m_Changed.Any(c => c[0] == 'D')))) {
				if (GUI.Button(downloadCell, Icon(" Download", "Download-Available", "Gets the team's newer large files, and any that are missing here. Files you changed yourself are left alone."))) {
					Do("download", new[] { "--missing" }, "Downloading large files");
				}
			}
			using (new EditorGUI.DisabledScope(DLF.busy || !canUpload || (m_Changed.Count > 0 ? m_Picked.Count == 0 : Status("uploaded") != "no"))) {
				if (GUI.Button(Small(0), Icon(" Upload", "Update-Available", "Shares the ticked files with the team. Commit and push DLF.txt afterwards."))) {
					Do("upload", Picked(), "Uploading large files");
				}
			}
			using (new EditorGUI.DisabledScope(DLF.busy || m_Picked.Count == 0)) {
				if (GUI.Button(Small(1), Icon(" Reset", "UndoHistory", "Throws away your changes to the ticked files and gets the team's version back.")) &&
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

	// What the filter dropdown says: all files, the kinds picked, or none
	private string FilterName(float width) {
		var kinds = new[] { (k_Changed, "Changed Locally"), (k_New, "Changed on Server"), (k_UpToDate, "Up to date"), (k_Meta, ".meta") }.Where(k => (m_Filter & k.Item1) != 0).Select(k => k.Item2).ToList();
		if (m_Filter == k_AllKinds) {
			return $"All files ({m_Entries.Count})";
		}
		if (kinds.Count == 0 || kinds[0] == ".meta") {
			return "No files";
		}
		// As many kinds as fit in the width, then "..." for the rest
		var shown = kinds.Count;
		while (shown > 1 && s_Dropdown.CalcSize(new GUIContent(string.Join(", ", kinds.Take(shown)) + (shown < kinds.Count ? ", ..." : ""))).x > width) {
			shown--;
		}
		return string.Join(", ", kinds.Take(shown)) + (shown < kinds.Count ? ", ..." : "");
	}

	// The filter's choices, any number of them ticked, under the dropdown. It stays open while the choices are ticked and
	// closes when the mouse has been out of it for a moment (or a click lands elsewhere): a slip over its edge is
	// forgiven. "All files" ticks every kind, or unticks them all when all are ticked
	private sealed class FilterPopup : PopupWindowContent {
		private const float k_Line = 20;
		// How long the mouse may be out of the popup before it closes, in seconds
		private const double k_Grace = 0.4;
		private readonly DLFWindow m_Owner;
		// When the mouse left it, or -1 while it is inside
		private double m_LeftAt = -1;

		public FilterPopup(DLFWindow owner) {
			m_Owner = owner;
		}

		public override Vector2 GetWindowSize() {
			return new Vector2(220, k_Line * 5 + 14);
		}

		public override void OnOpen() {
			editorWindow.wantsMouseEnterLeaveWindow = true;
			EditorApplication.update += CloseWhenLeft;
		}

		public override void OnClose() {
			EditorApplication.update -= CloseWhenLeft;
		}

		private void CloseWhenLeft() {
			if (m_LeftAt >= 0 && EditorApplication.timeSinceStartup - m_LeftAt > k_Grace && editorWindow) {
				m_LeftAt = -1;
				editorWindow.Close();
			}
		}

		public override void OnGUI(Rect rect) {
			if (Event.current.type == EventType.MouseLeaveWindow) {
				m_LeftAt = EditorApplication.timeSinceStartup;
				return;
			}
			if (Event.current.type == EventType.MouseEnterWindow || Event.current.type == EventType.MouseMove) {
				m_LeftAt = -1;
			}
			var filter = m_Owner.m_Filter;
			var all = filter == k_AllKinds;
			if (EditorGUI.ToggleLeft(new Rect(6, 4, rect.width - 12, 18), "All files", all) != all) {
				filter = all ? 0 : k_AllKinds;
			}
			EditorGUI.DrawRect(new Rect(4, 4 + k_Line + 2, rect.width - 8, 1), new Color(0.5f, 0.5f, 0.5f, 0.4f));
			var y = 4 + k_Line + 5;
			// The counts are of the rows each kind would show, with or without .meta rows
			var rows = (filter & k_Meta) != 0 ? m_Owner.m_Entries : m_Owner.m_Folded;
			foreach (var (kind, name) in new[] { (k_Changed, $"Changed Locally ({rows.Count(r => r.local)})"), (k_New, $"Changed on Server ({rows.Count(r => !r.local && r.status != k_UpToDateStatus)})"), (k_UpToDate, $"Up to date ({rows.Count(r => r.status == k_UpToDateStatus)})"), (k_Meta, ".meta files") }) {
				if (kind == k_Meta) {
					EditorGUI.DrawRect(new Rect(4, y + 2, rect.width - 8, 1), new Color(0.5f, 0.5f, 0.5f, 0.4f));
					y += 5;
				}
				var on = (filter & kind) != 0;
				if (EditorGUI.ToggleLeft(new Rect(6, y, rect.width - 12, 18), new GUIContent(name, kind == k_Meta ? "Show .meta files as rows of their own. Hidden, a file's row stands for its .meta too" : ""), on) != on) {
					filter ^= kind;
				}
				y += k_Line;
			}
			if (filter != m_Owner.m_Filter) {
				m_Owner.m_Filter = filter;
				EditorPrefs.SetInt(k_FilterKey, filter);
				m_Owner.Repaint();
			}
		}
	}

	// The titles of the columns. A click on one sorts by it, a second click the other way; an arrow marks the column
	private void DrawHeader(Rect rect) {
		EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.12f));
		EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), new Color(0, 0, 0, 0.3f));
		for (var i = 0; i < k_Titles.Length; i++) {
			var cell = Cell(i, rect);
			if (i == k_TickColumn || cell.width <= 0) {
				continue;
			}
			EditorGUI.DrawRect(new Rect(cell.x, cell.y + 3, 1, cell.height - 6), new Color(0.5f, 0.5f, 0.5f, 0.3f));
			var arrow = m_SortColumn == i ? EditorGUIUtility.IconContent(m_SortAscending ? "scrollup" : "scrolldown").image : null;
			if (i == k_StatusColumn) {
				if (arrow) {
					GUI.DrawTexture(new Rect(cell.center.x - 5, cell.center.y - 5, 10, 10), arrow, ScaleMode.ScaleToFit);
				}
			}
			else {
				GUI.Label(new Rect(cell.x + 4, cell.y, cell.width - 20, cell.height), k_Titles[i], i == k_SizeColumn ? s_TitleRight : s_ColumnTitle);
				if (arrow) {
					GUI.DrawTexture(new Rect(cell.xMax - 14, cell.center.y - 5, 10, 10), arrow, ScaleMode.ScaleToFit);
				}
			}
		}
		// A left click on a title sorts by it, a second the other way. A right click anywhere on the titles picks the columns
		// that show, and sorts nothing. A click on the tick box is the tick box's
		var e = Event.current;
		if (e.type != EventType.MouseDown || !rect.Contains(e.mousePosition)) {
			return;
		}
		if (e.button == 1) {
			ColumnMenu().ShowAsContext();
			e.Use();
			return;
		}
		for (var i = k_StatusColumn; i < k_Titles.Length; i++) {
			if (e.button == 0 && Cell(i, rect).width > 0 && Cell(i, rect).Contains(e.mousePosition)) {
				m_SortAscending = m_SortColumn != i || !m_SortAscending;
				m_SortColumn = i;
				EditorPrefs.SetInt(k_SortKey, m_SortColumn);
				EditorPrefs.SetBool(k_AscendingKey, m_SortAscending);
				e.Use();
			}
		}
	}

	private bool Shown(int column) {
		return column == k_TickColumn || column == k_NameColumn || (m_Columns & (1 << column)) != 0;
	}

	// The columns, each ticked when it shows. The name cannot be hidden: a row without it says nothing
	private GenericMenu ColumnMenu() {
		var menu = new GenericMenu();
		foreach (var (column, title) in new[] { (k_StatusColumn, "Status"), (k_NameColumn, "Name"), (k_FolderColumn, "Directory"), (k_SizeColumn, "Size") }) {
			if (column == k_NameColumn) {
				menu.AddDisabledItem(new GUIContent(title), true);
				continue;
			}
			menu.AddItem(new GUIContent(title), Shown(column), () => {
				m_Columns ^= 1 << column;
				EditorPrefs.SetInt(k_ColumnsKey, m_Columns);
				m_ListWidth = -1;
				Repaint();
			});
		}
		return menu;
	}

	// Where a column is in a row
	private Rect Cell(int column, Rect row) {
		var x = row.x;
		for (var i = 0; i < column; i++) {
			x += m_Widths[i];
		}
		return new Rect(x, row.y, m_Widths[column], row.height);
	}

	// The rows in the order of the column the header sorts by, the name breaking ties
	private IEnumerable<Entry> Sorted(IEnumerable<Entry> entries) {
		var column = m_SortColumn;
		var ascending = m_SortAscending;
		IOrderedEnumerable<Entry> By<T>(System.Func<Entry, T> key) => ascending ? entries.OrderBy(key) : entries.OrderByDescending(key);
		var sorted = column == k_FolderColumn ? By(e => e.folder) : column == k_StatusColumn ? By(e => e.rank) : column == k_SizeColumn ? By(e => e.size) : By(e => e.name);
		return sorted.ThenBy(e => e.name, System.StringComparer.OrdinalIgnoreCase).ThenBy(e => e.folder);
	}

	// A click on a row (outside its tick box) selects it: Ctrl adds or removes it, Shift selects from the last one clicked,
	// a double click shows it in the Project window.
	// A right click opens the menu for the selection, or for the row alone when it is not selected
	private void Click(Rect line, int index, List<Entry> shown) {
		var e = Event.current;
		if (e.type != EventType.MouseDown || !line.Contains(e.mousePosition) || Cell(k_TickColumn, line).Contains(e.mousePosition)) {
			return;
		}
		var path = shown[index].path;
		if (e.button == 1) {
			if (!m_Selected.Contains(path)) {
				m_Selected.Clear();
				m_Selected.Add(path);
				m_Anchor = path;
			}
			RowMenu(shown.Where(r => m_Selected.Contains(r.path)).ToList()).ShowAsContext();
		}
		else if (e.button == 0 && e.clickCount == 2) {
			// A double click shows the file in the Project window
			ShowInProject(new[] { shown[index] });
		}
		else if (e.button == 0) {
			var anchor = shown.FindIndex(r => r.path == m_Anchor);
			if (e.shift && anchor >= 0) {
				if (!(e.control || e.command)) {
					m_Selected.Clear();
				}
				for (var i = Mathf.Min(anchor, index); i <= Mathf.Max(anchor, index); i++) {
					m_Selected.Add(shown[i].path);
				}
			}
			else if (e.control || e.command) {
				if (!m_Selected.Remove(path)) {
					m_Selected.Add(path);
				}
				m_Anchor = path;
			}
			else {
				m_Selected.Clear();
				m_Selected.Add(path);
				m_Anchor = path;
			}
		}
		e.Use();
		Repaint();
	}

	// Selects the files in Unity's Project window, a .meta file standing for its asset
	private static void ShowInProject(IEnumerable<Entry> rows) {
		var assets = rows.Select(r => AssetDatabase.LoadMainAssetAtPath(Main(r.path))).Where(a => a).Distinct().ToArray();
		if (assets.Length == 0) {
			return;
		}
		EditorUtility.FocusProjectWindow();
		Selection.objects = assets;
		EditorGUIUtility.PingObject(assets[0]);
	}

	// What can be done with the selected files. A .meta file stands for its asset in the Project window, and a file and
	// its .meta are always deleted together: Unity would give a lone asset a new .meta, and with it a new ID
	private GenericMenu RowMenu(List<Entry> rows) {
		var menu = new GenericMenu();
		var here = rows.Where(r => r.size >= 0).ToList();
		string Asset(string path) => path.EndsWith(".meta") ? path.Substring(0, path.Length - 5) : path;
		if (here.Count > 0) {
			menu.AddItem(new GUIContent("Show in Project"), false, () => ShowInProject(here));
			menu.AddItem(new GUIContent("Show in Explorer"), false, () => EditorUtility.RevealInFinder(DLF.At(here[0].path)));
		}
		else {
			menu.AddDisabledItem(new GUIContent("Show in Project"));
			menu.AddDisabledItem(new GUIContent("Show in Explorer"));
		}
		menu.AddItem(new GUIContent(rows.Count == 1 ? "Copy Path" : "Copy Paths"), false, () => EditorGUIUtility.systemCopyBuffer = string.Join("\n", rows.Select(r => r.path)));
		var local = rows.Where(r => r.local).Select(r => r.path).ToList();
		if (local.Count > 0) {
			menu.AddSeparator("");
			menu.AddItem(new GUIContent("Tick"), local.All(Ticked), () => Tick(local, true));
			menu.AddItem(new GUIContent("Untick"), !local.Any(Ticked), () => Tick(local, false));
		}
		menu.AddSeparator("");
		var assetsHere = here.Select(r => Asset(r.path)).Distinct().ToList();
		if (assetsHere.Count == 0) {
			menu.AddDisabledItem(new GUIContent("Delete from This Computer"));
		}
		else {
			menu.AddItem(new GUIContent("Delete from This Computer"), false, () => {
				var changed = rows.Any(r => r.local && r.status != "Missing locally");
				if (!EditorUtility.DisplayDialog("DLF", $"Move {assetsHere.Count} large files (with their .meta files) to the Recycle Bin? Up-to-date ones come back with Download." + (changed ? " Changes you have not uploaded are lost from the project." : ""), "Delete", "Cancel")) {
					return;
				}
				foreach (var asset in assetsHere) {
					// Unity deletes the asset and its .meta; a file Unity does not know goes directly
					if (!AssetDatabase.MoveAssetToTrash(asset)) {
						foreach (var file in new[] { asset, asset + ".meta" }.Select(DLF.At).Where(File.Exists)) {
							File.Delete(file);
						}
					}
				}
				// The rows say so at once, unticked; the status check that follows confirms them
				var gone = new HashSet<string>(assetsHere.SelectMany(a => new[] { a, a + ".meta" }).Where(f => !File.Exists(DLF.At(f))));
				foreach (var entry in m_Entries.Where(e => gone.Contains(e.path))) {
					entry.status = "Missing locally";
					entry.local = true;
					entry.rank = 1;
					entry.size = -1;
					m_ChangedPaths.Add(entry.path);
					m_Known[entry.path] = 'D';
				}
				m_Picked.ExceptWith(gone);
				Fold();
				m_Selected.Clear();
				AssetDatabase.Refresh();
				Refresh();
			});
		}
		return menu;
	}

	// One file, in the header's columns: a tick box where it is a change here, then its mark (a coloured letter for a
	// change here), its status as an icon (its tooltip says which), its name, folder and size
	private void DrawRow(Rect row, Entry entry) {
		var tick = Cell(k_TickColumn, row);
		if (entry.local) {
			var ticked = Ticked(entry.path);
			if (GUI.Toggle(new Rect(tick.x + 4, tick.y + 1, 16, tick.height - 2), ticked, new GUIContent("", "Tick for Upload and Reset. A file and its .meta are ticked together")) != ticked) {
				Tick(new[] { entry.path }, !ticked);
			}
		}
		var cell = Cell(k_StatusColumn, row);
		if (cell.width > 0) {
			var icon = new Rect(cell.x + (cell.width - 16) / 2, cell.y + (cell.height - 16) / 2, 16, 16);
			GUI.DrawTexture(icon, EditorGUIUtility.IconContent(k_Icons[entry.status]).image, ScaleMode.ScaleToFit);
			GUI.Label(icon, new GUIContent("", entry.status));
		}
		GUI.Label(Cell(k_NameColumn, row), new GUIContent(entry.name, entry.path));
		GUI.Label(Cell(k_FolderColumn, row), new GUIContent(entry.folder, entry.folder), s_Folder);
		GUI.Label(Cell(k_SizeColumn, row), entry.size < 0 ? "" : EditorUtility.FormatBytes(entry.size), s_Size);
	}
}