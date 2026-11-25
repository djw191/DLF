# DLF (Derek's Large Files): the files a project keeps out of git live in an S3 bucket instead. This is all of its logic,
# shared by the git hooks (pre-commit, pre-push) and the dlf command that Unity runs (fetch, reset).
#
# DLF.cfg, at the top of the repository, says which files are shared and where. The bucket holds one set of them: each
# file gzipped and named by the SHA-256 of its content (files/<hash>), and an index that lists them, one "hash path" per
# line. A commit names the set it needs by the SHA-256 of that index, in DLF.txt.
#
# Needs only what git brings or POSIX has: sh, git, curl 7.75 or newer, gzip, and sha256sum or shasum.
cfg=DLF.cfg

value() { sed -n "s/^$1=//p" "$2" 2>/dev/null | tr -d '\r' | head -n 1; }

endpoint=$(value endpoint "$cfg")
endpoint=${endpoint%/}
bucket=$(value bucket "$cfg")
region=$(value region "$cfg")
region=${region:-auto}
# The write key is kept per machine and per bucket, outside the repository
keys="${APPDATA:-${XDG_CONFIG_HOME:-$HOME/.config}}/DLF/$bucket.cfg"
# What this checkout last fetched or published, and scratch files
state="$(git rev-parse --git-dir)/dlf"
hasher=sha256sum
command -v sha256sum > /dev/null 2>&1 || hasher="shasum -a 256"

fail() {
	echo "DLF: $*" >&2
	exit 1
}

hash_of() { [ -f "$1" ] && $hasher < "$1" | cut -c1-64; }

# The files the patterns match, sorted and NUL separated. Git does the matching, the same way on every system:
# * and ? stay within one folder or file name, ** crosses folders
shared_files() {
	patterns=$(sed -n 's/^pattern=//p' "$cfg" | tr -d '\r')
	[ -n "$patterns" ] || return 0
	printf '%s\n' "$patterns" | sed 's/^/:(top,glob)/' | tr '\n' '\0' | xargs -0 git ls-files --others --full-name -z -- | LC_ALL=C sort -z
}

# True when a shared file was written after the index was made
newer_than_index() {
	tr '\0' '\n' < "$state/list" | {
		while IFS= read -r file; do
			[ "$file" -nt "$state/index" ] && exit 0
		done
		exit 1
	}
}

# Prints the hash of the list of the shared files as they are now. The list ("hash path" per file, $state/index) is
# only rebuilt when files were added, removed or written since, because hashing gigabytes takes a while
current_set() {
	mkdir -p "$state"
	shared_files > "$state/list"
	if [ ! -f "$state/index" ] || ! cmp -s "$state/list" "$state/indexed" || newer_than_index; then
		echo "DLF: checking the shared large files..." >&2
		: > "$state/stamp"
		if [ -s "$state/list" ]; then
			xargs -0 $hasher < "$state/list" | sed 's/^\([0-9a-f]*\) [ *]/\1 /' > "$state/index"
		else
			: > "$state/index"
		fi
		# Dated from before the hashing started, so that a file written during it counts as newer
		touch -r "$state/stamp" "$state/index"
		cp "$state/list" "$state/indexed"
	fi
	hash_of "$state/index"
}

# Makes DLF.txt name the files as they are now, and notes that this checkout already has that set
name_current() {
	printf '%s' "$1" > DLF.txt
	printf '%s' "$1" > "$state/synced"
}

# Requests are signed with this key. It is handed to curl in a file only this user can read, not on the command line
use_key() {
	mkdir -p "$state"
	(umask 077 && printf 'user = "%s:%s"\n' "$1" "$2" > "$state/key")
	trap 'rm -f "$state/key"' EXIT
}

use_write_key() { use_key "$(value accessKeyId "$keys")" "$(value secretAccessKey "$keys")"; }

# This machine's own key if it has one, else the read key everyone shares
use_read_key() {
	if [ -f "$keys" ]; then
		use_write_key
	else
		[ -n "$(value readSecretAccessKey "$cfg")" ] || fail "DLF.cfg has no read key. Enter one in Tools > DLF > Settings."
		use_key "$(value readAccessKeyId "$cfg")" "$(value readSecretAccessKey "$cfg")"
	fi
}

# r2 METHOD NAME [curl options]: a signed request for one object of the bucket
r2() {
	method=$1
	name=$2
	shift 2
	curl -sS -f -K "$state/key" --aws-sigv4 "aws:amz:$region:s3" -X "$method" "$@" "$endpoint/$bucket/$name"
}

# Gets the bucket's index into $state/shared, empty when the bucket has none yet. Only "no index" counts as empty:
# any other failure stops, so that it never looks like there is nothing there
get_shared_index() {
	code=$(r2 GET index -o "$state/shared" -w '%{http_code}' 2> /dev/null)
	case "$code" in
		200) ;;
		404) : > "$state/shared" ;;
		*) fail "could not read the list of shared large files (answer $code). Try again." ;;
	esac
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

