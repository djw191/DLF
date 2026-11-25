#!/bin/bash
# End-to-end test of DLF's shell side against a stand-in bucket (fakes3.py). Unity never imports a folder whose name
# ends in ~, so this stays out of every project that uses the package.
# Needs git, curl and python. Usage: bash Tests~/dlftest.sh [folder with the DLF scripts] [empty work folder]
# The scripts default to the package's own Hooks~ folder; any other folder, such as the copy Unity downloaded, can be given
HOOKS=${1:-$(cd "$(dirname "$0")/../Hooks~" && pwd)}; T=${2:-$(mktemp -d)}; PORT=18765
rm -rf "$T"; mkdir -p "$T/bucket" "$T/appdata/DLF"
python "$(dirname "$0")/fakes3.py" "$T/bucket" $PORT & SERVER=$!
sleep 1
export APPDATA="$T/appdata"
printf 'accessKeyId=x\nsecretAccessKey=y\n' > "$APPDATA/DLF/b.cfg"
git init -q --bare "$T/remote.git"
O="$T/bucket/b__proj__"   # where the project's objects are in the stand-in bucket
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1 (got '$2', wanted '$3')"; fi; }
stored() { ls "$T/bucket" | grep -c "^b__proj__files__"; }
same() { cmp -s "$1" "$2" && echo same || echo differs; }
has() { [ -e "$1" ] && echo there || echo gone; }
dlf() { sh .githooks/dlf "$@"; }
status() { dlf status 2>/dev/null | grep -E "^($1)=" | sort | tr '\n' ' '; }

