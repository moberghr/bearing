# Git Workflow Rules (§8.x)

## §8.1 — Commits are opt-in
- Do NOT `git commit` or `git push` unless the user asks. Work is left in the working tree for review.
- When asked to commit off the default branch, create a feature branch first.

## §8.2 — Commit messages
- Match the existing style: short, lowercase, imperative summary describing the behavior change
  (e.g. `add roadmap`, `include version in about`, `Keybindings Phase 3: command palette`). No PR template
  exists in this repo.

## §8.3 — Main branch
- Feature work lands on `main` (historical branches like `keybindings-overhaul`, `editor-4a-redesign`,
  `grid-view` were merged and deleted). Prefer a short-lived branch for non-trivial work when asked to commit.
