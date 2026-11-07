#!/usr/bin/env bash
# Puts Gothic Godot on top of Todi's Gothic-Unity wonderbranch (all open PRs together), as branch `wonderbranch`:
# the Unity side is exactly the wonderbranch, the Godot side (Godot/, forks/, docs/wiki/, README.md) comes from
# main. Then the shared-core list is regenerated against the newer core and the Godot project is built.
#
#   Godot/tools/sync-wonderbranch.sh [--test] [--push]
#
# The wonderbranch's own history holds an editor crash dump and is never published: its tree goes in as a
# one-commit snapshot on Gothic-Unity main (at their merge base). Needs a clean working tree on Gothic Godot.
#   GOTHIC_UNITY  the Gothic-Unity checkout (default: ../Gothic-Unity next to this repository)
#   WONDER        the branch there (default: todis-magical-wonderbranch)
set -euo pipefail
cd "$(dirname "$0")/../.."
REPO="$PWD"
GOTHIC_UNITY="${GOTHIC_UNITY:-$(realpath "$REPO/../Gothic-Unity")}"
WONDER="${WONDER:-todis-magical-wonderbranch}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/opt/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
EMAIL="$(git config user.email)"
NAME="$(git config user.name)"

if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    echo "Gothic Godot has uncommitted changes - commit or stash them first." >&2
    exit 1
fi

# 1. The snapshot: the wonderbranch's tree with Gothic-Unity main (merge base) as its only parent.
TREE=$(git -C "$GOTHIC_UNITY" rev-parse "$WONDER^{tree}")
WONDER_SHA=$(git -C "$GOTHIC_UNITY" rev-parse --short "$WONDER")
BASE=$(git -C "$GOTHIC_UNITY" merge-base "$WONDER" origin/main)
SNAPSHOT=$(git -C "$GOTHIC_UNITY" -c user.email="$EMAIL" -c user.name="$NAME" commit-tree "$TREE" -p "$BASE" \
    -m "Wonderbranch snapshot (tree of local $WONDER $WONDER_SHA)")
git -C "$GOTHIC_UNITY" update-ref refs/heads/wonderbranch-snapshot "$SNAPSHOT"
git fetch -q "$GOTHIC_UNITY" wonderbranch-snapshot
echo "snapshot $SNAPSHOT of $WONDER $WONDER_SHA on main $(git -C "$GOTHIC_UNITY" rev-parse --short "$BASE")"

# 2. The branch: the snapshot's files, then the Godot side from main.
START=$(git rev-parse --abbrev-ref HEAD)
if git show-ref -q refs/heads/wonderbranch; then
    git checkout -q wonderbranch
else
    git checkout -q -b wonderbranch FETCH_HEAD
fi
git read-tree -u --reset FETCH_HEAD
git checkout main -- Godot forks docs/wiki README.md
if ! grep -q '^!forks/\*\*$' .gitignore; then
    printf '\n# Gothic-Godot: patches for forked upstream projects (ZenKitCS above is the symlink, not these)\n!forks/**\n' >> .gitignore
fi
git add -A

# 3. The shared core against the wonderbranch's scripts, and the Godot build.
python3 Godot/tools/core-files.py | tail -1
dotnet build Godot/GothicGodot.csproj -nologo -v q 2>&1 | grep -E " error |Build succeeded" | sort -u | head -5
git add -A
git -c user.email="$EMAIL" -c user.name="$NAME" commit -q -m "Gothic Godot on the wonderbranch ($WONDER $WONDER_SHA)

The Unity side is the wonderbranch snapshot $SNAPSHOT, the Godot side main $(git rev-parse --short main)." ||
    echo "nothing changed"

if [[ " $* " == *" --test "* ]]; then
    Godot/tools/run-tests.sh 1 && Godot/tools/run-tests.sh 2
fi
if [[ " $* " == *" --push "* ]]; then
    git push -q origin wonderbranch
fi
git checkout -q "$START"
echo "branch wonderbranch: $(git log -1 --format='%h %s' wonderbranch)"