# apply [PATH...]: makes the local shared files match $state/shared, all of them or only the selected ones. Files it
# does not name are removed, files that differ from it are downloaded. Prints "PROGRESS done total path" as it goes
apply() {
	# The index comes from the bucket: it must never reach outside the repository
	while read -r hash path; do
		case "$path" in "" | /* | ?:* | ../* | */../* | */..) fail "the shared list names a path outside the project: $path" ;; esac
	done < "$state/shared"
	current_set > /dev/null
	while read -r hash path; do
		selected "$path" "$@" && printf '%s %s\n' "$hash" "$path"
	done < "$state/shared" | LC_ALL=C sort > "$state/wanted"
	cut -d' ' -f2- "$state/wanted" | LC_ALL=C sort > "$state/wanted.paths"
	tr '\0' '\n' < "$state/list" | while IFS= read -r path; do
		selected "$path" "$@" && printf '%s\n' "$path"
	done | LC_ALL=C sort | comm -23 - "$state/wanted.paths" | while IFS= read -r path; do
		rm -f "$path"
	done
	LC_ALL=C sort "$state/index" | comm -23 "$state/wanted" - > "$state/needed"
	if [ -s "$state/needed" ]; then
		# Everything is downloaded and checked in a side folder first: a download that fails leaves the files as they were
		rm -rf "$state/in"
		mkdir -p "$state/in"
		total=$(wc -l < "$state/needed" | tr -d ' ')
		echo "PROGRESS 0 $total downloading"
		cut -d' ' -f1 "$state/needed" | LC_ALL=C sort -u | while read -r hash; do
			printf 'url = "%s/%s/files/%s"\noutput = "%s/in/%s.gz"\n' "$endpoint" "$bucket" "$hash" "$state" "$hash"
		done > "$state/get"
		curl -sS -f -K "$state/key" --aws-sigv4 "aws:amz:$region:s3" --parallel --parallel-max 8 -K "$state/get" || fail "could not download the shared large files."
		for packed in "$state"/in/*.gz; do
			gzip -dc "$packed" > "${packed%.gz}" || fail "a downloaded file is damaged."
			rm -f "$packed"
		done
		# Each file is named by its hash: one that does not match its name is not what was asked for
		printf '%s\0' "$state"/in/* | xargs -0 $hasher | sed 's/^\([0-9a-f]*\) [ *]/\1 /' | while read -r hash file; do
			[ "$hash" = "${file##*/}" ] || fail "a downloaded file does not match the shared list."
		done || exit 1
		done=0
		folder=
		while read -r hash path; do
			done=$((done + 1))
			echo "PROGRESS $done $total $path"
			case "$path" in */*) [ "${path%/*}" = "$folder" ] || { folder=${path%/*}; mkdir -p "$folder"; } ;; esac
			cp "$state/in/$hash" "$path"
		done < "$state/needed"
		rm -rf "$state/in"
	fi
	rm -f "$state/get"
}

# After everything was made to match $state/shared: this checkout has that set, and it is the one last seen in the bucket
note_synced() {
	cp "$state/shared" "$state/index"
	cut -d' ' -f2- "$state/shared" | tr '\n' '\0' > "$state/indexed"
	hash_of "$state/shared" | tr -d '\n' > "$state/synced"
	cp "$state/synced" "$state/base"
}

# publish POINTER WHAT: puts the set POINTER names into the bucket, from this checkout's files, unless it is there already.
# WHAT (a branch, "this commit") names the set in messages. The bucket holds one set: new files go up, then the index,
# then the files only the old set used are removed. Stops when this checkout cannot or must not do it
publish() {
	pointer=$1
	use_write_key
	get_shared_index
	shared=$(hash_of "$state/shared")
	if [ "$shared" = "$pointer" ]; then
		printf '%s' "$pointer" > "$state/base"
		return 0
	fi
	[ "$(current_set)" = "$pointer" ] || fail "not published: $2 names large files that are not uploaded, and this checkout does not have them to upload. Publish from the checkout that made that commit."
	# The bucket must still hold the set this checkout last saw there, or someone else's newer set would be destroyed
	if [ -s "$state/shared" ] && [ "$shared" != "$(cat "$state/base" 2> /dev/null)" ]; then
		fail "not published: someone else published other large files since this checkout last matched the shared ones. Pull their changes, let Unity fetch (Tools > DLF > Fetch), redo your change on top, then try again."
	fi
	echo "DLF: publishing the large files $2 needs..." >&2
	cut -d' ' -f1 "$state/shared" | LC_ALL=C sort -u > "$state/old"
	cut -d' ' -f1 "$state/index" | LC_ALL=C sort -u > "$state/new"
	total=$(wc -l < "$state/index" | tr -d ' ')
	done=0
	while read -r hash path; do
		done=$((done + 1))
		grep -qxF "$hash" "$state/old" && continue
		# The dlf command reports progress for Unity's bar; a hook lists the files in the terminal
		if [ -n "$progress" ]; then echo "PROGRESS $done $total $path"; else echo "  $path" >&2; fi
		gzip -1 -c "$path" > "$state/tmp"
		r2 PUT "files/$hash" -H "x-amz-content-sha256: $(hash_of "$state/tmp")" -T "$state/tmp" > /dev/null || fail "not published: could not upload $path."
	done < "$state/index"
	r2 PUT index -H "x-amz-content-sha256: $pointer" -T "$state/index" > /dev/null || fail "not published: could not upload the list."
	comm -23 "$state/old" "$state/new" | while read -r hash; do
		r2 DELETE "files/$hash" > /dev/null
	done
	rm -f "$state/tmp"
	printf '%s' "$pointer" > "$state/base"
	echo "DLF: published." >&2
}
