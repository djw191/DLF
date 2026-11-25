using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// DLF (Derek's Large Files) keeps files that are too large for git in a private S3 bucket (Cloudflare R2) and fetches
/// them into the project. It works in any Unity project inside a git repository: add this package and a DLF.cfg.
/// DLF.cfg, at the top of the repository, says which files are shared and where. The bucket holds one set of them: each
/// file compressed and named by the hash of its content, and an index that lists them. A commit names the set it needs
/// by the hash of that index (DLF.txt), and the Editor makes the local files match it.
/// Publishing is done by the git hooks in this package's Hooks~ folder on a machine with a write key: the commit names
/// the new set, the push uploads it.
/// </summary>
public sealed class DLF : IPreprocessBuildWithReport {
	internal const string k_ConfigName = "DLF.cfg";
	private const string k_PointerName = "DLF.txt";
	private const string k_TempPath = "Temp/DLF.tmp";
	private const string k_StartedKey = "DLF.Started";
	private const string k_TriedKey = "DLF.Tried";
	private const string k_HookStart = "# DLF begin: runs the DLF hook first (added by the DLF Unity package; delete down to \"DLF end\" to turn it off)";
	private const string k_HookEnd = "# DLF end";

	private static readonly HttpClient s_Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
	private static string s_Root, s_State;

	// The write key is kept per machine and per bucket, outside the repository, so it can never be committed
	internal static string keyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DLF", $"{Config("bucket")}.cfg");

	public int callbackOrder => 0;

	public void OnPreprocessBuild(BuildReport report) {
		Fetch();
		var pointer = ReadText(At(k_PointerName));
		if (pointer.Length > 0 && pointer != ReadText(State("synced"))) {
			throw new BuildFailedException("DLF: the shared large files are not the ones this commit names. Run Tools/DLF/Fetch.");
		}
	}

	[InitializeOnLoadMethod]
	private static void Start() {
		// A pull happens outside the Editor: look again whenever it comes back to the front
		EditorApplication.focusChanged -= OnFocusChanged;
		EditorApplication.focusChanged += OnFocusChanged;
		if (!SessionState.GetBool(k_StartedKey, false)) {
			SessionState.SetBool(k_StartedKey, true);
			UseHooks();
			QueueFetch();
		}
	}

	private static void OnFocusChanged(bool focused) {
		if (focused) {
			QueueFetch();
		}
	}

	// Makes git run the DLF hooks without taking over the repository's hooks: other hooks (Git LFS and the like) keep working.
	// The scripts are copied into the git folder, a path that survives package updates and is the same for every checkout
	// of the repository, and a marked block in the normal pre-commit and pre-push hooks calls them first
	private static void UseHooks() {
		var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(DLF).Assembly);
		var common = Git("rev-parse --git-common-dir");
		if (package == null || common.Length == 0) {
			return;
		}
		common = Path.GetFullPath(common);
		Directory.CreateDirectory(Path.Combine(common, "dlf-hooks"));
		foreach (var script in Directory.GetFiles(Path.Combine(package.resolvedPath, "Hooks~"))) {
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

	// What this checkout last fetched or published; kept in the git folder, where the hooks read and write it too
	private static string State(string name) {
		s_State ??= Path.Combine(Path.GetFullPath(Git("rev-parse --git-dir") is { Length: > 0 } gitFolder ? gitFolder : "Library"), "dlf");
		return Path.Combine(s_State, name);
	}

	// The values of "name=value" lines; a name can repeat (pattern)
	internal static List<string> Values(string path, string name) {
		return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.StartsWith(name + "=")).Select(l => l.Substring(name.Length + 1).Trim()).Where(v => v.Length > 0).ToList() : new List<string>();
	}

	internal static string Config(string name) {
		return Values(At(k_ConfigName), name).FirstOrDefault() ?? "";
	}

	private static string ReadText(string path) {
		return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
	}

	// Once per index a commit names: a set that cannot be fetched is not asked for again on every focus
	private static void QueueFetch() {
		var pointer = ReadText(At(k_PointerName));
		if (Application.isBatchMode || pointer == SessionState.GetString(k_TriedKey, "")) {
			return;
		}
		EditorApplication.delayCall += () => {
			if (EditorApplication.isPlayingOrWillChangePlaymode) {
				return;
			}
			SessionState.SetString(k_TriedKey, pointer);
			try {
				Fetch();
			}
			catch (Exception e) {
				Debug.LogWarning($"DLF: could not fetch the shared large files, retry with Tools/DLF/Fetch: {e.Message}");
			}
		};
	}

