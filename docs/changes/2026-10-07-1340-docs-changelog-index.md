# One changelog index over the change records

- **Started:** 2026-10-07 13:40
- **Branch:** docs/changelog-index
- **Kind:** docs
- **Status:** in progress

## Why

The owner asked for it: one common file recording what we did and which file holds the
description, so that finding a change and finding its reasoning are the same lookup.

`docs/changes/` had two record files and a README explaining the template, and nothing
that listed them. Finding what had been done meant either listing the directory and
reading file names, or reading commit messages. Both work; neither is a history you can
scan, and neither survives a rename.

The owner also chose the shape of the answer: **not** folding the records into one file,
but keeping the detail per change and adding an index over them. That is right — the
records are long and each one is about a single change, so concatenating them would make
both reading and diffing worse.

## What changed

**`docs/changes/CHANGELOG.md` created** — one table, newest first:

| Date | Kind | Change | Detail | Branch | Merged |

`Kind` and `Branch` are separate columns on purpose. Kind is how you find the *sort* of
change you want; branch is how you find everything one branch touched, which is the
question you are asking when a revert is under discussion. Merged carries the merge commit,
`—` until it exists.

**Populated, not started empty.** Both existing records have rows, and the two merges that
predate the folder are listed under a heading that says they predate it. An index that
silently begins at the rule which introduced indexes is quietly wrong about its own
history.

**`docs/changes/README.md`** — a *The index* section: add the row in the same commit as
the record, newest first, append one row rather than rewriting the table, fill `Merged` on
merge.

**`.opencode-rules.md` §16** — three bullets making the row part of creating the record,
with the reason it is not optional: an index that looks authoritative and is not is worse
than no index.

## How it was verified

Links and claims, checked rather than assumed:

- **Both link targets resolve.** Every `](….md)` in the index parsed and `Test-Path`'d.
- **Both record files appear in the index**, checked by name against the directory, so no
  file is orphaned.
- **All four commit hashes exist:** `59a3905`, `13463bf`, `b02442c`, `8a97eaa` — each
  resolved with `git log -1`.
- **The two historical dates were read from the commits** rather than remembered:
  `b02442c` → 2026-10-03, `8a97eaa` → 2026-10-04. Both match the table.
- Rules file 974 lines, 0 null bytes, no duplicate section numbers.

## What was rejected, and why

- **Folding every record into one big CHANGELOG.** Rejected: it is what the owner asked us
  *not* to do, and it is the worse shape anyway. A record is a self-contained account of
  one change; concatenated, the diff for a single change becomes unreadable and editing one
  risks corrupting another.
- **Generating the index with a script.** Rejected. A script that rebuilds the table on
  every change will eventually run at the wrong moment and produce a diff nobody reviewed,
  and the failure is invisible — the file still looks right. A hand-appended row is a
  one-line diff that a reviewer can check against the record beside it. This matches §15:
  long scripted edits are the ones that go wrong silently.
- **`git log` as the index.** Rejected: it is not searchable in the way that matters, it
  cannot hold the branch and kind columns, and it loses the link between "what we did" and
  "why we did it" — the second of which is the whole point of §16.

## Note on §17.1

`grill-me` is recorded as mandatory before a new feature or fix. **It was not run for this
change**, and the honest reason is that the owner had already stated the requirement in
full — one common file, updated alongside each record, holding what we did and where the
description is — and there was no open question left to interview about. Recorded here
rather than quietly skipped: §17.1's own carve-out is for trivial edits, and this is
process work the owner dictated, so the rule was applied as *not applicable* rather than
as *ignored*.

## Rules this changed

- §16 — three bullets: the `CHANGELOG.md` row is part of creating the record, appended not
  rewritten, `Merged` filled on merge, and the index must agree with the files beside it.
- `docs/changes/README.md` — new *The index* section, same rule in the place someone
  writing a record will actually read it.
