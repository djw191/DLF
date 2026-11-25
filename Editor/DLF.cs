using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// The Unity side of DLF (Derek's Large Files), which keeps files that are too large for git in an S3 bucket.
/// All of DLF's logic is in the shell scripts of the Hooks~ folder: this class only runs them, in the background where it
/// can (Unity's Background Tasks show how far a run is and can cancel it). It downloads what DLF.txt lists on start, when
/// the Editor regains focus and before a build, and on every reload has the scripts put DLF's reminders in the git hooks
/// of a machine with a write key. Everything a person does goes through DLFWindow. See README.md.
/// </summary>
public sealed class DLF : IPreprocessBuildWithReport {
	internal const string k_ConfigName = "DLF.cfg";
	private const string k_PointerName = "DLF.txt";
	private const string k_StartedKey = "DLF.Started";
	private const string k_TriedKey = "DLF.Tried";
	private const string k_NoGit = "DLF: git could not be run, so the shared large files cannot be downloaded. Install Git (git-scm.com; the git built into GitHub Desktop or another client is not enough), make sure it is on the PATH, and restart Unity.";

	private static string s_Root, s_KeyPath;
	// What the background runs hand back to the main thread, the only one where Unity's API may be used
	private static readonly ConcurrentQueue<Action> s_MainThread = new();

	// A running command of the dlf script: its process, and what it printed so far
	private sealed class Job {
		public System.Diagnostics.Process process;
		public readonly StringBuilder output = new();
		public readonly StringBuilder messages = new();
		public volatile string progress = "";
	}

	// Where this machine's write key is kept, as the scripts decide it (it depends on the bucket)
	internal static string keyPath {
		get => s_KeyPath ??= Run("keypath").output.Trim();
		set => s_KeyPath = value;
	}

	// True while a download, upload, reset or unlock runs in the background: one at a time
	internal static bool busy { get; private set; }

	public int callbackOrder => 0;

	public void OnPreprocessBuild(BuildReport report) {
		// Without git nothing here can tell which files the build needs: better no build than one without them
		if (Git("--version").Length == 0) {
			throw new BuildFailedException(k_NoGit);
		}
		if (!File.Exists(At(k_PointerName))) {
			return;
		}
		var (code, _, messages) = Run("download");
		AssetDatabase.Refresh();
		if (code != 0) {
			throw new BuildFailedException(messages);
		}
	}

	[InitializeOnLoadMethod]
	private static void Start() {
		EditorApplication.update -= Pump;
		EditorApplication.update += Pump;
		// A pull happens outside the Editor: look again whenever it comes back to the front
		EditorApplication.focusChanged -= OnFocusChanged;
		EditorApplication.focusChanged += OnFocusChanged;
		var first = !SessionState.GetBool(k_StartedKey, false);
		SessionState.SetBool(k_StartedKey, true);
		if (first && Git("--version").Length == 0) {
			Debug.LogWarning(k_NoGit);
			return;
		}
		// On every reload, so that the copies of the hook scripts follow an update of the package; its notes once a session
		RunInBackground("install", null, result => {
			if (first && result.messages.Length > 0) {
				Debug.LogWarning(result.messages);
			}
		});
		if (first) {
			QueueFetch();
		}
	}

	private static void Pump() {
		while (s_MainThread.TryDequeue(out var action)) {
			try {
				action();
			}
			catch (Exception e) {
				Debug.LogException(e);
			}
		}
	}

	private static void OnFocusChanged(bool focused) {
		if (focused) {
			QueueFetch();
		}
	}

	// Once per list of files a commit has: one that cannot be fetched is not asked for again on every focus
	private static void QueueFetch() {
		var list = File.Exists(At(k_PointerName)) ? File.ReadAllText(At(k_PointerName)) : "";
		// The list can be long: its length and hash code stand for it
		var pointer = $"{list.Length}:{list.GetHashCode()}";
		if (Application.isBatchMode || list.Length == 0 || pointer == SessionState.GetString(k_TriedKey, "")) {
			return;
		}
		EditorApplication.delayCall += () => {
			if (EditorApplication.isPlayingOrWillChangePlaymode) {
				return;
			}
			// While another run goes, the next focus tries again
			if (RunInBackground("download", null, Report, "Downloading the shared large files")) {
				SessionState.SetString(k_TriedKey, pointer);
			}
		};
	}

	private static void Report((int code, string output, string messages) result) {
		AssetDatabase.Refresh();
		DLFOverlay.Refresh();
		if (result.code != 0) {
			Debug.LogWarning(result.messages.Length > 0 ? result.messages : "DLF: stopped.");
		}
		else if (result.messages.Length > 0) {
			Debug.Log(result.messages);
		}
	}

