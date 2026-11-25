using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

/// <summary>
/// A small status line in the corner of the Scene view: whether the shared files on this machine are the ones DLF.txt
/// lists, changed, or waiting to be downloaded. A click opens the DLF window. It asks the scripts in the background,
/// without the bucket, when the Editor comes to the front and every half minute.
/// </summary>
[InitializeOnLoad]
internal static class DLFOverlay {
	private const float k_Height = 16;
	private const float k_Padding = 4;
	private const double k_Interval = 30;

	private static string s_Message = "";
	private static bool s_Ok, s_Running, s_Repaint;
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
		if (s_Repaint) {
			s_Repaint = false;
			SceneView.RepaintAll();
		}
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
		var arguments = DLF.Arguments("status", new[] { "local" });
		Task.Run(() => {
			try {
				using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments) {
					RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
				});
				var lines = process.StandardOutput.ReadToEnd().Split('\n').Select(l => l.Trim()).ToList();
				process.WaitForExit();
				var changed = lines.Count(l => l.StartsWith("changed="));
				var incoming = lines.Count(l => l.StartsWith("incoming="));
				var canUpload = lines.Contains("key=write");
				// The most pressing step first: what to get, then what to share
				s_Message = incoming > 0 ? $"{incoming} large files to download"
					: lines.Contains("behind=yes") ? "a newer DLF.txt to pull"
					: changed > 0 ? canUpload ? $"{changed} large files to upload" : $"{changed} large files changed or missing here"
					: lines.Contains("committed=no") ? "DLF.txt to commit and push"
					: lines.Contains("pushed=no") ? "DLF.txt to push"
					: lines.Any(l => l.StartsWith("files=")) ? "large files: all good"
					: "no large files shared yet";
				s_Ok = !lines.Any(l => l.StartsWith("files=")) || s_Message == "large files: all good";
			}
			catch (Exception) {
				s_Ok = false;
				s_Message = "status unknown (git could not be run)";
			}
			finally {
				s_Running = false;
				s_Repaint = true;
			}
		});
	}

	private static void OnSceneGUI(SceneView view) {
		if (s_Message.Length == 0) {
			return;
		}
		Handles.BeginGUI();
		var text = new GUIContent($"DLF  {s_Message}", "Open the DLF window");
		var size = EditorStyles.label.CalcSize(text);
		// Under the git status line of Snail's GitTools when that package is in the project, else in its place
		var top = Type.GetType("Snail.GitTools.GitStatusOverlay, Snail.GitTools") != null ? k_Padding * 4 + k_Height : k_Padding;
		var box = new Rect(k_Padding, top, k_Padding + k_Height + k_Padding + size.x + k_Padding, k_Height + k_Padding * 2);
		EditorGUI.LabelField(box, GUIContent.none, EditorStyles.textField);
		GUI.DrawTexture(new Rect(box.x + k_Padding, box.y + k_Padding, k_Height, k_Height), EditorGUIUtility.IconContent(s_Ok ? "TestPassed" : "Warning").image);
		GUI.Label(new Rect(box.x + k_Padding * 2 + k_Height, box.y + (box.height - size.y) / 2, size.x, size.y), text);
		if (GUI.Button(box, GUIContent.none, GUIStyle.none)) {
			EditorApplication.ExecuteMenuItem("Tools/DLF/DLF");
		}
		Handles.EndGUI();
	}
}