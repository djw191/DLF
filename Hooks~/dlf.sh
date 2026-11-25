# DLF (Derek's Large Files): the files a project keeps out of git live in an S3 bucket instead. This is all of its logic,
# shared by the dlf command that Unity runs and the git hooks, which only remind.
#
# DLF.cfg, at the top of the repository, says which files are shared and where. DLF.txt, committed, lists them: one
# "hash path" line per file, the hash being the SHA-256 of its content. So every commit says exactly which files it needs.
#
# In the bucket, under the project's name:
#   <project>/files/<hash>       each file once, gzipped, whatever branches use it
#   <project>/branches/<branch>  the DLF.txt last uploaded for that branch
#   <project>/set                with oneSetForAllBranches=true in DLF.cfg: the one list all branches share, instead
#   <project>/lock               who is uploading right now
# A file is kept while a list in the bucket names it, or the newest commit this machine knows as pushed for any branch,
# or the checked-out commit; and for good with keepOldVersions=true in DLF.cfg.
#
# On this machine three things are kept, in the dlf folder of the repository's git folder: "synced", the git id of
# the list the shared files last matched; "seen", the size and time each of them had then (a file that differs from
# it, was added or is gone, is what "changed" means); and "uploaded", the git id of the list this machine last put
# in the bucket. Everything else a command writes goes to a folder of its own, removed when it ends: two commands
# at once, such as a status check during a download, never touch each other's files.
#
# Needs only what git brings or POSIX has: sh, git, curl 7.75 or newer, gzip, and sha256sum or shasum.
# Sorting and comparing sorted lists must not depend on the machine's language settings
export LC_ALL=C
cfg=DLF.cfg

value() { sed -n "s/^$1=//p" "$2" 2>/dev/null | tr -d '\r' | head -n 1; }

endpoint=$(value endpoint "$cfg")
endpoint=${endpoint%/}
bucket=$(value bucket "$cfg")
project=$(value project "$cfg")
region=$(value region "$cfg")
region=${region:-auto}
# keepOldVersions=true: files stay in the bucket for good, so that every commit can still download its own
keep=$(value keepOldVersions "$cfg")
# oneSetForAllBranches=true: every branch uploads to the same list, so the bucket holds what was uploaded last,
# whichever branch it was on. Otherwise each branch has its own list
one_set=$(value oneSetForAllBranches "$cfg")
# The write key is kept per machine and per bucket, outside the repository
keys="${APPDATA:-${XDG_CONFIG_HOME:-$HOME/.config}}/DLF/$bucket.cfg"
state="$(git rev-parse --git-dir)/dlf"
work="$state/work.$$"
mkdir -p "$work"
trap 'rm -rf "$work"' EXIT
# What a command that was killed left behind
find "$state" -maxdepth 1 -name 'work.*' -mmin +1440 -exec rm -rf {} + 2> /dev/null
# Who holds the lock: the same person on another machine is someone else
me="$(git config user.name)@$(uname -n)"
hasher=sha256sum
command -v sha256sum > /dev/null 2>&1 || hasher="shasum -a 256"
# Prints "size/time/path" of a file: GNU's stat, or BSD's
stat="stat -c %s/%Y/%n"
stat -c %s . > /dev/null 2>&1 || stat="stat -f %z/%m/%N"
# A lock older than this is left over from an upload that died, and is ignored
lock_seconds=3600

fail() {
	echo "DLF: $*" >&2
	exit 1
}

# The files the patterns match, sorted and NUL separated. Git does the matching, the same way on every system:
# * and ? stay within one folder or file name, ** crosses folders
shared_files() {
	patterns=$(sed -n 's/^pattern=//p' "$cfg" | tr -d '\r')
	[ -n "$patterns" ] || return 0
	printf '%s\n' "$patterns" | sed 's/^/:(top,glob)/' | tr '\n' '\0' | xargs -0 git ls-files --others --full-name -z -- | sort -z
}

# Prints the size and time of every shared file. Telling by these whether a file changed needs no hashing, and
# also catches a file copied in from elsewhere, which keeps its old time
seen() {
	shared_files > "$work/seen.names"
	[ -s "$work/seen.names" ] && xargs -0 $stat < "$work/seen.names"
	rm -f "$work/seen.names"
}

# Prints the list of the shared files as they are now, "hash path" per line. Hashing gigabytes takes a while
local_list() {
	echo "DLF: checking the shared large files..." >&2
	# What is remembered for this list is taken before the hashing, so that a file written during it counts as changed
	seen > "$work/seen"
	shared_files > "$work/names"
	[ -s "$work/names" ] && xargs -0 $hasher < "$work/names" | sed 's/^\([0-9a-f]*\) [ *]/\1 /'
	rm -f "$work/names"
}

