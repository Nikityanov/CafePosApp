# Change records

One file per feature or fix, created on the feature branch and committed with it. A change
that is not recorded here did not happen as far as the next person — or the next agent —
is concerned.

## Naming

`docs/changes/YYYY-MM-DD-HHMM-<slug>.md`

Local time, 24-hour, zero-padded. The stamp sorts chronologically and no two entries can
collide, which matters because several changes are started in one session. The slug is
`feat-…`, `fix-…`, `refactor-…`, matching the branch name without the slash.

## The index

**Every record gets a line in [`CHANGELOG.md`](CHANGELOG.md), in the same commit that
creates the record.** The index is what turns this folder from a pile of files into
something searchable: one table answers "what did we do" and "where is it written down"
without listing a directory and guessing at file names.

- **Newest first.** That is the order a reader arrives in.
- **Add the row, do not rewrite the table.** Append one line; a diff that adds a row is
  reviewable, a reformatted table is not.
- **The index and the files beside it must agree.** A record on `develop` with no row
  here means the index is wrong. An index that looks authoritative and is not is worse
  than no index, so this is not optional bookkeeping.
- **On merge**, fill in the `Merged` column with the merge commit. Before that, `—`.

## Template

```markdown
# <What changed, in one line>

- **Started:** 2026-10-07 10:49
- **Branch:** fix/catalog-chip-strips
- **Kind:** fix | feat | refactor | docs | chore
- **Status:** in progress | merged | abandoned

## Why

The problem, in the user's terms. What was wrong, and who it affected.

## What changed

Files and behaviour. A reader who has not seen the diff should be able to tell what to
expect to find.

## How it was verified

Measured numbers and screenshots — not "looks fine". See `.opencode-rules.md` §1.2:
a prediction is not a measurement, and a layout claim without one is not a claim.

## What was rejected, and why

The alternative that was seriously considered and the reason it lost. This is the part
that is worthless to reconstruct later and the reason the file exists at all.

## Rules this changed

Which sections of `.opencode-rules.md` this work added to or amended. Empty is a legitimate
answer; a non-empty one is a checklist item for §17.
```

## Rules

- **Created before the first code change**, on the branch, in the same commit. Its value
  is that it captures intent while intent is still cheap to change.
- **Committed, not local.** `docs/` is tracked; these are history, not scratch. If the
  change is abandoned, the file stays with `Status: abandoned` and says why — an abandoned
  approach that was tried and rejected is worth more than a silence.
- **Not a duplicate of `docs/decisions/`.** A change record says *what changed, when*.
  A decision document says *why this design rather than the obvious alternative* and is
  keyed by subject, not by date. Link to each other; do not merge them.
- **Numbers over adjectives.** "44.2 dp, measured from a uiautomator dump" is a record.
  "chips look right now" is not.
- On merge, set `Status: merged` and add the merge commit hash.
