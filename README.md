# DLF

DLF (Derek's Large Files) keeps files that are too large for git, such as baked lighting, in a private S3 bucket
(Cloudflare R2 by default) and brings them into a Unity project. Git still decides which version everyone has: each
commit names the set of files it needs.

## How it works

- `DLF.cfg`, at the top of your repository, lists which files are shared (patterns), the bucket and a read key.
- The bucket holds **one set** of those files: each file compressed and named by the hash of its content, and an index
  that lists them.
- `DLF.txt`, committed, holds the hash of the index the commit needs.
- **Fetching**, for everyone: on start, when it regains focus and before a build, the Unity Editor makes the local
  files match the set the checked-out commit names.
- **Publishing**, only on a machine with a write key, through two git hooks:
  - `pre-commit`: when the shared files changed, it rewrites `DLF.txt` and stops the commit once, so you can stage
    `DLF.txt` with the changes that need the new files.
  - `pre-push`: a change to `DLF.txt` never leaves without its files. They are uploaded first, or the push is refused.

All of this is done by the shell scripts in `Hooks~`, which git runs with the shell it brings. The Unity code only
runs them and shows the settings window. Git also matches the patterns, so they mean the same on every system:
`*` and `?` stay within one folder or file name, `**` crosses folders.

## Install

1. Add the package to the Unity project (Package Manager, "Add package from git URL", or a line in
   `Packages/manifest.json`). It can also live inside the repository as an embedded package or a submodule.
2. Create a bucket and two API tokens for it: one read-only, one read and write.
3. Open `Tools > DLF > Settings`, open `Keys`, and fill in the bucket, the read key and, on a machine that publishes,
   the write key. Enter the patterns of the files to share. Save.
4. Commit `DLF.cfg` and `.gitignore`. Saving listed the patterns in `.gitignore`: files that git already tracks must be
   removed from git by hand (`git rm --cached`).

On a machine with a write key, the Editor adds the hooks by itself, without taking over the repository's hooks: it
copies the scripts to `.git/dlf-hooks/` and adds a marked block to the normal `pre-commit` and `pre-push` hooks in
`.git/hooks/` that calls them first. Hooks that are already there, such as Git LFS's, keep running. A repository that
keeps its hooks in another folder (`core.hooksPath`) is left alone, with a note in the Console on how to call the DLF
hooks from them. A machine without a write key gets no hooks.

## Everyday use

- **Change the shared files** (rebake): commit. The commit stops once; stage `DLF.txt` and commit again. Push: the
  changed files are uploaded and the ones the new set no longer has are removed from the bucket.
- **Get someone else's change:** pull and switch to Unity. It fetches what changed.
- **Not ready to share a change:** commit without staging `DLF.txt`. Nothing is uploaded.
- **Publish without pushing:** `Tools > DLF > Publish` (or `sh Hooks~/dlf publish`) uploads the set the checked-out
  commit names, exactly as pushing that commit would. A later push finds the files already there.
- **Throw a change away:** `Tools > DLF > Reset All To Remote` makes every shared file the one in the bucket.
  `Reset Selected To Remote` does it for the files and folders selected in the Project window.

## Limits

- The bucket keeps one set. An older commit, or a second branch that names another set, cannot fetch its files.
- Two people who change the shared files at the same time: the second push is refused, and that change has to be
  redone on top of the first.
- Commits made with `--no-verify` or with low-level git commands skip the hooks.
- Fetching is started by Unity. Without Unity, run `sh Hooks~/dlf fetch` from the top of the repository.
- A shared file with a line break in its name is not supported.

## Requirements

git, and what it brings or every system has: `sh`, `curl` 7.75 or newer, `gzip`, and `sha256sum` or `shasum`.
On Windows, Git for Windows provides all of them.

## Where things are stored

| What | Where |
|---|---|
| Patterns, bucket, region, read key | `DLF.cfg`, committed |
| The set a commit needs | `DLF.txt`, committed |
| Write key | `%APPDATA%/DLF/<bucket>.cfg`, or `~/.config/DLF/` elsewhere, per machine |
| What a checkout last fetched or published | `.git/dlf/` |
| The hook scripts git runs | `.git/dlf-hooks/`, copied from the package |
