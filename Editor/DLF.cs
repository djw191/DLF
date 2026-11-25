using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// The Unity side of DLF (Derek's Large Files), which keeps files that are too large for git in an S3 bucket.
/// All of DLF's logic is in the shell scripts of the Hooks~ folder: this class only runs them. It fetches the set of
/// files the checked-out commit names (on start, when the Editor regains focus, before a build and from the menu),
/// resets files to the bucket's, and on a machine with a write key adds DLF's hooks to the repository's git hooks.
/// See README.md.
/// </summary>
public sealed class DLF : IPreprocessBuildWithReport {
	internal const string k_ConfigName = "DLF.cfg";
	private const string k_PointerName = "DLF.txt";
	private const string k_StartedKey = "DLF.Started";
	private const string k_TriedKey = "DLF.Tried";
	private const string k_HookStart = "# DLF begin: runs the DLF hook first (added by the DLF Unity package; delete down to \"DLF end\" to turn it off)";
	private const string k_HookEnd = "# DLF end";

	private static string s_Root, s_KeyPath;

	// Where this machine's write key is kept, as the scripts decide it (it depends on the bucket)
	internal static string keyPath {
		get => s_KeyPath ??= Run("keypath").output.Trim();
		set => s_KeyPath = value;
	}

	public int callbackOrder => 0;

	public void OnPreprocessBuild(BuildReport report) {
		if (!File.Exists(At(k_PointerName))) {
			return;
		}
		var (code, _, messages) = Run("fetch");
		AssetDatabase.Refresh();
		if (code != 0) {
			throw new BuildFailedException(messages);
		}
	}

	[InitializeOnLoadMethod]
	private static void Start() {
		// A pull happens outside the Editor: look again whenever it comes back to the front
		EditorApplication.focusChanged -= OnFocusChanged;
		EditorApplication.focusChanged += OnFocusChanged;
		if (!SessionState.GetBool(k_StartedKey, false)) {
			SessionState.SetBool(k_StartedKey, true);
			InstallHooks();
			QueueFetch();
		}
	}

	private static void OnFocusChanged(bool focused) {
		if (focused) {
			QueueFetch();
		}
	}

	// Once per set a commit names: a set that cannot be fetched is not asked for again on every focus
	private static void QueueFetch() {
		var pointer = File.Exists(At(k_PointerName)) ? File.ReadAllText(At(k_PointerName)).Trim() : "";
		if (Application.isBatchMode || pointer.Length == 0 || pointer == SessionState.GetString(k_TriedKey, "")) {
			return;
		}
		EditorApplication.delayCall += () => {
			if (EditorApplication.isPlayingOrWillChangePlaymode) {
				return;
			}
			SessionState.SetString(k_TriedKey, pointer);
			Report(Run("fetch"));
		};
	}

	[MenuItem("Tools/DLF/Fetch")]
	private static void Fetch() {
		Report(Run("fetch"));
	}

	// Uploads the set the checked-out commit names, as pushing that commit would, without pushing anything
	[MenuItem("Tools/DLF/Publish")]
	private static void Publish() {
		Report(Run("publish"));
	}

	[MenuItem("Tools/DLF/Reset All To Remote")]
	private static void ResetAll() {
		if (EditorUtility.DisplayDialog("DLF", "Replace all shared files on this machine with the ones in the bucket? Local changes to them are lost.", "Reset", "Cancel")) {
			Report(Run("reset"));
		}
	}

