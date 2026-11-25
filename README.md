# DLF

DLF (Derek's Large Files) keeps files that are too large for git, such as baked lighting, in a private S3 bucket
(Cloudflare R2) and brings them into a Unity project. Git still decides which version everyone has: each commit names
the set of files it needs.

## How it works

- `DLF.cfg`, at the top of your repository, lists which files are shared (patterns), the bucket and a read key.
- The bucket holds **one set** of those files: each file compressed and named by the hash of its content, and an index
  that lists them.
- `DLF.txt`, committed, holds the hash of the index the commit needs.
- **Fetching** is done by the Unity Editor, for everyone: on start, when it regains focus and before a build it makes
  the local files match the set the checked-out commit names.
- **Publishing** is done by two git hooks, only on a machine with a write key:
  - `pre-commit`: when the shared files changed, it rewrites `DLF.txt` and stops the commit once, so you can stage
    `DLF.txt` with the changes that need the new files.
  - `pre-push`: a change to `DLF.txt` never leaves without its files. They are uploaded first, or the push is refused.

## Install

1. Add the package to the Unity project (Package Manager, "Add package from git URL", or a line in
   `Packages/manifest.json`). It can also live inside the repository as an embedded package or a submodule.
2. Create a bucket and two API tokens for it: one read-only, one read and write.
3. Open `Tools > DLF > Settings`, open `Keys`, and fill in the bucket, the read key and, on a machine that publishes,
   the write key. Enter the patterns of the files to share. Save.
4. Commit `DLF.cfg` and `.gitignore`. Saving listed the patterns in `.gitignore`: files that git already tracks must be
   removed from git by hand (`git rm --cached`).

The Editor sets the hooks up by itself, without taking over the repository's hooks: it copies the scripts to
`.git/dlf-hooks/` and adds a marked block to the normal `pre-commit` and `pre-push` hooks in `.git/hooks/` that calls
them first. Hooks that are already there, such as Git LFS's, keep running. A repository that keeps its hooks in another
folder (`core.hooksPath`) is left alone, with a note in the Console on how to call the DLF hooks from them.

## Everyday use

- **Change the shared files** (rebake): commit. The commit stops once; stage `DLF.txt` and commit again. Push: the
  changed files are uploaded and the ones the new set no longer has are removed from the bucket.
- **Get someone else's change:** pull and switch to Unity. It fetches what changed.
- **Not ready to share a change:** commit without staging `DLF.txt`. Nothing is uploaded.
- **Throw a change away:** `Tools > DLF > Reset All To Remote` makes every shared file the one in the bucket.
  `Reset Selected To Remote` does it for the files and folders selected in the Project window.

## Limits

- The bucket keeps one set. An older commit, or a second branch that names another set, cannot fetch its files.
- Two people who change the shared files at the same time: the second push is refused, and that change has to be
  redone on top of the first.
- Commits made with `--no-verify` or with low-level git commands skip the hooks.
- The hooks need `sh`, `curl` 7.75 or newer, `sha256sum`, `gzip` and GNU `stat`, as shipped with Git for Windows.
- Fetching needs Unity. A repository without a Unity project has no way to fetch.
- A shared file with a space in its path is not supported by the hooks.

## Where things are stored

| What | Where |
|---|---|
| Patterns, bucket, read key | `DLF.cfg`, committed |
| The set a commit needs | `DLF.txt`, committed |
| Write key | `%APPDATA%/DLF/<bucket>.cfg` (or `~/.config/DLF/`), per machine |
| What a checkout last fetched or published | `.git/dlf/` |
