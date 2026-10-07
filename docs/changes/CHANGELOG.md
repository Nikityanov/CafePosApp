# Change log — the index

One line per change, newest first. Each line points at the file that holds the detail.
This file exists so that "what did we do, and where is it written down" is one search
rather than a directory listing and a guess.

**Adding an entry is part of creating the change record**, in the same commit (§16). An
index that drifts from the files beside it is worse than no index — it looks authoritative
and is wrong. If you find a record here that is not on `develop`, the index is wrong, not
the record.

## Format

| Column | What goes in it |
|---|---|
| Date | `YYYY-MM-DD HH:MM` — the same stamp as the file name |
| Kind | `feat` / `fix` / `refactor` / `docs` / `chore` |
| Change | one line, the same as the record's `#` heading |
| Detail | a link to the file |
| Branch | the branch it was done on |
| Merged | the merge commit, or `—` while in progress |

Kind and branch are worth a column each: the first is how you find the *kind* of change
you are looking for, the second is how you find everything that branch touched, which is
the question you ask when a revert is being considered.

## Entries

| Date | Kind | Change | Detail | Branch | Merged |
|---|---|---|---|---|---|
| 2026-10-07 13:40 | docs | One changelog index over the change records | [record](2026-10-07-1340-docs-changelog-index.md) | `docs/changelog-index` | [`95a49e3`](https://github.com/Nikityanov/CafePosApp/commit/95a49e3) |
| 2026-10-07 13:05 | chore | Merge, then delete the branch | [record](2026-10-07-1305-chore-delete-branch-after-merge.md) | `chore/delete-branch-after-merge` | [`59a3905`](https://github.com/Nikityanov/CafePosApp/commit/59a3905) |
| 2026-10-07 12:22 | docs | One branch per feature, and a written record per change | [record](2026-10-07-1222-docs-agent-rules-and-change-records.md) | `docs/agent-rules-and-change-records` | [`13463bf`](https://github.com/Nikityanov/CafePosApp/commit/13463bf) |

## Earlier work, before records existed

Two merges in the history predate this folder, and they are listed here so the index is
honest about its own start rather than pretending the records began with the rule that
introduced them:

| Date | Kind | Change | Branch | Merged |
|---|---|---|---|---|
| 2026-10-03 | feat | Cash operations and drawer reconciliation | `feat/cash-operations-and-drawer-reconciliation` | `b02442c` |
| 2026-10-04 | chore | Two comments made self-contained | `chore/self-contained-comments` | `8a97eaa` |

The reasoning for both is in their commits, and in `docs/decisions/` where it was filed.
Nothing was reconstructed here that is not already in the history.