	// What is selected in the Project window, with its .meta files; a folder stands for all below it
	[MenuItem("Tools/DLF/Reset Selected To Remote")]
	private static void ResetSelected() {
		var root = At(".");
		var selection = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath).Select(p => Path.GetRelativePath(root, Path.GetFullPath(p)).Replace('\\', '/')).ToList();
		if (EditorUtility.DisplayDialog("DLF", "Replace the selected shared files on this machine with the ones in the bucket? Local changes to them are lost.", "Reset", "Cancel")) {
			Report(Run("reset", selection));
		}
	}

	[MenuItem("Tools/DLF/Reset Selected To Remote", true)]
	private static bool CanResetSelected() {
		return Selection.assetGUIDs.Length > 0;
	}

	private static void Report((int code, string output, string messages) result) {
		AssetDatabase.Refresh();
		if (result.code != 0) {
			Debug.LogWarning(result.messages.Length > 0 ? result.messages : "DLF: stopped.");
		}
		else if (result.messages.Length > 0) {
			Debug.Log(result.messages);
		}
	}

	// Runs a command of the dlf script (Hooks~/dlf) and waits for it, with a progress bar that can cancel it.
	// Git runs the script with the shell it brings, from the top of the repository: no shell has to be installed or found
	private static (int code, string output, string messages) Run(string command, IEnumerable<string> arguments = null) {
		var line = new StringBuilder($"-c \"alias.dlf=!sh \\\"{Scripts()}/dlf\\\"\" dlf {command}");
		foreach (var argument in arguments ?? Enumerable.Empty<string>()) {
			line.Append($" \"{argument.Replace("\"", "\\\"")}\"");
		}
		var output = new StringBuilder();
		var messages = new StringBuilder();
		var progress = "";
		try {
			using var process = new System.Diagnostics.Process {
				StartInfo = new System.Diagnostics.ProcessStartInfo("git", line.ToString()) {
					RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
				}
			};
			// The script prints "PROGRESS done total what" lines; everything else is its result
			process.OutputDataReceived += (_, e) => {
				if (e.Data != null && e.Data.StartsWith("PROGRESS ")) {
					progress = e.Data;
				}
				else if (e.Data != null) {
					lock (output) {
						output.AppendLine(e.Data);
					}
				}
			};
			process.ErrorDataReceived += (_, e) => {
				if (e.Data != null) {
					lock (messages) {
						messages.AppendLine(e.Data);
					}
				}
			};
			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			var shown = false;
			while (!process.WaitForExit(shown ? 100 : 1000)) {
				// Only something that takes a while gets a progress bar
				shown = true;
				var parts = progress.Split(new[] { ' ' }, 4);
				var fraction = parts.Length == 4 && float.TryParse(parts[1], out var done) && float.TryParse(parts[2], out var total) && total > 0 ? done / total : 0;
				if (EditorUtility.DisplayCancelableProgressBar("DLF", parts.Length == 4 ? parts[3] : "Working on the shared large files", fraction)) {
					process.Kill();
					return (1, "", "DLF: cancelled.");
				}
			}
			process.WaitForExit();
			return (process.ExitCode, output.ToString(), messages.ToString().Trim());
		}
		catch (Exception e) {
			return (1, "", $"DLF: could not run git ({e.Message}).");
		}
		finally {
			EditorUtility.ClearProgressBar();
		}
	}

	// The folder of the shell scripts. Asked for each time: a path fixed when the code was compiled goes stale when the
	// package moves (a new version in Unity's cache, a renamed project folder) without being compiled again
	private static string Scripts() {
		var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DLF).Assembly);
		// Installed as a package, or copied into Assets with the Hooks~ folder next to the Editor folder
		var folder = package != null ? package.resolvedPath : Path.GetDirectoryName(Path.GetDirectoryName(UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("DLF.Editor")));
		return Path.GetFullPath(Path.Combine(folder, "Hooks~")).Replace('\\', '/');
	}

	// The output of a git command, empty when git is missing or this is not a repository
	private static string Git(string arguments) {
		try {
			using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", arguments) {
				RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
			});
			var output = git.StandardOutput.ReadToEnd().Trim();
			git.WaitForExit();
			return git.ExitCode == 0 ? output : "";
		}
		catch (Exception) {
			return "";
		}
	}

	// DLF's paths are relative to the top folder of the repository, which need not be the Unity project folder
	internal static string At(string path) {
		s_Root ??= Git("rev-parse --show-toplevel");
		return Path.Combine(s_Root, path);
	}

	// The values of "name=value" lines; a name can repeat (pattern)
	internal static List<string> Values(string path, string name) {
		return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.StartsWith(name + "=")).Select(l => l.Substring(name.Length + 1).Trim()).Where(v => v.Length > 0).ToList() : new List<string>();
	}

	// The files the patterns match, as paths from the top of the repository. Git does the matching, exactly as the scripts do
	internal static List<string> Matches(IEnumerable<string> patterns) {
		var specs = string.Concat(patterns.Select(p => $" \":(top,glob){p.Replace("\"", "\\\"")}\""));
		return specs.Length == 0 ? new List<string>() : Git($"ls-files --others --full-name -z --{specs}").Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();
	}

	// On a machine with a write key, makes git run the DLF hooks without taking over the repository's hooks: other hooks
	// (Git LFS and the like) keep working. The scripts are copied into the git folder, a path that survives package updates
	// and is the same for every checkout of the repository, and a marked block in the normal pre-commit and pre-push hooks
	// calls them first. A machine that only fetches needs no hooks and gets none
	internal static void InstallHooks() {
		var common = Git("rev-parse --git-common-dir");
		if (common.Length == 0 || !File.Exists(keyPath)) {
			return;
		}
		common = Path.GetFullPath(common);
		Directory.CreateDirectory(Path.Combine(common, "dlf-hooks"));
		foreach (var script in Directory.GetFiles(Scripts())) {
			File.Copy(script, Path.Combine(common, "dlf-hooks", Path.GetFileName(script)), true);
		}
		if (Git("config core.hooksPath").Length > 0) {
			Debug.LogWarning("DLF: this repository keeps its git hooks in another folder (core.hooksPath), so DLF did not add itself. To publish from this machine, call .git/dlf-hooks/pre-commit and .git/dlf-hooks/pre-push from those hooks.");
			return;
		}
		Directory.CreateDirectory(Path.Combine(common, "hooks"));
		foreach (var hook in new[] { "pre-commit", "pre-push" }) {
			var path = Path.Combine(common, "hooks", hook);
			var text = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "#!/bin/sh\n";
			if (text.Contains(k_HookStart)) {
				continue;
			}
			if (!text.StartsWith("#!") || !text.Split('\n')[0].EndsWith("sh")) {
				Debug.LogWarning($"DLF: the git hook {path} is not a shell script, so DLF did not add itself to it. To publish from this machine, call .git/dlf-hooks/{hook} from it.");
				continue;
			}
			// pre-push gets the pushed refs on its input: they are read once and handed to both the DLF hook and the rest of the script
			var call = hook == "pre-push"
				? "dlf_refs=$(cat)\nprintf '%s\\n' \"$dlf_refs\" | sh \"$(git rev-parse --git-common-dir)/dlf-hooks/pre-push\" \"$@\" || exit 1\nexec <<DLF_REFS\n$dlf_refs\nDLF_REFS\n"
				: "sh \"$(git rev-parse --git-common-dir)/dlf-hooks/pre-commit\" \"$@\" || exit 1\n";
			var first = text.IndexOf('\n') < 0 ? text.Length : text.IndexOf('\n') + 1;
			File.WriteAllText(path, text.Substring(0, first).TrimEnd('\n') + $"\n{k_HookStart}\n{call}{k_HookEnd}\n" + text.Substring(first));
			if (Application.platform != RuntimePlatform.WindowsEditor) {
				System.Diagnostics.Process.Start("chmod", $"+x \"{path}\"")?.WaitForExit();
			}
		}
	}
}