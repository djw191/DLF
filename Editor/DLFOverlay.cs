using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A small status line in the corner of the Scene view naming the next step, if there is one: files to download, a newer
/// DLF.txt to pull, files to upload, DLF.txt to commit or push. A click opens the DLF window. It asks the scripts in the
/// background, without the bucket, when the Editor comes to the front and every half minute.
/// </summary>
[InitializeOnLoad]
internal static class DLFOverlay {
	private const float k_Height = 16;
	private const float k_Padding = 4;
	private const double k_Interval = 30;

	// Snail's GitTools draws a git status line in the same corner: this one goes under it
	private static readonly bool s_BelowGitStatus = Type.GetType("Snail.GitTools.GitStatusOverlay, Snail.GitTools") != null;
	private static string s_Message = "";
	// What the last status check printed: the DLF window shows it at once when it opens
	internal static string lastStatus { get; private set; } = "";
	private static bool s_Running;
	private static double s_Next;

	static DLFOverlay() {
		SceneView.duringSceneGui += OnSceneGUI;
		EditorApplication.update += Tick;
		EditorApplication.focusChanged += focused => {
			if (focused) {
				Refresh();
			}
		};
	}

	// Asks again at the next chance: after something changed the files
	internal static void Refresh() {
		s_Next = 0;
	}

	private static void Tick() {
		if (s_Running || EditorApplication.timeSinceStartup < s_Next) {
			return;
		}
		s_Next = EditorApplication.timeSinceStartup + k_Interval;
		// Nothing to show in a project that does not use DLF
		if (!File.Exists(DLF.At(DLF.k_ConfigName))) {
			s_Message = "";
			return;
		}
		s_Running = true;
		DLF.RunInBackground("status", new[] { "local" }, result => {
			s_Running = false;
			if (result.code == 0) {
				lastStatus = result.output;
			}
			Show(result.output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList());
			SceneView.RepaintAll();
		});
	}

	private static void Show(List<string> lines) {
		if (lines.Count == 0) {
			s_Message = "status unknown (git could not be run)";
			return;
		}
		var changed = lines.Count(l => l.StartsWith("changed="));
		var missing = lines.Count(l => l.StartsWith("changed=D "));
		var incoming = lines.Count(l => l.StartsWith("incoming="));
		// The most pressing step first: what to get, then what to share. Nothing to do, nothing shown
		s_Message = incoming > 0 ? $"{incoming} large files to download"
			: lines.Contains("behind=yes") ? "new large files to pull"
			: missing == changed && missing > 0 ? $"{missing} large files missing"
			: changed > 0 ? lines.Contains("key=write") ? $"{changed} large files to upload" : $"{changed} large files changed here"
			: lines.Contains("committed=no") ? "commit and push DLF.txt"
			: lines.Contains("pushed=no") ? "push DLF.txt"
			: "";
	}

	// Unity saw files added, deleted, changed or moved: the status line and the DLF window look again at once
	private sealed class Watcher : AssetPostprocessor {
		private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom) {
			Refresh();
			DLFWindow.RefreshAll();
		}
	}

	private static void OnSceneGUI(SceneView view) {
		if (s_Message.Length == 0) {
			return;
		}
		Handles.BeginGUI();
		var text = new GUIContent($"DLF  {s_Message}", "Open the DLF window");
		var size = EditorStyles.label.CalcSize(text);
		var top = s_BelowGitStatus ? k_Padding * 4 + k_Height : k_Padding;
		var box = new Rect(k_Padding, top, k_Padding + k_Height + k_Padding + size.x + k_Padding, k_Height + k_Padding * 2);
		EditorGUI.LabelField(box, GUIContent.none, EditorStyles.textField);
		GUI.DrawTexture(new Rect(box.x + k_Padding, box.y + k_Padding, k_Height, k_Height), EditorGUIUtility.IconContent("console.warnicon.sml").image);
		GUI.Label(new Rect(box.x + k_Padding * 2 + k_Height, box.y + (box.height - size.y) / 2, size.x, size.y), text);
		if (GUI.Button(box, GUIContent.none, GUIStyle.none)) {
			DLFWindow.Open();
		}
		Handles.EndGUI();
	}
}