# The git id of a list read from the input, with line endings as git stores them
list_id() { tr -d '\r' | git hash-object --stdin; }

# True when the shared files are still the ones the marker's list names: same names, same sizes and times
unchanged() {
	[ -f "$state/synced" ] && [ -f "$state/seen" ] || return 1
	git cat-file -e "$(cat "$state/synced")" 2> /dev/null || return 1
	[ "$(shared_files | tr '\0' '\n')" = "$(git cat-file blob "$(cat "$state/synced")" | cut -d' ' -f2-)" ] || return 1
	[ "$(seen)" = "$(cat "$state/seen")" ]
}

# Notes that the shared files are now the list on the input. The list is stored in git's own object store, where
# its content can be read back later
mark_synced() {
	tr -d '\r' | git hash-object -w --stdin > "$state/synced"
	if [ -f "$work/seen" ]; then
		mv "$work/seen" "$state/seen"
	else
		seen > "$state/seen"
	fi
}

# Prints the list the shared files have now: remembered when nothing changed, hashed otherwise
current_list() {
	if unchanged; then
		git cat-file blob "$(cat "$state/synced")"
	else
		local_list
	fi
}

# Requests are signed with this key. It is handed to curl in a file only this user can read, not on the command line
use_key() {
	[ -n "$project" ] || fail "DLF.cfg has no project name. Enter one in Tools > DLF > Settings."
	(umask 077 && printf 'user = "%s:%s"\n' "$1" "$2" > "$work/key")
}

use_write_key() {
	[ -f "$keys" ] || fail "this machine has no write key. Enter one in Tools > DLF > Settings."
	use_key "$(value accessKeyId "$keys")" "$(value secretAccessKey "$keys")"
}

# This machine's own key if it has one, else the read key everyone shares
use_read_key() {
	if [ -f "$keys" ]; then
		use_write_key
	else
		[ -n "$(value readSecretAccessKey "$cfg")" ] || fail "DLF.cfg has no read key. Enter one in Tools > DLF > Settings."
		use_key "$(value readAccessKeyId "$cfg")" "$(value readSecretAccessKey "$cfg")"
	fi
}

# signed [curl options] URL: a signed request
signed() { curl -sS -f -K "$work/key" --aws-sigv4 "aws:amz:$region:s3" "$@"; }

# r2 METHOD NAME [curl options]: a signed request for one object of the project
r2() {
	method=$1
	name=$2
	shift 2
	signed -X "$method" "$@" "$endpoint/$bucket/$project/$name"
}

# get NAME FILE: gets an object into FILE, empty when the bucket does not have it. Only "not there" counts as
# empty: any other failure stops, so that it never looks like there is nothing there
get() {
	code=$(r2 GET "$1" -o "$2" -w '%{http_code}' 2> /dev/null)
	case "$code" in
		200) ;;
		404) : > "$2" ;;
		*) fail "could not reach the bucket (answer $code). Try again." ;;
	esac
}

# put NAME FILE [curl options]: uploads a file as an object
put() {
	object=$1
	file=$2
	shift 2
	r2 PUT "$object" "$@" -H "x-amz-content-sha256: $($hasher < "$file" | cut -c1-64)" -T "$file" > /dev/null
}

# names PREFIX: the names of the project's objects that start with PREFIX, without the project's own name
names() {
	token=
	while :; do
		signed "$endpoint/$bucket?list-type=2&prefix=$project/$1${token:+&continuation-token=$token}" > "$work/page" || fail "could not list the bucket."
		tr '<' '\n' < "$work/page" | sed -n "s|^Key>$project/||p"
		# A long listing comes in pages
		token=$(tr '<' '\n' < "$work/page" | sed -n 's|^NextContinuationToken>||p' | head -n 1)
		[ -n "$token" ] || break
	done
	rm -f "$work/page"
}

# The branch this checkout's files belong to in the bucket: the branch it sends its commits to when that is set up,
# else its own name
current_branch() {
	upstream=$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2> /dev/null) && printf '%s' "${upstream#*/}" && return
	git symbolic-ref --short -q HEAD
}

# The name a branch's list has in the bucket: its own, or the one all branches share
branch_key() {
	if [ "$one_set" = true ]; then
		printf 'set'
	else
		printf 'branches/%s' "$(printf '%s' "$1" | sed 's/[^A-Za-z0-9._/-]/_/g')"
	fi
}

