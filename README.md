# DLF

DLF (Derek's Large Files) keeps files that are too large for git, such as baked lighting, in a private S3 bucket
(Cloudflare R2 by default) and brings them into a Unity project. Git still decides which version everyone has: each
commit lists the files it needs.

## How it works

- `DLF.cfg`, at the top of your repository, says which files are shared (patterns), the bucket, the project's name in
  it, and a read key.
- `DLF.txt`, committed, lists the shared files: one line per file, with the hash of its content. A diff of it shows
  which files a commit changed.
- **Download**, for everyone: makes the local files the ones `DLF.txt` lists. The Unity Editor does it by itself on
  start, when it regains focus and before a build.
- **Upload**, on a machine with a write key: lists the shared files as they are now in `DLF.txt` and puts them in the
  bucket. You then commit `DLF.txt` with the changes that need the new files.

Nothing happens behind your back in git: the two hooks DLF adds only remind. One speaks up at a commit when shared
files changed and were not uploaded, the other at a push when it lists files the bucket does not have.

In the bucket, under the project's name:

| Object | Holds |
|---|---|
| `<project>/files/<hash>` | each file once, compressed, whichever branches use it |
| `<project>/branches/<branch>` | the `DLF.txt` last uploaded for that branch |
| `<project>/lock` | who is uploading right now |

A file stays in the bucket while a list there names it, or a commit already pushed for the branch does. So branches
with different files can all download, a new branch costs nothing until its files differ, nobody loses files between
your upload and your push, and several projects can share one bucket.

Two settings change that, both in `Tools > DLF > Settings` under `Keys > Bucket`:

- **One Set For All Branches** (`oneSetForAllBranches=true`): all branches share one list, `<project>/set`, which holds
  whatever was uploaded last. Smallest bucket; a branch whose files were replaced by another's can no longer download.
- **Keep Old Versions** (`keepOldVersions=true`): nothing is ever removed, so every commit of every branch can
  download, and the bucket grows with every change.

All of this is done by the shell scripts in `Hooks~`, which git runs with the shell it brings. The Unity code only
runs them and shows the two windows. Git also matches the patterns, so they mean the same on every system:
`*` and `?` stay within one folder or file name, `**` crosses folders.

## Install

1. Add the package to the Unity project (Package Manager, "Add package from git URL", or a line in
   `Packages/manifest.json`). It can also live inside the repository as an embedded package or a submodule.
2. Create a bucket and two API tokens for it: one read-only, one read and write.
3. Open `Tools > DLF > Settings`, open `Keys`, and fill in the bucket, a project name, the read key and, on a machine
   that uploads, the write key. Enter the patterns of the files to share. Save.
4. Commit `DLF.cfg` and `.gitignore`. Saving listed the patterns in `.gitignore`: files that git already tracks must be
   removed from git by hand (`git rm --cached`).
5. On the machine with the write key, open `Tools > DLF > DLF`, press Upload, and commit `DLF.txt`.

On a machine with a write key, the Editor adds the two reminder hooks without taking over the repository's hooks: it
copies the scripts to `.git/dlf-hooks/` and adds a marked block to the normal `pre-commit` and `pre-push` hooks in
`.git/hooks/` that calls them first. Hooks that are already there, such as Git LFS's, keep running. A repository that
keeps its hooks in another folder (`core.hooksPath`) is left alone, with a note in the Console. A machine without a
write key gets no hooks.

## The two windows

- **`Tools > DLF > DLF`** shows where things stand, the way a git client shows changes: the files that changed on this
  machine (added, modified, deleted) and the ones a download would bring, whether `DLF.txt` is committed, and whether
  the bucket has it. Its buttons are every action: Download, Upload, Reset All, Reset Ticked, Unlock.
- **`Tools > DLF > Settings`** holds the patterns, the bucket and the keys.

A status line in the corner of the Scene view says in a few words whether the shared files are up to date, changed
here or waiting to be downloaded. A click on it opens the DLF window.

## Everyday use

- **Change the shared files** (rebake): open the DLF window, press Upload, then commit `DLF.txt` together with the
  changes that need the new files.
- **Get someone else's change:** get their commit and switch to Unity. It downloads what changed.
- **Not ready to share a change:** do nothing. Files that changed on your machine stay there until you upload.
- **Throw a change away:** Reset All makes every shared file the one the checked-out commit lists, and puts `DLF.txt`
  back. Reset Ticked does it for the files ticked in the window's list of changes.

## Limits

- By default a file is removed from the bucket once nothing lists it any more, so an older commit of a branch that
  has since changed its files cannot download them. Turn on **Keep Old Versions** to never remove anything.
- Two people who change the shared files of the same branch at the same time: the second upload is refused, and that
  change has to be redone on top of the first.
- While someone uploads, the bucket is locked and others wait. A lock left behind by an upload that died is ignored
  after an hour; the Unlock button of the DLF window clears it sooner.
- The list of a branch that was deleted stays in the bucket, with its files, until it is removed there by hand.
- The hooks remind, they do not enforce: a commit can list files that were never uploaded.
- Downloading is started by Unity. Without Unity, run `sh Hooks~/dlf download` from the top of the repository.
- A shared file with a line break in its name is not supported.

## Requirements

git, and what it brings or every system has: `sh`, `curl` 7.75 or newer, `gzip`, and `sha256sum` or `shasum`.
On Windows, Git for Windows provides all of them.

## Where things are stored

| What | Where |
|---|---|
| Patterns, bucket, project, region, settings, read key | `DLF.cfg`, committed |
| The files a commit needs | `DLF.txt`, committed |
| Write key | `%APPDATA%/DLF/<bucket>.cfg`, or `~/.config/DLF/` elsewhere, per machine |
| Which list the local files last matched, and which this machine last uploaded | `.git/dlf/` |
| The hook scripts git runs | `.git/dlf-hooks/`, copied from the package |
