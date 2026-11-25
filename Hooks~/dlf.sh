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
# A file is kept while a list in the bucket names it, or a commit this machine knows as pushed for the branch, or the
# checked-out commit; and for good with keepOldVersions=true in DLF.cfg.
#
# On this machine only two markers are kept, both git ids of a list: $state/synced, the list the shared files matched
# when it was last written (files written after it, added or removed, are what "changed" means), and $state/uploaded,
# the list this machine last put in the bucket.
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
mkdir -p "$state"
hasher=sha256sum
command -v sha256sum > /dev/null 2>&1 || hasher="shasum -a 256"
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

# Prints the list of the shared files as they are now, "hash path" per line. Hashing gigabytes takes a while
local_list() {
	echo "DLF: checking the shared large files..." >&2
	# The marker written for this list is dated from before the hashing, so that a file written during it counts as changed
	: > "$state/stamp"
	shared_files > "$state/names"
	[ -s "$state/names" ] && xargs -0 $hasher < "$state/names" | sed 's/^\([0-9a-f]*\) [ *]/\1 /'
	rm -f "$state/names"
}

# The git id of a list read from the input, with line endings as git stores them
list_id() { tr -d '\r' | git hash-object --stdin; }

# True when the shared files are still the ones the marker's list names: same names, none written since
unchanged() {
	[ -f "$state/synced" ] || return 1
	git cat-file -e "$(cat "$state/synced")" 2> /dev/null || return 1
	[ "$(shared_files | tr '\0' '\n')" = "$(git cat-file blob "$(cat "$state/synced")" | cut -d' ' -f2-)" ] || return 1
	shared_files | tr '\0' '\n' | {
		while IFS= read -r file; do
			[ "$file" -nt "$state/synced" ] && exit 1
		done
		exit 0
	}
}