	// What git is started with to run a command of the dlf script
	internal static string Arguments(string command, IEnumerable<string> arguments = null) {
		var line = new StringBuilder($"-c \"alias.dlf=!sh \\\"{Scripts()}/dlf\\\"\" dlf {command}");
		foreach (var argument in arguments ?? Enumerable.Empty<string>()) {
			line.Append($" \"{argument.Replace("\"", "\\\"")}\"");
		}
		return line.ToString();
	}

	// Starts a command of the dlf script (Hooks~/dlf). Git runs the script with the shell it brings, from the top of the
	// repository: no shell has to be installed or found
	private static Job Launch(string line) {
		var job = new Job {
			process = new System.Diagnostics.Process {
				StartInfo = new System.Diagnostics.ProcessStartInfo("git", line) {
					RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
				}
			}
		};
		// The script prints "PROGRESS done total what" lines; everything else is its result
		job.process.OutputDataReceived += (_, e) => {
			if (e.Data == null) {
				return;
			}
			if (e.Data.StartsWith("PROGRESS ")) {
				job.progress = e.Data;
				return;
			}
			lock (job.output) {
				job.output.AppendLine(e.Data);
			}
		};
		job.process.ErrorDataReceived += (_, e) => {
			if (e.Data == null) {
				return;
			}
			lock (job.messages) {
				job.messages.AppendLine(e.Data);
			}
		};
		job.process.Start();
		job.process.BeginOutputReadLine();
		job.process.BeginErrorReadLine();
		return job;
	}

	// Waits for the end of a command, and takes what it printed
	private static (int code, string output, string messages) Finish(Job job) {
		job.process.WaitForExit();
		var code = job.process.ExitCode;
		job.process.Dispose();
		lock (job.output) {
			lock (job.messages) {
				return (code, job.output.ToString(), job.messages.ToString().Trim());
			}
		}
	}

	// A "PROGRESS done total what" line: how far, and at what
	private static (int done, int total, string text) Parse(string progress) {
		var parts = progress.Split(new[] { ' ' }, 4);
		return parts.Length == 4 && int.TryParse(parts[1], out var done) && int.TryParse(parts[2], out var total) ? (done, total, parts[3]) : (0, 0, "Working on the shared large files");
	}

	// Runs a command and waits for it, with a progress bar that can cancel it: for what has to be done before going on
	internal static (int code, string output, string messages) Run(string command, IEnumerable<string> arguments = null) {
		try {
			var job = Launch(Arguments(command, arguments));
			var shown = false;
			while (!job.process.WaitForExit(shown ? 100 : 1000)) {
				// Only something that takes a while gets a progress bar
				shown = true;
				var (done, total, text) = Parse(job.progress);
				if (EditorUtility.DisplayCancelableProgressBar("DLF", text, total > 0 ? (float)done / total : 0)) {
					Stop(job.process);
					return (1, "", "DLF: cancelled.");
				}
			}
			return Finish(job);
		}
		catch (Exception e) {
			return (1, "", $"{k_NoGit} ({e.Message})");
		}
		finally {
			EditorUtility.ClearProgressBar();
		}
	}

	// Runs a command in the background and hands its result to then, on the main thread. With a title it shows in Unity's
	// Background Tasks, where it can be cancelled, and it only starts when no other such run goes: false when one does
	internal static bool RunInBackground(string command, IEnumerable<string> arguments = null, Action<(int code, string output, string messages)> then = null, string title = null) {
		if (title != null && busy) {
			return false;
		}
		var line = Arguments(command, arguments);
		var id = -1;
		Job job = null;
		if (title != null) {
			busy = true;
			id = Progress.Start("DLF", title);
			Progress.RegisterCancelCallback(id, () => {
				var running = job;
				if (running != null) {
					Stop(running.process);
				}
				return true;
			});
		}
		Task.Run(() => {
			(int code, string output, string messages) result;
			try {
				job = Launch(line);
				var shown = "";
				while (!job.process.WaitForExit(200)) {
					var progress = job.progress;
					if (id < 0 || progress == shown) {
						continue;
					}
					shown = progress;
					var (done, total, text) = Parse(progress);
					s_MainThread.Enqueue(() => {
						if (!Progress.Exists(id)) {
							return;
						}
						if (total > 0) {
							Progress.Report(id, done, total, text);
						}
						else {
							Progress.SetDescription(id, text);
						}
					});
				}
				result = Finish(job);
			}
			catch (Exception e) {
				result = (1, "", $"{k_NoGit} ({e.Message})");
			}
			s_MainThread.Enqueue(() => {
				if (id >= 0) {
					busy = false;
					if (Progress.Exists(id) && Progress.GetStatus(id) == Progress.Status.Running) {
						Progress.Finish(id, result.code == 0 ? Progress.Status.Succeeded : Progress.Status.Failed);
					}
				}
				then?.Invoke(result);
			});
		});
		return true;
	}

	// Stops a run with everything it started (killing git alone leaves its shell going on), then lets go of what it held:
	// the busy mark, and the bucket's lock when it was uploading
	private static void Stop(System.Diagnostics.Process process) {
		try {
			if (!process.HasExited) {
				var kill = Application.platform == RuntimePlatform.WindowsEditor
					? new System.Diagnostics.ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}")
					: new System.Diagnostics.ProcessStartInfo("pkill", $"-TERM -P {process.Id}");
				kill.UseShellExecute = false;
				kill.CreateNoWindow = true;
				System.Diagnostics.Process.Start(kill)?.WaitForExit();
				if (!process.HasExited) {
					process.Kill();
				}
			}
		}
		catch (Exception) {
			// It ended on its own meanwhile
		}
		RunInBackground("release");
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
}