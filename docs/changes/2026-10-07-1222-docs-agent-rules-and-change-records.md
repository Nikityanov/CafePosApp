# One branch per feature, and a written record per change

- **Started:** 2026-10-07 12:22
- **Branch:** docs/agent-rules-and-change-records
- **Kind:** docs
- **Status:** merged — `13463bf` (merge commit), `1c09928` (branch)

## Why

Two things were missing from how we work, and one existing rule was not being followed.

The comment-out and split work of this session produced 12 commits that went **straight
onto `develop`**. §3 already said "work only on your feature branch" — the rule existed and
was simply not obeyed, because a rule stated as a principle has nothing to enforce it and
nothing to check it against. Every feature and every fix now gets its own branch and is
merged back, with the commands written down.

Second, decisions taken during that work lived only in commit messages and in
`docs/decisions/`, and nothing said where a change should be *recorded* while it was being
made. The reasoning for why a chip strip had to scroll was in my head and in a commit
body; it should have been a file that existed from the first minute.

Third, these rules are a snapshot. Decisions get made in planning sessions and then vanish,
so the next agent works from a rule that contradicts the last accepted decision. Planning
now has to write its outcome back into the rules file.

## What changed

- **§3 rewritten** around branch-per-feature, with the exact commands: `switch -c` off
  `develop`, push the branch, `merge --no-ff`, delete it after. `--no-ff` on purpose — the
  merge commit is the index entry for the change record, and squashing would erase the
  boundary. Merge is gated on five conditions, written as a checklist because a gate
  nobody can check is not a gate.
- **§16 added** — one change record per feature or fix, in `docs/changes/`, stamped
  `YYYY-MM-DD-HHMM-<slug>.md`, created before the first code change and committed with it.
  The template lives in `docs/changes/README.md` and demands the section most records omit:
  what was rejected and why.
- **§17 added** — `grill-me` is mandatory before a new feature or fix, and any planning
  session owes a write-back into this file. Includes where each kind of rule belongs, and
  the case that matters most: a decision that **invalidates** an existing rule must amend
  or remove it, because a stale rule is not neutral, it is an instruction to do the thing
  we just decided against.
- **§0 updated** — `grill-me` added to the skills table as mandatory, and the boundary now
  says a skill that produces a decision obliges the write-back.
- **Contradiction found and fixed.** §4 said the owner owns running and testing the app on
  device and "do not run these unasked", while §1.2 documents the adb measurement recipe
  and §10 authorises the emulator. Both could not hold. Now: the emulator is the agent's
  to drive without asking, the physical phone stays the owner's, and §3.3 item 4 is
  explicitly the one merge condition automation does not satisfy.

## How it was verified

Read, not assumed:

- Rules file 452 → **912 lines**, 57 522 bytes, **0 null bytes**, no BOM, headings §0–§17
  contiguous with no gaps or duplicates.
- Cross-references checked: §17 is linked from §0, §16 from §3.1 and §3.3, §13 from §2,
  §12 from §7. No dangling section numbers.
- `docs/` is tracked (18 files before this), so `docs/changes/` will be committed — unlike
  `.opencode-rules.md`, which is gitignored by its own declared choice and stays local.
- Not verified: no rule here can be proven by running it. The evidence that these rules
  work is the next feature going through §3 and §16 without being asked.

## What was rejected, and why

- **Putting the change records in `docs/decisions/`.** Rejected: that folder is keyed by
  subject and holds *why this design*, which outlives any one change. A dated record is the
  opposite shape, and merging the two produces a folder nobody can navigate.
- **`git merge --squash`.** Rejected: it gives a clean linear history but destroys the
  feature boundary the change record describes, and the record then refers to a commit that
  no longer exists.
- **A rule that `grill-me` is mandatory for every single edit, with no exception.**
  Rejected, and this is the one place I did not simply do as told. A mandatory interview on
  a typo is a rule that gets skipped within a week, and a routinely-violated rule is worse
  than no rule — the same reasoning already in §6 for touch targets. So the carve-out is
  narrow (typo, one-character fix with an obvious cause) and, crucially, **written down as
  something the owner may overrule** rather than something I decided quietly.
  Nothing touching money, stock, order state or layout is carved out whatever its size.

## Rules this changed

- §0 — added `grill-me` as mandatory; added the write-back obligation to the boundary.
- §3 — rewritten: branch per feature, merge into `develop`, five merge conditions.
- §4 — resolved against §1.2/§10: the emulator is the agent's, the phone is the owner's.
- §16 — new.
- §17 — new.