# Notes that the shared files are now the list on the input. The list is stored in git's own object store, where
# its content can be read back later
mark_synced() {
	tr -d '\r' | git hash-object -w --stdin > "$state/synced"
	[ -f "$state/stamp" ] && touch -r "$state/stamp" "$state/synced"
	rm -f "$state/stamp"
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
	(umask 077 && printf 'user = "%s:%s"\n' "$1" "$2" > "$state/key")
	trap 'rm -f "$state/key"' EXIT
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
signed() { curl -sS -f -K "$state/key" --aws-sigv4 "aws:amz:$region:s3" "$@"; }

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

# put NAME FILE: uploads a file as an object
put() { r2 PUT "$1" -H "x-amz-content-sha256: $($hasher < "$2" | cut -c1-64)" -T "$2" > /dev/null; }

# names PREFIX: the names of the project's objects that start with PREFIX, without the project's own name
names() {
	token=
	while :; do
		signed "$endpoint/$bucket?list-type=2&prefix=$project/$1${token:+&continuation-token=$token}" > "$state/page" || fail "could not list the bucket."
		tr '<' '\n' < "$state/page" | sed -n "s|^Key>$project/||p"
		# A long listing comes in pages
		token=$(tr '<' '\n' < "$state/page" | sed -n 's|^NextContinuationToken>||p' | head -n 1)
		[ -n "$token" ] || break
	done
	rm -f "$state/page"
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
	get lock "$state/lock"
	IFS='|' read -r owner since what < "$state/lock" || true
	rm -f "$state/lock"
	if [ -n "$since" ] && [ $(($(date +%s) - since)) -lt $lock_seconds ] && [ "$owner" != "$(git config user.name)" ]; then
		fail "$owner is uploading $what right now. Try again in a moment (Unlock in the DLF window clears a lock that was left behind)."
	fi
}

# True when the path is among the selected ones: the path itself, its .meta, or anything in a selected folder.
# With nothing selected, every path is
selected() {
	path=$1
	shift
	[ $# -eq 0 ] && return 0
	for choice; do
		case "$path" in "$choice" | "$choice.meta" | "$choice"/*) return 0 ;; esac
	done
	return 1
}

# apply LIST [PATH...]: makes the local shared files match the list in the file LIST, all of them or only the selected
# ones. Files it does not name are removed, files that differ from it are downloaded. Prints "PROGRESS done total path"
apply() {
	list=$1
	shift
	# The list must never reach outside the repository
	while read -r hash path; do
		case "$path" in "" | /* | ?:* | ../* | */../* | */..) fail "the list of shared files names a path outside the project: $path" ;; esac
	done < "$list"
	check_lock
	current_list | sort > "$state/have"
	while read -r hash path; do
		selected "$path" "$@" && printf '%s %s\n' "$hash" "$path"
	done < "$list" | sort > "$state/wanted"
	cut -d' ' -f2- "$state/wanted" | sort > "$state/wanted.paths"
	cut -d' ' -f2- "$state/have" | while IFS= read -r path; do
		selected "$path" "$@" && printf '%s\n' "$path"
	done | sort | comm -23 - "$state/wanted.paths" | while IFS= read -r path; do
		rm -f "$path"
	done
	comm -23 "$state/wanted" "$state/have" > "$state/needed"
	if [ -s "$state/needed" ]; then
		# Everything is downloaded and checked in a side folder first: a download that fails leaves the files as they were
		rm -rf "$state/in"
		mkdir -p "$state/in"
		total=$(wc -l < "$state/needed" | tr -d ' ')
		echo "PROGRESS 0 $total downloading"
		cut -d' ' -f1 "$state/needed" | sort -u | while read -r hash; do
			printf 'url = "%s/%s/%s/files/%s"\noutput = "%s/in/%s.gz"\n' "$endpoint" "$bucket" "$project" "$hash" "$state" "$hash"
		done > "$state/get"
		signed --parallel --parallel-max 8 -K "$state/get" 2> /dev/null || fail "some of the files DLF.txt lists are not in the bucket (any more), or it could not be reached. Nothing was changed."
		for packed in "$state"/in/*.gz; do
			gzip -dc "$packed" > "${packed%.gz}" || fail "a downloaded file is damaged."
			rm -f "$packed"
		done
		# Each file is named by its hash: one that does not match its name is not what was asked for
		printf '%s\0' "$state"/in/* | xargs -0 $hasher | sed 's/^\([0-9a-f]*\) [ *]/\1 /' | while read -r hash file; do
			[ "$hash" = "${file##*/}" ] || fail "a downloaded file does not match the list."
		done || exit 1
		done=0
		folder=
		while read -r hash path; do
			done=$((done + 1))
			echo "PROGRESS $done $total $path"
			case "$path" in */*) [ "${path%/*}" = "$folder" ] || { folder=${path%/*}; mkdir -p "$folder"; } ;; esac
			cp "$state/in/$hash" "$path"
		done < "$state/needed"
	fi
	rm -rf "$state/in" "$state/get" "$state/have" "$state/wanted" "$state/wanted.paths" "$state/needed" "$state/stamp"
	if [ $# -eq 0 ]; then
		mark_synced < "$list"
	else
		# Some files changed, not all: what they are now has to be worked out again
		rm -f "$state/synced"
	fi
}

# upload BRANCH: lists the shared files as they are now in DLF.txt and puts them in the bucket as BRANCH's set.
# New files go up first, then the branch's list, then files nothing needs any more are removed. DLF.txt is left for
# the author to commit. Stops when it must not replace what is there
upload() {
	branch=$1
	use_write_key
	current_list > "$state/new"
	[ -s "$state/new" ] || fail "nothing to upload: no file matches the patterns in DLF.cfg."
	id=$(list_id < "$state/new")
	if [ ! -f DLF.txt ] || [ "$(list_id < DLF.txt)" != "$id" ]; then
		cp "$state/new" DLF.txt
	fi
	mark_synced < "$state/new"
	get "$(branch_key "$branch")" "$state/old"
	old_id=$(list_id < "$state/old")
	if [ "$old_id" = "$id" ]; then
		echo "$id" > "$state/uploaded"
		rm -f "$state/old" "$state/new"
		echo "DLF: nothing to upload, the bucket already has these files for $branch." >&2
		return 0
	fi
	# What is there for the branch must be a list this checkout knows: one it uploaded itself, or one in the history
	# of the checked-out commit. Otherwise someone else's newer files would be replaced
	if [ -s "$state/old" ] && [ "$old_id" != "$(cat "$state/uploaded" 2> /dev/null)" ]; then
		known=$(git rev-list HEAD -- DLF.txt 2> /dev/null | while read -r past; do git rev-parse -q --verify "$past:DLF.txt"; done | grep -c "^$old_id$")
		[ "$known" -gt 0 ] || fail "not uploaded: someone else uploaded other files for $branch. Get their commit, download, redo your change on top, then try again."
	fi
	check_lock
	printf '%s|%s|%s' "$(git config user.name)" "$(date +%s)" "$branch" > "$state/lock"
	put lock "$state/lock" || fail "not uploaded: could not lock the bucket."
	rm -f "$state/lock"
	trap 'r2 DELETE lock > /dev/null 2>&1; rm -f "$state/key"' EXIT
	echo "DLF: uploading the large files of $branch..." >&2
	names files/ | sed 's|^files/||' | sort > "$state/there"
	total=$(wc -l < "$state/new" | tr -d ' ')
	done=0
	while read -r hash path; do
		done=$((done + 1))
		grep -qxF "$hash" "$state/there" && continue
		echo "PROGRESS $done $total $path"
		gzip -1 -c "$path" > "$state/tmp"
		put "files/$hash" "$state/tmp" || fail "not uploaded: could not upload $path."
		echo "$hash" >> "$state/there"
	done < "$state/new"
	put "$(branch_key "$branch")" "$state/new" || fail "not uploaded: could not upload the list."
	echo "$id" > "$state/uploaded"
	if [ "$keep" != true ]; then
		# Kept: what any list in the bucket names, what the commits pushed for this branch list (others still download
		# those until the new DLF.txt is committed and pushed), and what the checked-out commit lists
		cut -d' ' -f1 "$state/new" > "$state/kept"
		names branches/ > "$state/lists"
		names set >> "$state/lists"
		while IFS= read -r key; do
			get "$key" "$state/other"
			cut -d' ' -f1 "$state/other" >> "$state/kept"
		done < "$state/lists"
		{
			git show HEAD:DLF.txt 2> /dev/null
			git for-each-ref --format='%(refname)' "refs/remotes/*/$branch" | while read -r ref; do git show "$ref:DLF.txt" 2> /dev/null; done
		} | cut -d' ' -f1 >> "$state/kept"
		sort -u "$state/kept" -o "$state/kept"
		sort -u "$state/there" | comm -23 - "$state/kept" | while read -r hash; do
			r2 DELETE "files/$hash" > /dev/null
		done
	fi
	r2 DELETE lock > /dev/null
	rm -f "$state/old" "$state/new" "$state/other" "$state/lists" "$state/there" "$state/kept" "$state/tmp"
	trap 'rm -f "$state/key"' EXIT
	echo "DLF: uploaded. Commit DLF.txt with the changes that need these files." >&2
}