# Stops while someone else is uploading: files are being added and removed
check_lock() {
	get lock "$work/lock"
	IFS='|' read -r owner since what < "$work/lock" || true
	rm -f "$work/lock"
	if [ -n "$since" ] && [ $(($(date +%s) - since)) -lt $lock_seconds ] && [ "$owner" != "$me" ]; then
		fail "$owner is uploading $what right now. Try again in a moment (Unlock in the DLF window clears a lock that was left behind)."
	fi
}

# True when the path is among the selected ones: the path itself, its .meta, or anything in a selected folder
selected() {
	path=$1
	shift
	for choice; do
		case "$path" in "$choice" | "$choice.meta" | "$choice"/*) return 0 ;; esac
	done
	return 1
}

# apply LIST: makes the local shared files the ones the sorted list in the file LIST names. Files it does not name
# are removed, files that differ from it are downloaded. The caller has listed the files as they are now in
# $work/have. Prints "PROGRESS done total path"
apply() {
	check_lock
	comm -23 "$1" "$work/have" > "$work/needed"
	if [ -s "$work/needed" ]; then
		# Everything is downloaded and checked in a side folder first: a download that fails leaves the files as they were
		rm -rf "$work/in"
		mkdir -p "$work/in"
		total=$(wc -l < "$work/needed" | tr -d ' ')
		echo "PROGRESS 0 $total downloading"
		cut -d' ' -f1 "$work/needed" | sort -u | while read -r hash; do
			printf 'url = "%s/%s/%s/files/%s"\noutput = "%s/in/%s.gz"\n' "$endpoint" "$bucket" "$project" "$hash" "$work" "$hash"
		done > "$work/get"
		signed --parallel --parallel-max 8 -K "$work/get" 2> /dev/null || fail "some of the files DLF.txt lists are not in the bucket (any more), or it could not be reached. Nothing was changed."
		for packed in "$work"/in/*.gz; do
			gzip -dc "$packed" > "${packed%.gz}" || fail "a downloaded file is damaged."
			rm -f "$packed"
		done
		# Each file is named by its hash: one that does not match its name is not what was asked for
		printf '%s\0' "$work"/in/* | xargs -0 $hasher | sed 's/^\([0-9a-f]*\) [ *]/\1 /' | while read -r hash file; do
			[ "$hash" = "${file##*/}" ] || fail "a downloaded file does not match the list."
		done || exit 1
		done=0
		folder=
		while read -r hash path; do
			done=$((done + 1))
			echo "PROGRESS $done $total $path"
			case "$path" in */*) [ "${path%/*}" = "$folder" ] || { folder=${path%/*}; mkdir -p "$folder"; } ;; esac
			cp "$work/in/$hash" "$path"
		done < "$work/needed"
	fi
	# Only now that every file it needs is in place, the files the list does not name are removed
	cut -d' ' -f2- "$1" | sort > "$work/wanted.paths"
	cut -d' ' -f2- "$work/have" | sort | comm -23 - "$work/wanted.paths" | while IFS= read -r path; do
		rm -f "$path"
	done
	rm -rf "$work/in" "$work/get" "$work/wanted.paths" "$work/needed" "$work/seen"
}

# differing A B: the paths whose line is not the same in the sorted lists A and B
differing() { sort "$1" "$2" | uniq -u | cut -d' ' -f2- | sort -u; }

# lines_at PATHS LIST 1: the lines of LIST whose path is named in the file PATHS; with 0, the other lines.
# A line is a 64 character hash, a space, and the path
lines_at() { awk -v wanted="$3" 'FILENAME == ARGV[1] { named[$0]; next } ((substr($0, 66) in named) == wanted)' "$1" "$2"; }

# sync [--all | PATH...]: brings the shared files in line with DLF.txt, file by file, the way git merges. Compared
# with the list this machine last took: a file that only changed in DLF.txt is downloaded (or removed), a file that
# only changed here is left alone, and a file that changed on both sides is left alone and named in $work/conflicts.
# Changes made here to the selected paths, or with --all to any, are thrown away instead: those files become the
# listed ones too
sync() {
	tr -d '\r' < DLF.txt | sort > "$work/theirs"
	# The list must never reach outside the repository
	while read -r hash path; do
		case "$hash" in *[!0-9a-f]*) fail "DLF.txt is not a list of files. If git left a merge conflict in it, settle that first: keep one side's line for each file." ;; esac
		case "$path" in "" | /* | ?:* | ../* | */../* | */.. | *\\*) fail "the list of shared files names a path outside the project: $path" ;; esac
	done < "$work/theirs"
	if [ -f "$state/synced" ] && git cat-file -e "$(cat "$state/synced")" 2> /dev/null; then
		git cat-file blob "$(cat "$state/synced")" | sort > "$work/base"
	else
		: > "$work/base"
	fi
	current_list | sort > "$work/have"
	differing "$work/base" "$work/have" > "$work/changed"
	if [ "$1" = --all ]; then
		: > "$work/kept"
	elif [ $# -eq 0 ]; then
		cp "$work/changed" "$work/kept"
	else
		while IFS= read -r path; do
			selected "$path" "$@" || printf '%s\n' "$path"
		done < "$work/changed" > "$work/kept"
	fi
	# A change made here that gives the listed file anyway is no change to keep
	differing "$work/have" "$work/theirs" | comm -12 - "$work/kept" > "$work/both"
	mv "$work/both" "$work/kept"
	differing "$work/base" "$work/theirs" | comm -12 - "$work/kept" > "$work/conflicts"
	# What the files become: the listed ones, except where a change made here is kept
	{
		lines_at "$work/kept" "$work/theirs" 0
		lines_at "$work/kept" "$work/have" 1
	} | sort > "$work/target"
	# What this machine has taken of DLF.txt after this: all of it, except the files in conflict
	if [ -s "$work/conflicts" ]; then
		{
			lines_at "$work/conflicts" "$work/theirs" 0
			lines_at "$work/conflicts" "$work/base" 1
		} | sort -k 2 > "$work/taken"
	else
		tr -d '\r' < DLF.txt > "$work/taken"
	fi
	apply "$work/target"
	mark_synced < "$work/taken"
	if [ -s "$work/kept" ]; then
		# The files that keep a change made here go on counting as changed: they are left out of what was seen
		awk 'FILENAME == ARGV[1] { named[$0]; next } { path = $0; sub(/^[^\/]*\/[^\/]*\//, "", path); if (!(path in named)) print }' "$work/kept" "$state/seen" > "$work/seen.kept"
		mv "$work/seen.kept" "$state/seen"
	fi
	# $work/target stays: it lists the files as they are now, which an upload needs next
	rm -f "$work/theirs" "$work/base" "$work/have" "$work/changed" "$work/kept" "$work/taken"
}

