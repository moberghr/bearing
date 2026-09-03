#!/usr/bin/env bash
#
# One platform of a release, called by .github/workflows/release.yml on the runner that targets it.
#
# It exists so the two platform jobs share one body rather than two copies of it — a Windows job and a
# Linux job that drifted apart would ship a Setup.exe and an AppImage built from different rules.
#
# Expects, from the workflow: TAG, VERSION, RID, GH_TOKEN, and PRERELEASE (0 or 1).
set -euo pipefail

: "${TAG:?}" "${VERSION:?}" "${RID:?}" "${GH_TOKEN:?}" "${PRERELEASE:?}"

# --tool-path rather than `dotnet tool install -g`, and put on PATH here rather than through GITHUB_PATH:
# the global tools directory is $HOME/.dotnet/tools on Linux and %USERPROFILE%\.dotnet\tools on Windows, and
# a PATH entry that has to be right in both Git Bash and the Windows runner is a thing to get wrong once per
# platform. $PWD is the repo either way.
export PATH="$PWD/.tools:$PATH"

# The release description is the release notes. Fetched from the API rather than read out of the event
# payload: `${{ github.event.release.body }}` spliced into a shell script is an injection hole on a public
# repo, and this also picks up an edit made between publishing and this job running.
#
# `|| :` because a workflow_dispatch can name a tag with no release yet — then there is no body, the notes
# fall back to docs/release-notes/<version>.md or the commit log, and vpk creates the release.
NOTES_DIR="$PWD/artifacts"
NOTES="$NOTES_DIR/release-body.md"
mkdir -p "$NOTES_DIR"
: > "$NOTES"
gh release view "$TAG" --repo "$GITHUB_REPOSITORY" --json body --jq '.body // ""' > "$NOTES" || :

# Hand the release's own title back to `vpk upload --releaseName`, so merging assets into a release someone
# named "0.5.5 — the timestamp release" cannot quietly retitle it "Bearing 0.5.5".
TITLE="$(gh release view "$TAG" --repo "$GITHUB_REPOSITORY" --json name --jq '.name // ""' 2>/dev/null || :)"
[[ -n "$TITLE" ]] && export RELEASE_NAME="$TITLE"

if grep -q '[^[:space:]]' "$NOTES"; then
  echo "==> Notes: the release description ($(wc -l < "$NOTES") lines)"
  export RELEASE_NOTES_FILE="$NOTES"
  # The notes came *from* the release body, so writing them back could only mangle what a human typed —
  # markdown round-tripped through a file and `gh release edit` for no gain.
  export KEEP_RELEASE_BODY=1
else
  echo "==> The release has no description; velopack.sh will generate notes and set it."
fi

# SKIP_TESTS because the `test` job already ran the suite once, against a real PostgreSQL, before either
# platform started. Running it again per platform would double the wall clock to re-answer a settled
# question — and the Windows runner has no Postgres, so its copy would answer it more weakly.
PUBLISH=1 SKIP_TESTS=1 VERSION="$VERSION" RID="$RID" PRERELEASE="$PRERELEASE" bash build/velopack.sh