	[MenuItem("Tools/DLF/Fetch")]
	private static void Fetch() {
		var pointer = ReadText(At(k_PointerName));
		if (pointer.Length == 0 || pointer == ReadText(State("synced"))) {
			return;
		}
		var index = RemoteIndex(out var keyId, out var secret);
		// The bucket holds one set, which can be newer or older than the one this commit names
		if (index == null || HashText(index) != pointer) {
			Debug.LogWarning("DLF: the shared large files are not the ones this commit names, so none were fetched. Pull the latest changes, or ask whoever changed them to push.");
			return;
		}
		var count = Apply(index, null, keyId, secret);
		Debug.Log($"DLF: fetched the shared large files ({count} changed).");
	}

	[MenuItem("Tools/DLF/Reset All To Remote")]
	private static void ResetAll() {
		Reset(null);
	}

	// The shared files among what is selected in the Project window, with their .meta files; a folder stands for all below it
	[MenuItem("Tools/DLF/Reset Selected To Remote")]
	private static void ResetSelected() {
		var root = At(".");
		Reset(Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath).Select(p => Path.GetRelativePath(root, Path.GetFullPath(p)).Replace('\\', '/')).ToList());
	}

	[MenuItem("Tools/DLF/Reset Selected To Remote", true)]
	private static bool CanResetSelected() {
		return Selection.assetGUIDs.Length > 0;
	}

	// Throws away local changes: the files become the ones in the bucket, whatever this commit names
	private static void Reset(List<string> selection) {
		var index = RemoteIndex(out var keyId, out var secret);
		if (index == null) {
			Debug.LogError("DLF: the bucket has no shared files yet.");
			return;
		}
		HashSet<string> only = null;
		if (selection != null) {
			var shared = Patterns().SelectMany(Matches).Concat(Entries(index).Keys).Distinct();
			only = new HashSet<string>(shared.Where(f => selection.Any(s => f == s || f == s + ".meta" || f.StartsWith(s + "/"))));
			if (only.Count == 0) {
				Debug.LogWarning("DLF: nothing in the selection is a shared file.");
				return;
			}
		}
		var what = only == null ? "all shared files" : $"{only.Count} selected shared files";
		if (!EditorUtility.DisplayDialog("DLF", $"Replace {what} on this machine with the ones in the bucket? Local changes to them are lost.", "Reset", "Cancel")) {
			return;
		}
		var count = Apply(index, only, keyId, secret);
		if (only == null) {
			// DLF.txt follows: it shows as changed when the bucket's set is not the one this commit names
			File.WriteAllText(At(k_PointerName), HashText(index));
		}
		Debug.Log($"DLF: reset {what} to the bucket's ({count} changed).");
	}

	// The bucket's list of files, null when it has none. Read with this machine's own key if it has one, else the read key everyone shares
	private static string RemoteIndex(out string keyId, out string secret) {
		keyId = Values(keyPath, "accessKeyId").FirstOrDefault() ?? Config("readAccessKeyId");
		secret = Values(keyPath, "secretAccessKey").FirstOrDefault() ?? Config("readSecretAccessKey");
		if (string.IsNullOrEmpty(secret)) {
			throw new Exception("the project has no read key, enter one in Tools/DLF/Settings");
		}
		using var response = Get("index", keyId, secret);
		var index = Task.Run(() => response.Content.ReadAsStringAsync()).GetAwaiter().GetResult();
		return response.IsSuccessStatusCode ? index : null;
	}

	// An index holds "hash path" per file
	private static Dictionary<string, string> Entries(string index) {
		return index.Split('\n').Where(e => e.Length > 0).ToDictionary(e => e.Substring(e.IndexOf(' ') + 1), e => e.Substring(0, e.IndexOf(' ')));
	}

	// Makes the local files match the index: files it does not name go, files that differ from it are replaced.
	// With "only", just those files. Returns how many files changed
	private static int Apply(string index, HashSet<string> only, string keyId, string secret) {
		var count = 0;
		try {
			// A run that fails halfway must not look like this checkout matches anything
			Directory.CreateDirectory(Path.GetDirectoryName(State("synced")));
			if (only == null) {
				File.Delete(State("synced"));
			}
			var wanted = Entries(index).Where(e => only == null || only.Contains(e.Key)).ToList();
			var named = new HashSet<string>(wanted.Select(e => e.Key));
			foreach (var old in Patterns().SelectMany(Matches).Distinct().Where(f => !named.Contains(f) && (only == null || only.Contains(f)))) {
				File.Delete(At(old));
				count++;
			}
			// shortcut: one file at a time, download in parallel if a full fetch gets too slow
			for (var i = 0; i < wanted.Count; i++) {
				var path = wanted[i].Key;
				var hash = wanted[i].Value;
				EditorUtility.DisplayProgressBar("DLF", $"Fetching {Path.GetFileName(path)}", (float)i / wanted.Count);
				if (File.Exists(At(path)) && Hash(At(path)) == hash) {
					continue;
				}
				using (var response = Get($"files/{hash}", keyId, secret))
				using (var packed = new GZipStream(Task.Run(() => response.Content.ReadAsStreamAsync()).GetAwaiter().GetResult(), CompressionMode.Decompress))
				using (var target = File.Create(k_TempPath)) {
					packed.CopyTo(target);
				}
				if (Hash(k_TempPath) != hash) {
					throw new Exception($"The download of {path} does not match the index.");
				}
				Directory.CreateDirectory(Path.GetDirectoryName(At(path)));
				File.Delete(At(path));
				File.Move(k_TempPath, At(path));
				count++;
			}
			if (only == null) {
				File.WriteAllText(State("synced"), HashText(index));
				// The set this checkout last saw in the bucket: the push hook only replaces that one
				File.WriteAllText(State("base"), HashText(index));
			}
			return count;
		}
		finally {
			EditorUtility.ClearProgressBar();
			AssetDatabase.Refresh();
		}
	}

	internal static List<string> Patterns() {
		return Values(At(k_ConfigName), "pattern");
	}

	// The files a pattern matches, as paths from the top of the repository with forward slashes.
	// * and ? match within one folder or file name
	internal static List<string> Matches(string pattern) {
		var root = At(".");
		var segments = pattern.Split('/');
		IEnumerable<string> folders = new[] { root };
		for (var i = 0; i < segments.Length - 1; i++) {
			var segment = segments[i];
			folders = folders.SelectMany(f => Directory.GetDirectories(f, segment)).ToList();
		}
		return folders.SelectMany(f => Directory.GetFiles(f, segments[segments.Length - 1])).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
	}

	// A GET signed with an access key (AWS Signature Version 4, R2's region is "auto")
	private static HttpResponseMessage Get(string name, string keyId, string secret) {
		var uri = new Uri($"{Config("endpoint").TrimEnd('/')}/{Config("bucket")}/{name}");
		var now = DateTime.UtcNow;
		var date = now.ToString("yyyyMMdd");
		var time = now.ToString("yyyyMMdd'T'HHmmss'Z'");
		var scope = $"{date}/auto/s3/aws4_request";
		var empty = HashText("");
		var canonical = $"GET\n{uri.AbsolutePath}\n\nhost:{uri.Authority}\nx-amz-content-sha256:{empty}\nx-amz-date:{time}\n\nhost;x-amz-content-sha256;x-amz-date\n{empty}";
		var toSign = $"AWS4-HMAC-SHA256\n{time}\n{scope}\n{HashText(canonical)}";
		var signingKey = new[] { date, "auto", "s3", "aws4_request" }.Aggregate(Encoding.UTF8.GetBytes("AWS4" + secret), Hmac);
		var request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.TryAddWithoutValidation("x-amz-content-sha256", empty);
		request.Headers.TryAddWithoutValidation("x-amz-date", time);
		request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={keyId}/{scope}, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={Hex(Hmac(signingKey, toSign))}");
		var response = Task.Run(() => s_Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)).GetAwaiter().GetResult();
		// A missing index is not an error: the bucket has no set yet, and its answer matches no commit
		if (!response.IsSuccessStatusCode && !(name == "index" && response.StatusCode == System.Net.HttpStatusCode.NotFound)) {
			throw new Exception($"{uri.Host} answered {(int)response.StatusCode} for {name}: {Task.Run(() => response.Content.ReadAsStringAsync()).GetAwaiter().GetResult()}");
		}
		return response;
	}

	private static byte[] Hmac(byte[] key, string text) {
		using var hmac = new HMACSHA256(key);
		return hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
	}

	private static string Hex(byte[] bytes) {
		return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
	}

	private static string Hash(string path) {
		using var sha = SHA256.Create();
		using var stream = File.OpenRead(path);
		return Hex(sha.ComputeHash(stream));
	}

	private static string HashText(string text) {
		using var sha = SHA256.Create();
		return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
	}
}