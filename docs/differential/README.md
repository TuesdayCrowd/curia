# Differential comparison — archived run records

Frozen reports from the three-way differential harness
(`tools/differential-oracle/compare.mjs`, R14.6). Each file records one run on one date
against one seed. **None of them states the current divergence count, and none is a gate.**

| File | Records |
|---|---|
| `2026-08-13-divergences.md` | The first full run: 15 divergence classes over 22,515 lines. |
| `2026-08-13-divergences-rerun.md` | A rerun ~90 minutes later: 14 classes, under a heading that still said fifteen. Kept as the specimen behind errata E14. |
| `2026-08-13-findings.md` | The analysis of the first run — per-class verdicts and 18 proposed conformance vectors. |

## Where the current answer lives

```bash
node tools/differential-oracle/compare.mjs --fail-on-divergence
```

CI runs this as its own gating job. **The gate's exit code is the record.** The report it
writes goes to a temporary path (`--report`) and is uploaded as a build artifact only when
the gate is red, so a divergence is readable exactly when there is one to read.

Without `--fail-on-divergence` the harness exits 0 even having found divergences. That is
right for a human run judged by the report and wrong for a gate, which is why CI passes the
flag and why nothing here is a substitute for running it.

## Why these are not in `tools/differential-oracle/`

They were, and it was the wrong place for two reasons that turned out to be the same one.

`compare.mjs`'s default `--report` path is `tools/differential-oracle/DIVERGENCES.md`. So a
tracked document sat at the default output path of the tool that generates it: any local run
of the documented command silently overwrote a month-old record, and any run whose result
someone committed would replace the history with a snapshot.

And it read as current. It said *"Found 15 divergence classes across 22515 compared lines"*
with no date in its title, while the gate beside it passed clean — a tracked document
contradicting its own gate. That is the shape this project keeps finding in itself: an
artifact claiming more than its measurement supports. `compare.mjs` had already fixed one
instance inside the report generator (see the comment at `writeReport` citing errata E14);
the document as a whole was the same defect one level up.

`tools/differential-oracle/DIVERGENCES*.md` is now git-ignored, so the default output path
produces a local scratch file that cannot become tracked history again. Adding a record here
is therefore deliberate: date the filename, say in the first line what run it records, and
say that it is not the current state.
