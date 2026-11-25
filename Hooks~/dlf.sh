# DLF (Derek's Large Files), shared by pre-commit and pre-push: publishing the large files a project keeps out of git.
# What is shared and where is in DLF.cfg, at the top of the repository. Fetching is done by Unity: Editor/DLF.cs in this package
cfg=DLF.cfg

value() { sed -n "s/^$1=//p" "$2" 2>/dev/null | tr -d '\r' | head -n 1; }

# The write key is kept per machine and per bucket, outside the repository
keys="${APPDATA:-$HOME/.config}/DLF/$(value bucket "$cfg").cfg"
# What this checkout last fetched or published; Unity reads and writes it too
state="$(git rev-parse --git-dir)/dlf"

# shortcut: a shared file with a space in its path is not supported, quote the expansion below if one ever appears
shared_files() {
	for file in $(sed -n 's/^pattern=//p' "$cfg" | tr -d '\r'); do
		[ -f "$file" ] && echo "$file"
	done | LC_ALL=C sort -u
}

# Cheap sign that the shared files changed: names, sizes and dates
fingerprint() { shared_files | xargs -r stat -c '%n %s %Y' | sha256sum | cut -c1-64; }

hash_of() { [ -f "$1" ] && sha256sum < "$1" | cut -c1-64; }

# The hash of the list of the shared files as they are now. The list ("hash path" per file, "$state/index") is only
# rebuilt when the files changed, which takes a while for gigabytes
current_set() {
	now=$(fingerprint)
	if [ "$now" != "$(cat "$state/fingerprint" 2>/dev/null)" ]; then
		echo "DLF: checking the shared large files..." >&2
		shared_files | xargs -r sha256sum | sed 's/^\([0-9a-f]*\) [ *]/\1 /' > "$state/index"
		printf '%s' "$now" > "$state/fingerprint"
	fi
	hash_of "$state/index"
}

# Makes DLF.txt name the files as they are now. Unity is told too, or it would fetch the old set back over them
name_current() {
	printf '%s' "$1" > DLF.txt
	printf '%s' "$1" > "$state/synced"
}

# r2 METHOD NAME [curl options]: a signed request to the bucket with this machine's write key
r2() {
	method=$1
	name=$2
	shift 2
	curl -sS -f --aws-sigv4 "aws:amz:auto:s3" --user "$(value accessKeyId "$keys"):$(value secretAccessKey "$keys")" \
		-X "$method" "$@" "$(value endpoint "$cfg")/$(value bucket "$cfg")/$name"
}
