# DLF

DLF (Derek's Large Files) keeps files that are too large for git, such as baked lighting, video or builds, in a private S3 bucket
(Cloudflare R2 by default) and brings them into a Unity project. Git still decides which version everyone has: each
commit lists the files it needs.

## How it works

- `DLF.cfg`, at the top of your repository, says which files are shared (patterns), the bucket, the project's name in
  it, and a read key.
- `DLF.txt`, committed, lists the shared files: one line per file, with the hash of its content. A diff of it shows
  which files a commit changed.
- **Download**, for everyone: takes what is new in `DLF.txt`. The Unity Editor does it by itself on start, when it
  regains focus and before a build.
- **Upload**, on a machine with a write key: lists the shared files as they are now in `DLF.txt` and puts them in the
  bucket. You then commit `DLF.txt` with the changes that need the new files.

Downloads and uploads run in the background: Unity's Background Tasks, at the bottom right of the Editor, show how
far they are and can cancel them.

Nothing happens behind your back in git: the two hooks DLF adds only remind. One speaks up at a commit when shared
files changed and were not uploaded, the other at a push when it lists files the bucket does not have.

In the bucket, under the project's name:

| Object | Holds |
|---|---|
| `<project>/files/<hash>` | each file once, compressed, whichever branches use it |
| `<project>/branches/<branch>` | the `DLF.txt` last uploaded for that branch |
| `<project>/lock` | who is uploading right now |

A file stays in the bucket while a list there names it, or the newest pushed commit of any branch does. So branches
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

## Changes on two machines

A download works file by file, the way git merges. Compared with what this machine last took:

- a file that only changed in `DLF.txt` is downloaded, or removed;
- a file that only changed on this machine is left alone;
- a file that changed on both sides is a conflict. It is left as it is here and named, in the Console and in the
  DLF window. Upload shares yours for everyone; Reset takes the listed one.

So two people who change different files never get in each other's way: get the other's commit, and upload. An
upload takes what is new in `DLF.txt` first, the same way, so it never lists someone else's file as the older one
this machine still had.

`DLF.txt` itself is merged by git, line by line. When both branches changed the same file git reports a conflict in
it: keep one side's line for each file, then download.

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
write key gets no hooks. The block never stops a commit or a push, and the copies follow package updates.

## The two windows

- **`Tools > DLF > DLF`** shows where things stand, the way a git client shows changes: the files that changed on this
  machine (added, modified, deleted) and the ones a download would bring, whether `DLF.txt` is committed, and whether
  the bucket has it. Its buttons are every action: Download, Upload, Reset All, Reset Ticked, Unlock. Only a reset
  ever replaces a file that was changed on this machine.
- **`Tools > DLF > Settings`** holds the patterns, the bucket and the keys.

A status line in the corner of the Scene view names the next step, if there is one: files to download, a newer
`DLF.txt` to pull (as of the last fetch), files to upload, `DLF.txt` to commit or to push. Otherwise it says all is
good. A click on it opens the DLF window.

## Everyday use

- **Change the shared files:** open the DLF window, press Upload, then commit `DLF.txt` together with the changes
  that need the new files.
- **Get someone else's change:** get their commit and switch to Unity. It downloads what changed, and leaves the
  files you changed yourself alone.
- **Not ready to share a change:** do nothing. Files that changed on your machine stay there until you upload.
- **Throw a change away, or get a deleted file back:** Reset All makes every shared file the one `DLF.txt` lists.
  Reset Ticked does it for the files ticked in the window's list of changes. To undo an upload that is not committed
  yet, discard the change to `DLF.txt` in git first.

## Limits

- By default a file is removed from the bucket once nothing lists it any more, so an older commit of a branch that
  has since changed its files cannot download them. Turn on **Keep Old Versions** to never remove anything.
- Two people who change the shared files of the same branch at the same time: the second upload is refused until
  that machine has the first one's commit. Only a file both of them changed has to be redone or chosen.
- While someone uploads, the bucket is locked and others wait. A lock left behind by an upload that died is ignored
  after an hour; the Unlock button of the DLF window clears it sooner.
- The list of a branch that was merged and deleted is removed at the next upload. One whose files were uploaded but
  never committed stays in the bucket, with its files, until it is removed there by hand.
- An upload removes files nothing needs, going by the branches the uploading machine last fetched. A branch pushed
  since then, that lists files which are in no list of the bucket, could lose them: upload again from that branch.
- The hooks remind, they do not enforce: a commit can list files that were never uploaded.
- Downloading is started by Unity. Without Unity, run `sh <package folder>/Hooks~/dlf download` from the top of the
  repository (Unity keeps a package installed from git in `Library/PackageCache`).
- A single file can be at most 5 GB: a larger one cannot be uploaded in one piece, and DLF does not split files.
- Only one download, upload or reset runs at a time on a machine.
- Git must be installed and on the PATH: the git inside GitHub Desktop or another client is not enough. Without it
  the Console says so, and a build stops.
- A shared file with a line break in its name is not supported.

## Tests

`Tests~/dlftest.sh` runs the scripts end to end against a stand-in bucket (`Tests~/fakes3.py`): several checkouts of
one repository upload, commit, push, pull, download and reset, and it checks the files, `DLF.txt` and the bucket. It
needs git, curl and python, takes a few minutes, and leaves the Unity side to be tried in the Editor:

    bash Tests~/dlftest.sh

## Requirements

git, and what it brings or every system has: `sh`, `curl` 7.75 or newer, `gzip`, and `sha256sum` or `shasum`.
On Windows, Git for Windows provides all of them. The bucket must support conditional writes (`If-None-Match`), which
the upload lock uses: Cloudflare R2 and Amazon S3 do.

## Where things are stored

| What | Where |
|---|---|
| Patterns, bucket, project, region, settings, read key | `DLF.cfg`, committed |
| The files a commit needs | `DLF.txt`, committed |
| Write key | `%APPDATA%/DLF/<bucket>.cfg`, or `~/.config/DLF/` elsewhere, per machine |
| Which list the local files last matched, and which this machine last uploaded | `.git/dlf/` |
| The hook scripts git runs | `.git/dlf-hooks/`, copied from the package |