# After sync: names the files it left alone because they changed on both sides, and fails when there are any
report_conflicts() {
	if [ -s "$work/conflicts" ]; then
		echo "DLF: these files changed both on this machine and in DLF.txt, and were left as they are here:" >&2
		sed 's/^/  /' "$work/conflicts" >&2
		echo "In the DLF window: Upload shares yours, Reset takes the listed ones instead." >&2
		rm -f "$work/conflicts"
		return 1
	fi
	rm -f "$work/conflicts"
}

# upload BRANCH: lists the shared files as they are now in DLF.txt and puts them in the bucket as BRANCH's set.
# New files go up first, then the branch's list, then files nothing needs any more are removed. DLF.txt is left for
# the author to commit. Stops when it must not replace what is there
upload() {
	branch=$1
	merged=
	use_write_key
	# What DLF.txt lists and this machine never took is taken first, file by file: an upload lists the files as they
	# are here, and must not put older ones back. A file that changed on both sides stays as it is here
	if [ -f DLF.txt ] && [ "$(cat "$state/synced" 2> /dev/null)" != "$(list_id < DLF.txt)" ]; then
		if (sync); then
			merged=1
		else
			echo "DLF: could not download the files DLF.txt lists first; uploading the files as they are here." >&2
		fi
		rm -f "$work/conflicts"
	fi
	if [ -n "$merged" ]; then
		# Hashed a moment ago, by the merge
		sort -k 2 "$work/target" > "$work/new"
	else
		current_list > "$work/new"
	fi
	[ -s "$work/new" ] || fail "nothing to upload: no file matches the patterns in DLF.cfg."
	id=$(list_id < "$work/new")
	get "$(branch_key "$branch")" "$work/old"
	old_id=$(list_id < "$work/old")
	# What is there for the branch must be a list this checkout knows: one it uploaded itself, or one in the history
	# of the checked-out commit. Otherwise someone else's newer files would be replaced. With one set for all branches
# there is nothing to protect: the set is whatever was uploaded last
	if [ "$one_set" != true ] && [ -s "$work/old" ] && [ "$old_id" != "$id" ] && [ "$old_id" != "$(cat "$state/uploaded" 2> /dev/null)" ]; then
		known=$(git rev-list HEAD -- DLF.txt 2> /dev/null | while read -r past; do git rev-parse -q --verify "$past:DLF.txt"; done | grep -c "^$old_id$")
		[ "$known" -gt 0 ] || fail "not uploaded: someone else uploaded other files for $branch. Get their commit and upload again: your changed files are kept, theirs are downloaded."
	fi
	# From here on the upload is not refused any more: DLF.txt becomes the list of the files as they are here
	if [ ! -f DLF.txt ] || [ "$(list_id < DLF.txt)" != "$id" ]; then
		cp "$work/new" DLF.txt
	fi
	mark_synced < "$work/new"
	if [ "$old_id" = "$id" ]; then
		echo "$id" > "$state/uploaded"
		rm -f "$work/old" "$work/new"
		echo "DLF: nothing to upload, the bucket already has these files for $branch." >&2
		return 0
	fi
	check_lock
	# shortcut: a lock left behind is deleted before the new one is written, and two uploaders doing that in the same
	# moment could both get in; replace it with a write that needs the old lock's ETag (If-Match) if that ever happens
	[ -n "$since" ] && r2 DELETE lock > /dev/null 2>&1
	printf '%s|%s|%s' "$me" "$(date +%s)" "$branch" > "$work/lock"
	# The bucket only takes this write while there is no lock: of two uploaders starting together, one is refused
	put lock "$work/lock" -H 'If-None-Match: *' 2> /dev/null || fail "not uploaded: someone else started uploading at the same moment, or the bucket could not be reached. Try again."
	rm -f "$work/lock"
	trap 'r2 DELETE lock > /dev/null 2>&1; rm -rf "$work"' EXIT
	echo "DLF: uploading the large files of $branch..." >&2
	names files/ | sed 's|^files/||' | sort > "$work/there"
	total=$(wc -l < "$work/new" | tr -d ' ')
	done=0
	while read -r hash path; do
		done=$((done + 1))
		grep -qxF "$hash" "$work/there" && continue
		echo "PROGRESS $done $total $path"
		gzip -1 -c "$path" > "$work/tmp"
		put "files/$hash" "$work/tmp" || fail "not uploaded: could not upload $path."
		echo "$hash" >> "$work/there"
	done < "$work/new"
	put "$(branch_key "$branch")" "$work/new" || fail "not uploaded: could not upload the list."
	echo "$id" > "$state/uploaded"
	if [ "$keep" != true ]; then
		# The list of a branch that is gone is removed, when it is safe to: the branch is on no remote (the remotes are
		# asked, what this machine last fetched may be out of date) and not on this machine, and its list is in the
		# history of a pushed branch, so it was merged. A list nobody committed stays: it may be someone's work
		if heads=$(for remote in $(git remote); do GIT_TERMINAL_PROMPT=0 git ls-remote --heads "$remote" || exit 1; done 2> /dev/null) && [ -n "$heads" ]; then
			{
				printf '%s\n' "$heads" | sed 's|^.*refs/heads/||'
				git for-each-ref --format='%(refname:short)' refs/heads
				printf '%s\n' "$branch"
			} | while IFS= read -r head; do branch_key "$head"; echo; done | sort -u > "$work/alive"
			git rev-list --remotes -- DLF.txt | while read -r past; do git rev-parse -q --verify "$past:DLF.txt"; done | sort -u > "$work/pushed"
			names branches/ | sort | comm -23 - "$work/alive" | while IFS= read -r key; do
				get "$key" "$work/other"
				grep -qx "$(list_id < "$work/other")" "$work/pushed" && r2 DELETE "$key" > /dev/null
			done
			rm -f "$work/alive" "$work/pushed"
		fi
		# Kept: what any list in the bucket names, what the newest pushed commit of every branch lists (others still
		# download those: a branch's new DLF.txt may not be committed and pushed yet), and what the checked-out commit lists
		cut -d' ' -f1 "$work/new" > "$work/kept"
		names branches/ > "$work/lists"
		names set >> "$work/lists"
		while IFS= read -r key; do
			get "$key" "$work/other"
			cut -d' ' -f1 "$work/other" >> "$work/kept"
		done < "$work/lists"
		{
			git show HEAD:DLF.txt 2> /dev/null
			git for-each-ref --format='%(refname)' refs/remotes | while read -r ref; do git rev-parse -q --verify "$ref:DLF.txt"; done | sort -u | while read -r past; do git cat-file blob "$past"; done
		} | cut -d' ' -f1 >> "$work/kept"
		sort -u "$work/kept" -o "$work/kept"
		sort -u "$work/there" | comm -23 - "$work/kept" | while read -r hash; do
			r2 DELETE "files/$hash" > /dev/null
		done
	fi
	r2 DELETE lock > /dev/null
	rm -f "$work/old" "$work/new" "$work/other" "$work/lists" "$work/there" "$work/kept" "$work/tmp"
	trap 'rm -rf "$work"' EXIT
	echo "DLF: uploaded. Commit DLF.txt with the changes that need these files." >&2
}