mk() { # a checkout with the hooks on
	git init -q -b main "$T/$1"; cd "$T/$1"; git config core.hooksPath .githooks; git config user.name $1; git config user.email $1@t; git config core.autocrlf false
	git remote add origin "$T/remote.git"
}
clone() { # clone NAME BRANCH: another checkout of what is pushed
	git clone -q -c core.autocrlf=false -b $2 "$T/remote.git" "$T/$1" 2>/dev/null; cd "$T/$1"; git config core.hooksPath .githooks; git config user.name $1; git config user.email $1@t
}
share() { # share MESSAGE: uploads the changed large files and commits and pushes DLF.txt, the way a person would
	dlf upload > /dev/null 2>&1 || return 1
	echo "$1" > code.cs; git add code.cs DLF.txt; git commit -q -m "$1" 2>/dev/null; git push -q origin HEAD 2>/dev/null
}
mk a; mkdir -p .githooks Big/sub; cp "$HOOKS"/* .githooks/
printf "endpoint=http://127.0.0.1:$PORT\nbucket=b\nproject=proj\npattern=Big/*\npattern=Big/sub/*.dat\n" > DLF.cfg; printf 'Big/\n' > .gitignore
echo one > Big/a.bin; echo two > "Big/b with space.bin"; echo dot > Big/.hidden; echo deep > Big/sub/d.dat; echo not-shared > Big/sub/skip.txt; echo c > code.cs; git add code.cs .gitignore DLF.cfg .githooks

echo "--- 1 first upload"
git commit -q -m first 2>/dev/null; check "a commit is never stopped" $? 0
check "and never writes DLF.txt" "$(has DLF.txt)" gone
check "status lists the files as added" "$(status changed | grep -c 'changed=A')" 1
dlf upload > /dev/null 2>&1; check "upload succeeds" $? 0
check "DLF.txt lists the 4 shared files" "$(wc -l < DLF.txt | tr -d ' ')" 4
check "with their paths, a space included" "$(grep -c ' Big/b with space.bin$' DLF.txt)" 1
check "a file outside the patterns is not listed" "$(grep -c skip.txt DLF.txt)" 0
check "DLF.txt is left for the author to commit" "$(git status --short DLF.txt | cut -c1-2)" "??"
check "bucket holds the 4 files" "$(stored)" 4
check "the branch's list in the bucket is DLF.txt" "$(same "${O}branches__main" DLF.txt)" same
H=$(head -1 DLF.txt | cut -d' ' -f1); check "a stored file unzips to its own hash" "$(gzip -dc "${O}files__$H" | sha256sum | cut -c1-64)" "$H"
check "the lock is released" "$(has "${O}lock")" gone
check "the key file handed to curl is gone" "$(has .git/dlf/key)" gone
check "only the three markers are kept on this machine" "$(ls .git/dlf | tr '\n' ' ')" "seen synced uploaded "
check "status: DLF.txt to commit" "$(status committed)" "committed=no "
git add DLF.txt; git commit -q -m list 2>/dev/null; check "status: DLF.txt to push" "$(status pushed)" "pushed=no "
git push -q -u origin main 2>/dev/null; check "commit and push go through" $? 0
check "status: nothing to push" "$(status pushed)" ""
check "status: nothing changed, list committed, bucket has it" "$(status 'branch|committed|uploaded|key|changed|incoming')" "branch=main committed=yes key=write uploaded=yes "

echo "--- 1b files deleted after an upload"
rm Big/a.bin "Big/b with space.bin"
check "status lists them as deleted here" "$(status changed | grep -c 'changed=D')" 1
dlf download > /dev/null 2>&1; check "a download leaves that alone" "$(has Big/a.bin)" gone
dlf download --missing > /dev/null 2>&1; check "the Download button brings missing files back" "$(cat Big/a.bin)$(cat 'Big/b with space.bin')" onetwo
rm Big/a.bin "Big/b with space.bin"
dlf reset > /dev/null 2>&1; check "reset succeeds, with a DLF.txt that is not committed" $? 0
check "and brings the deleted files back" "$(cat Big/a.bin)$(cat 'Big/b with space.bin')" onetwo
check "status: nothing left" "$(status 'changed|incoming')" ""
sleep 1

echo "--- 2 the hooks only remind"
echo c2 > code.cs; git add code.cs; check "no reminder when nothing changed" "$(git commit -m code 2>&1 | grep -c DLF:)" 0
sleep 1; echo touched > Big/a.bin; echo c2b > code.cs; git add code.cs
check "a commit with changed files that are not uploaded gets a reminder" "$(git commit -m code2 2>&1 | grep -c 'not uploaded')" 1
check "and still goes through" "$(git log -1 --format=%s)" code2
check "DLF.txt is not touched by it" "$(git status --short DLF.txt | wc -l | tr -d ' ')" 0
echo one > Big/a.bin; dlf upload > /dev/null 2>&1
check "putting the file back and uploading changes nothing" "$(git status --short DLF.txt | wc -l | tr -d ' ')" 0
git push -q origin HEAD 2>/dev/null

echo "--- 3 a second checkout downloads"
clone b main; mkdir -p Big; echo stale > Big/a.bin; echo extra > Big/extra.bin
check "status lists what a download would bring" "$(status incoming | grep -c 'incoming=A')" 1
check "download names a file that was here already and is not the listed one" "$(dlf download 2>&1 | grep -c '^  Big/a.bin$')" 1
check "and leaves it alone, like a file the list does not name" "$(cat Big/a.bin)$(has Big/extra.bin)" stalethere
check "a file with a space arrives" "$(cat 'Big/b with space.bin')" two
check "a file in a subfolder arrives" "$(cat Big/sub/d.dat)" deep
dlf reset > /dev/null 2>&1; check "reset succeeds" $? 0
check "it replaces the changed file" "$(cat Big/a.bin)" one
check "and removes the file the list does not name" "$(has Big/extra.bin)" gone
check "a second download has nothing to do, without hashing" "$(dlf download 2>&1 | grep -c Hashing)" 0
check "status: nothing left" "$(status 'changed|incoming')" ""

echo "--- 4 a file changes and one is removed (a rebake)"
cd "$T/a"
OLD=$(grep ' Big/a.bin$' DLF.txt | cut -d' ' -f1)
sleep 1; echo changed > Big/a.bin; rm "Big/b with space.bin"; echo three > Big/c.bin
check "status lists each changed file: modified, deleted, added" "$(status changed)" "changed=A Big/c.bin changed=D Big/b with space.bin changed=M Big/a.bin "
dlf download > /dev/null 2>&1; check "a download leaves changes that are not uploaded alone" "$(cat Big/a.bin)" changed
dlf download --missing > /dev/null 2>&1; check "the Download button brings back the deleted file, and keeps the changed ones" "$(cat 'Big/b with space.bin')$(cat Big/a.bin)$(cat Big/c.bin)" twochangedthree
sleep 1; rm "Big/b with space.bin"
dlf upload > /dev/null 2>&1; check "upload succeeds" $? 0
check "git shows which files changed" "$(git diff --stat DLF.txt | grep -c 'DLF.txt')" 1
check "the branch's list is the new DLF.txt" "$(same "${O}branches__main" DLF.txt)" same
check "the file the pushed commit still lists stays in the bucket, for those who have not got the new commit" "$(has "${O}files__$OLD")" there
check "status: uploaded, DLF.txt not committed yet" "$(status 'committed|uploaded')" "committed=no uploaded=yes "
echo c3 > code.cs; git add code.cs DLF.txt; git commit -q -m rebake 2>/dev/null; git push -q origin HEAD 2>/dev/null; check "commit and push go through" $? 0
sleep 1; echo changed2 > Big/c.bin; share rebake2; check "a later upload succeeds" $? 0
check "and removes the file nothing lists any more" "$(has "${O}files__$OLD")" gone
check "bucket holds the 4 files of the list, and the one the last push still listed" "$(stored)" 5

echo "--- 5 reset"
cd "$T/b"; check "status: nothing to pull before a fetch" "$(status behind)" ""
git fetch -q origin; check "status: a newer DLF.txt to pull after it" "$(status behind)" "behind=yes "
git pull -q origin main 2>/dev/null; check "status: nothing to pull once pulled" "$(status behind)" ""
check "status lists what a download would bring" "$(status incoming)" "incoming=A Big/c.bin incoming=D Big/b with space.bin incoming=M Big/a.bin "
dlf download > /dev/null 2>&1; check "download of the new list succeeds" $? 0
check "the removed file is removed here too" "$(has 'Big/b with space.bin')" gone
sleep 1; echo edit1 > Big/a.bin; echo edit2 > Big/c.bin
dlf reset Big/a.bin > /dev/null 2>&1; check "reset of one file succeeds" $? 0
check "the selected file is reset" "$(cat Big/a.bin)" changed
check "another edited file is left alone" "$(cat Big/c.bin)" edit2
dlf reset > /dev/null 2>&1; check "reset of everything succeeds" $? 0
check "every file is the committed one again" "$(cat Big/c.bin)" changed2
check "DLF.txt is not changed by it" "$(git status --short DLF.txt | wc -l | tr -d ' ')" 0

echo "--- 6 branches"
cd "$T/a"; git checkout -q -b plain; git push -q origin plain 2>/dev/null; check "a new branch with the same files pushes" $? 0
check "and has no list of its own" "$(has "${O}branches__plain")" gone
clone p plain; dlf download > /dev/null 2>&1; check "a checkout of that branch still downloads: the files its commit lists are there" "$(cat Big/c.bin 2>/dev/null)" changed2
cd "$T/a"; git checkout -q -b feature/x main; sleep 1; echo feature > Big/c.bin; share feat; check "a branch with other files uploads its own set" $? 0
check "its list is in the bucket" "$(same "${O}branches__feature__x" DLF.txt)" same
check "main's list is untouched" "$(git show main:DLF.txt | cmp -s - "${O}branches__main" && echo same)" same
clone f feature/x; dlf download > /dev/null 2>&1; check "a checkout of the feature branch gets its file" "$(cat Big/c.bin 2>/dev/null)" feature
clone m main; dlf download > /dev/null 2>&1; check "a fresh checkout of main still gets main's" "$(cat Big/c.bin 2>/dev/null)" changed2
cd "$T/a"; git checkout -q main; dlf download > /dev/null 2>&1; check "switching back and downloading restores main's file" "$(cat Big/c.bin)" changed2

echo "--- 7 two people change the files of one branch"
sleep 1; echo a-new > Big/a.bin; dlf upload > /dev/null 2>&1; check "A uploads first" $? 0
cp DLF.txt "$T/a-list"
cd "$T/b"; sleep 1; echo b-new > Big/c.bin
check "B's upload is refused" "$(dlf upload 2>&1 | grep -c 'someone else uploaded')" 1
check "A's list is still the branch's" "$(same "${O}branches__main" "$T/a-list")" same
cd "$T/a"; echo c7 > code.cs; git add code.cs DLF.txt; git commit -q -m a-new 2>/dev/null; git push -q origin HEAD 2>/dev/null
check "the refused upload left B's DLF.txt alone" "$(git status --short DLF.txt | wc -l | tr -d ' ')" 0
cd "$T/b"; git pull -q origin main 2>/dev/null; dlf download > /dev/null 2>&1; check "B gets A's commit and downloads" $? 0
check "A's file arrives" "$(cat Big/a.bin)" a-new
check "B's own changed file is kept" "$(cat Big/c.bin)" b-new
check "status: only B's change is left" "$(status 'changed|incoming')" "changed=M Big/c.bin "
share b-new; check "then B uploads, without redoing anything" $? 0
check "the list has A's file and B's" "$(grep -c -e "^$(echo a-new | sha256sum | cut -c1-64) " -e "^$(echo b-new | sha256sum | cut -c1-64) " DLF.txt)" 2

echo "--- 7b a new DLF.txt arrives while files here are changed and not uploaded"
cd "$T/a"; git pull -q origin main 2>/dev/null; dlf download > /dev/null 2>&1; check "with nothing changed here, a download brings the new files" "$(cat Big/c.bin)" b-new
cd "$T/b"; sleep 1; echo b-newer > Big/c.bin; share b-newer
cd "$T/a"; sleep 1; echo mine > Big/a.bin; git pull -q origin main 2>/dev/null
dlf upload > /dev/null 2>&1; check "an upload straight after getting their commit succeeds" $? 0
check "it took their file first" "$(cat Big/c.bin)" b-newer
check "and kept the change made here" "$(cat Big/a.bin)" mine
check "so the list has both, and their file is not put back to the old one" "$(grep -c -e "^$(echo mine | sha256sum | cut -c1-64) " -e "^$(echo b-newer | sha256sum | cut -c1-64) " DLF.txt)" 2
echo c7b > code.cs; git add code.cs DLF.txt; git commit -q -m mine 2>/dev/null; git push -q origin HEAD 2>/dev/null

echo "--- 7c the same file changed on both sides"
cd "$T/b"; sleep 1; echo clash > Big/a.bin; echo b-side > Big/sub/d.dat; git pull -q origin main 2>/dev/null
check "download names the file" "$(dlf download 2>&1 | grep -c '^  Big/a.bin$')" 1
check "and leaves it, and the other change made here, alone" "$(cat Big/a.bin)$(cat Big/sub/d.dat)" clashb-side
check "status lists it as changed here and as waiting" "$(status 'changed|incoming')" "changed=M Big/a.bin changed=M Big/sub/d.dat incoming=M Big/a.bin "
dlf reset Big/a.bin > /dev/null 2>&1; check "reset of that file succeeds" $? 0
check "it takes the listed one, and leaves the other change alone" "$(cat Big/a.bin)$(cat Big/sub/d.dat)" mineb-side
check "status: the other change is still there" "$(status 'changed|incoming')" "changed=M Big/sub/d.dat "
dlf reset > /dev/null 2>&1; check "reset of everything puts that back too" "$(cat Big/sub/d.dat)" deep

echo "--- 7d a file copied in with an older date, and two commands at once"
cd "$T/a"; git pull -q origin main 2>/dev/null; dlf download > /dev/null 2>&1
echo older-copy > "$T/older"; touch -d '2020-01-01' "$T/older"; cp -p "$T/older" Big/c.bin
check "status sees a file that was replaced by an older one" "$(status changed)" "changed=M Big/c.bin "
check "a download fails when a file cannot be had, and removes nothing" "$(H=$(grep ' Big/a.bin$' DLF.txt | cut -d' ' -f1); mv "${O}files__$H" "$T/held"; cp DLF.txt "$T/list"; grep -v ' Big/sub/d.dat$' "$T/list" | sed "s/^[0-9a-f]* Big\/a.bin$/$H Big\/a.bin/" > DLF.txt; rm Big/a.bin; dlf reset > /dev/null 2>&1; echo $?; has Big/sub/d.dat; mv "$T/held" "${O}files__$H"; cp "$T/list" DLF.txt)" "1
there"
CHECKS=; for i in 1 2 3 4 5 6; do dlf status local > /dev/null 2>&1 & CHECKS="$CHECKS $!"; done; dlf reset > /dev/null 2>&1; check "a reset beside six status checks succeeds" $? 0; wait $CHECKS
check "and leaves the files right" "$(cat Big/c.bin)$(status 'changed|incoming')" "b-newer"
check "nothing but the markers is left behind" "$(ls .git/dlf | tr '\n' ' ')" "seen synced uploaded "

echo "--- 8 a push that lists files nobody uploaded"
cd "$T/a"; git pull -q origin main 2>/dev/null; dlf download > /dev/null 2>&1
echo "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 Big/made-up.bin" >> DLF.txt; git add DLF.txt; git commit -q -m bogus 2>/dev/null
check "the push gets a reminder" "$(git push origin HEAD 2>&1 | grep -c 'not uploaded for it')" 1
check "and still goes through" "$(git -C "$T/remote.git" rev-parse main)" "$(git rev-parse HEAD)"
git checkout -q HEAD~1 -- DLF.txt; git commit -q -m unbogus 2>/dev/null; check "no reminder for a push that lists what the bucket has" "$(git push origin HEAD 2>&1 | grep -c 'DLF:')" 0

echo "--- 9 a checkout that never took a list"
clone c main; mkdir -p Big; echo stale > Big/a.bin; cp DLF.txt "$T/c-list"; echo cc > codec.cs; git add codec.cs
check "its commits get no reminder" "$(git commit -m c-code 2>&1 | grep -c DLF:)" 0
check "DLF.txt left alone" "$(same DLF.txt "$T/c-list")" same
git push -q origin HEAD 2>/dev/null

echo "--- 10 a list that points outside the project"
echo "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 ../escaped.bin" >> DLF.txt
check "download refuses it" "$(dlf download 2>&1 | grep -c 'outside the project')" 1
check "nothing was written outside" "$(has ../escaped.bin)" gone
git checkout -q -- DLF.txt
echo "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 ..\escaped.bin" >> DLF.txt
check "the same with a backslash" "$(dlf download 2>&1 | grep -c 'outside the project')$(has ../escaped.bin)" 1gone
git checkout -q -- DLF.txt
E=e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
echo "$E codec.cs" >> DLF.txt; check "download refuses a file git keeps" "$(dlf download 2>&1 | grep -c 'git keeps: codec.cs')" 1
check "and leaves it alone" "$(git status --short codec.cs | wc -l | tr -d ' ')" 0
git checkout -q -- DLF.txt; echo "$E .git/hooks/post-checkout" >> DLF.txt; check "download refuses git's own folder" "$(dlf download 2>&1 | grep -c "git's own folder")$(has .git/hooks/post-checkout)" 1gone
git checkout -q -- DLF.txt; echo "$E Big/.GIT/x" >> DLF.txt; check "whatever the case of its name" "$(dlf download 2>&1 | grep -c "git's own folder")" 1
git checkout -q -- DLF.txt; echo "abc123 Big/short.bin" >> DLF.txt; check "and a line without a whole hash" "$(dlf download 2>&1 | grep -c 'not a list of files')" 1
git checkout -q -- DLF.txt
printf '<<<<<<< HEAD\n' >> DLF.txt
check "a DLF.txt with a git merge conflict in it says so" "$(dlf download 2>&1 | grep -c 'merge conflict')" 1
git checkout -q -- DLF.txt

echo "--- 10b an upload from one branch keeps what another branch's pushed commit lists"
cd "$T/a"; git pull -q origin main 2>/dev/null; dlf download > /dev/null 2>&1
PUSHED=$(grep ' Big/a.bin$' DLF.txt | cut -d' ' -f1)
sleep 1; echo unpushed > Big/a.bin; dlf upload > /dev/null 2>&1   # main's list in the bucket is replaced, DLF.txt is not pushed yet
clone e main; dlf download > /dev/null 2>&1; git checkout -q -b other
sleep 1; echo other1 > Big/a.bin; share other1; sleep 1; echo other2 > Big/a.bin; share other2; check "two uploads from another branch succeed" $? 0
check "the file main's pushed commit lists is still in the bucket" "$(has "${O}files__$PUSHED")" there
cd "$T/a"; git checkout -q -- DLF.txt; dlf reset > /dev/null 2>&1; check "so main can still go back to its pushed files" "$(sha256sum < Big/a.bin | cut -c1-64)" "$PUSHED"

echo "--- 10c the list of a merged and deleted branch is removed"
git pull -q origin other 2>/dev/null; dlf download > /dev/null 2>&1; git push -q origin main 2>/dev/null; git push -q origin --delete other 2>/dev/null
echo "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 Big/ghost.bin" > "${O}branches__ghost"
check "before the next upload it is still there" "$(has "${O}branches__other")" there
sleep 1; echo after-merge > Big/a.bin; share after-merge; check "the next upload succeeds" $? 0
check "and removes the list of the branch that is gone" "$(has "${O}branches__other")" gone
check "a list nobody committed stays" "$(has "${O}branches__ghost")" there
check "the list of a branch that still exists stays" "$(has "${O}branches__feature__x")" there
rm -f "${O}branches__ghost"

echo "--- 11 keeping old versions"
cd "$T/a"; git pull -q origin main 2>/dev/null; dlf download > /dev/null 2>&1
echo "keepOldVersions=true" >> DLF.cfg; git add DLF.cfg; git commit -q -m keep 2>/dev/null; git push -q origin HEAD 2>/dev/null
KEPT=$(grep ' Big/a.bin$' DLF.txt | cut -d' ' -f1); PAST=$(git rev-parse HEAD)
sleep 1; echo newer > Big/a.bin; share newer; sleep 1; echo newest > Big/a.bin; share newest; check "uploads with keepOldVersions succeed" $? 0
check "a file two versions back stays in the bucket" "$(has "${O}files__$KEPT")" there
git checkout -q $PAST 2>/dev/null; dlf download > /dev/null 2>&1; check "so an older commit can still download its file" "$(cat Big/a.bin)" after-merge
git checkout -q main 2>/dev/null; dlf download > /dev/null 2>&1

echo "--- 12 one set for all branches"
sed -i 's/^keepOldVersions=true$/oneSetForAllBranches=true/' DLF.cfg; git add DLF.cfg; git commit -q -m one-set 2>/dev/null; git push -q origin HEAD 2>/dev/null
sleep 1; echo shared1 > Big/a.bin; share shared1; check "an upload from main succeeds" $? 0
check "it goes to the one shared list" "$(same "${O}set" DLF.txt)" same
git checkout -q -b side; sleep 1; echo shared2 > Big/a.bin; share shared2; check "an upload from another branch succeeds" $? 0
check "it replaces the same shared list" "$(same "${O}set" DLF.txt)" same
check "and has no list of its own" "$(has "${O}branches__side")" gone
git checkout -q -b apart main 2>/dev/null; sleep 1; echo apart > Big/a.bin
dlf upload > /dev/null 2>&1; check "a branch that lacks the last uploader's commit uploads too" $? 0
git checkout -q -- DLF.txt; git checkout -q side 2>/dev/null; dlf reset > /dev/null 2>&1; dlf upload > /dev/null 2>&1; git branch -q -D apart
check "and the set can be put back" "$(same "${O}set" DLF.txt)$(cat Big/a.bin)" sameshared2
git checkout -q main 2>/dev/null; git merge -q side 2>/dev/null; dlf download > /dev/null 2>&1; git push -q origin HEAD 2>/dev/null
cd "$T/c"; git pull -q origin main 2>/dev/null

echo "--- 13 the lock"
printf 'someone|%s|main' "$(date +%s)" > "${O}lock"
check "a download waits while someone uploads" "$(dlf download 2>&1 | grep -c 'someone is uploading')" 1
check "the local files are untouched" "$(cat Big/a.bin)" stale
check "status shows who" "$(status lock)" "lock=someone is uploading main "
printf 'someone|%s|main' "$(($(date +%s) - 7200))" > "${O}lock"
dlf reset > /dev/null 2>&1; check "a lock left behind two hours ago is ignored" $? 0
check "and the files arrive" "$(cat Big/a.bin)" shared2
printf 'someone|%s|main' "$(date +%s)" > "${O}lock"
dlf unlock > /dev/null 2>&1; check "unlock clears a lock" "$(has "${O}lock")" gone
printf 'someone|%s|main' "$(($(date +%s) - 7200))" > "${O}lock"
sleep 1; echo over-old-lock > Big/a.bin; share over-old-lock; check "an upload replaces a lock left behind" $? 0
check "and releases its own" "$(has "${O}lock")" gone
(. .githooks/dlf.sh; use_write_key; echo x > "$state/t"; put locktest "$state/t" -H 'If-None-Match: *' 2>/dev/null; put locktest "$state/t" -H 'If-None-Match: *' 2>/dev/null; echo $? > "$T/second"; rm -f "$state/t")
check "of two uploaders taking the lock, the second is refused" "$(cat "$T/second")" 22; rm -f "${O}locktest"

echo "--- 13b one run at a time, one content under two names, unchanged files not read again"
cd "$T/c"; sleep 30 & SLEEPER=$!
mkdir -p .git/dlf/busy; echo $SLEEPER > .git/dlf/busy/pid
check "a reset waits while another run goes on this machine" "$(dlf reset 2>&1 | grep -c 'already downloading or uploading')" 1
kill $SLEEPER 2>/dev/null; wait $SLEEPER 2>/dev/null
dlf reset > /dev/null 2>&1; check "the mark of a run that ended is taken over" $? 0
check "and let go of afterwards" "$(has .git/dlf/busy)" gone
mkdir -p .git/dlf/busy; echo 999999 > .git/dlf/busy/pid; printf '%s|%s|main' "c@$(uname -n)" "$(date +%s)" > "${O}lock"
dlf release > /dev/null 2>&1; check "release lets go of the mark and of this machine's own lock" "$(has .git/dlf/busy)$(has "${O}lock")" gonegone
printf 'someone|%s|main' "$(date +%s)" > "${O}lock"; dlf release > /dev/null 2>&1; check "but not of someone else's lock" "$(has "${O}lock")" there; rm -f "${O}lock"
sleep 1; echo twin > Big/t1.bin; echo twin > Big/t2.bin; share twins; rm Big/t1.bin Big/t2.bin
dlf reset > /dev/null 2>&1; check "one content listed under two names arrives under both" "$(cat Big/t1.bin)$(cat Big/t2.bin)" twintwin
printf 'twix\n' > "$T/look-alike"; touch -r Big/t1.bin "$T/look-alike"; cp -p "$T/look-alike" Big/t1.bin
sleep 1; echo moved-on > Big/c.bin; share moved-on
check "a file with the same size and time keeps its hash: it is not read again" "$(grep ' Big/t1.bin$' DLF.txt | cut -c1-64)" "$(echo twin | sha256sum | cut -c1-64)"
check "a changed file gets its new hash" "$(grep ' Big/c.bin$' DLF.txt | cut -c1-64)" "$(echo moved-on | sha256sum | cut -c1-64)"
rm Big/t1.bin; dlf reset > /dev/null 2>&1
sleep 1; echo picked > Big/a.bin; echo unpicked > Big/c.bin
dlf upload Big/a.bin > "$T/out" 2>&1; check "an upload of the ticked files only succeeds" $? 0
check "it says what it uploads, and how big" "$(grep -c '^PROGRESS 1 1 Uploading Big/a.bin (7 B)$' "$T/out")" 1
check "the ticked file is listed as it is here" "$(grep ' Big/a.bin$' DLF.txt | cut -c1-64)" "$(echo picked | sha256sum | cut -c1-64)"
check "the other keeps its old line" "$(grep ' Big/c.bin$' DLF.txt | cut -c1-64)" "$(echo moved-on | sha256sum | cut -c1-64)"
check "and is not in the bucket" "$(has "${O}files__$(echo unpicked | sha256sum | cut -c1-64)")" gone
check "status: the other is still changed here, nothing to download" "$(status 'changed|incoming')" "changed=M Big/c.bin "
check "the change stays on this machine" "$(cat Big/c.bin)" unpicked
dlf reset > /dev/null 2>&1; git checkout -q -- DLF.txt; dlf reset > /dev/null 2>&1

echo "--- 13c hooks and .gitignore"
git init -q -b main "$T/i"; cd "$T/i"; git config user.name i; git config user.email i@t; git config core.autocrlf false
mkdir -p "$T/pkg"; cp "$HOOKS"/* "$T/pkg/"; ins() { sh "$T/pkg/dlf" "$@"; }
printf "endpoint=http://127.0.0.1:$PORT\nbucket=b\nproject=proj\npattern=Big/*\npattern=Media/**/*.mp4\n" > DLF.cfg
START='# DLF begin: runs the DLF hook first (added by the DLF Unity package; delete down to "DLF end" to turn it off)'
printf '#!/bin/sh\n%s\nsh "$(git rev-parse --git-common-dir)/dlf-hooks/pre-commit" "$@" || exit 1\n# DLF end\necho mine >> "%s/ran"\n' "$START" "$T" > .git/hooks/pre-commit
ins install 2> /dev/null; check "install copies the scripts" "$(ls .git/dlf-hooks | tr '\n' ' ')" "dlf dlf.sh pre-commit pre-push "
check "it puts its block after the first line" "$(sed -n 2p .git/hooks/pre-commit)" "$START"
check "an older block is replaced, and the block cannot stop a commit" "$(grep -c '# DLF begin' .git/hooks/pre-commit)$(grep -c 'exit 1' .git/hooks/pre-commit)" 10
check "what the hook did is kept" "$(tail -n 1 .git/hooks/pre-commit)" "echo mine >> \"$T/ran\""
check "pre-push gets a block that hands on the pushed refs" "$(grep -c 'DLF_REFS' .git/hooks/pre-push)" 2
cp .git/hooks/pre-commit "$T/once"; ins install 2> /dev/null; check "a second install changes nothing" "$(cmp -s .git/hooks/pre-commit "$T/once" && echo same)" same
rm -rf .git/dlf-hooks; echo x > x.txt; git add x.txt; git commit -q -m x 2> /dev/null; check "a commit goes through when the DLF hook is gone" $? 0
check "and the rest of the hook still runs" "$(cat "$T/ran")" mine
git config core.hooksPath elsewhere; check "a repository with its hooks elsewhere is left alone, with a note" "$(ins install 2>&1 | grep -c hooksPath)" 1; git config --unset core.hooksPath
printf 'Library/\r\n\r\n' > .gitignore
ins ignore; check ".gitignore lists the patterns" "$(tr -d '\r' < .gitignore | tr '\n' '|')" "Library/||# DLF: shared through the bucket instead of git (written by Tools/DLF/Settings, edit the patterns there)|/Big/*|/Media/**/*.mp4|# DLF end|"
check "with the line endings it had" "$(grep -c $'\r$' .gitignore)" 6
sed -i '/Media/d' DLF.cfg; ins ignore; check "a changed pattern replaces the block" "$(grep -c Media .gitignore)$(grep -c '# DLF: shared' .gitignore)$(head -n 1 .gitignore | tr -d '\r')" "01Library/"
cp .gitignore "$T/once"; ins ignore; check "a second run changes nothing" "$(cmp -s .gitignore "$T/once" && echo same)" same
cd "$T/c"

echo "--- 14 no network"
kill $SERVER 2>/dev/null; sleep 1
rm -f .git/dlf/synced; echo local > Big/a.bin; dlf download > /dev/null 2>&1; check "download fails instead of treating the bucket as empty" $? 1
check "the local files are untouched" "$(cat Big/a.bin)" local
check "status says the bucket cannot be reached, and still reports this machine" "$(status 'bucket|committed')" "bucket=unreachable committed=yes "
echo cn > codec.cs; git add codec.cs; git commit -q -m offline 2>/dev/null; check "a commit still goes through" $? 0
