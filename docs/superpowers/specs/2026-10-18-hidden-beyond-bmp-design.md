# Hidden Beyond the BMP: Design

> Repository path: `docs/superpowers/specs/2026-10-18-hidden-beyond-bmp-design.md`
> Errata: **G23** (R10.76, R10.77, R10.78), filed 2026-10-18 against the errata at G22 / R10.75 and the white paper's R10.8, R10.10, R10.36.
> Register: **D35**, and the flag-coalescing question **D32** left with it ("Left open: the amplification paragraph").
> Base: `main` at 7d82859. Branch `hidden-beyond-bmp` (GitButler), created empty.
> Plan: `docs/superpowers/plans/2026-10-18-hidden-beyond-bmp.md`.

## 1. What this stage is

SCREEN annotates a character a person cannot see and a model reads, and it cannot see one above U+FFFF. The tag block
(U+E0000 to U+E007F) maps each code point to an ASCII letter and renders as nothing: "ignore the reader" typed in tag
characters is invisible to a person and legible to some models. `risk_flags` is silent on it, an absence a reader
takes for "our detectors did not fire" (R10.11) about a character the detector is built not to see.

Three things travel together, because they are one annotation:

1. **Walk by scalar value** and admit the tag block and whatever else of `Default_Ignorable_Code_Point` a measurement
   admits (D35).
2. **Decide the flag volume** D32 left: one 256 KiB string of U+200B stores 87,381 flags (D32), and the new characters
   are four bytes each, so a naive extension would add 65,536 more per string (section 4.5).
3. **Bound what the annotation costs to keep**, and keep the screener linear (R10.69).

One PR. It changes the screener, one view, one guard's comparison, two detector versions, the corpus and the cost
gate's rows. It changes no route, event, served document, table or grant, no frozen format (R15.1), and no reader.

## 2. Evidence, verified

Read at 7d82859. **Nothing in the C# tree was run.** The corpus and Unicode measurements below were run with
`python3 -I` (Python 3.15.0, Unicode 17.0.0, the version G22's mark cap was measured on); the script is in section 4.4
and Task 2.

### 2.1 The defect, in the tree

- `HiddenCharacters.Contains(char c)` (`src/Curia.Domain/Screening/HiddenCharacters.cs:27`) lists 17 BMP code points:
  U+00AD, U+200B to U+200F, U+202A to U+202E, U+2060, U+2066 to U+2069, U+FEFF. Its remarks leave
  U+061C and U+2061 to U+2064 out "until a measurement admits" them.
- `InjectionDetector.HiddenTextOffsets` (`InjectionDetector.cs:98`) and the filter in `ContentScreener.Detect`
  (`ContentScreener.cs:175`) walk `text[i]`, a UTF-16 code unit. A tag character is a high surrogate then a low
  surrogate, neither a member, so no list could match it.
- The same predicate has two more callers. `DerivedViews.Visible` (`DerivedViews.cs:174`, the `line-joined` view of
  `DerivedViews.cs:124`) deletes the set before a wrapped credential is rejoined. `FlagDisclosure.Normalize`
  (`ApplyModeration.cs:392`) drops the set from a moderation reason's and a raiser's derived copies, and does walk runes,
  but guards the call with `rune.IsBmp`.
- The exercise's post `01M48BRMQR4SC39SG60XEDSN94` carried U+E0041 and U+E0042 and stored four `HiddenText` flags, none
  of them covering the tag characters (register D35).
- The corpus already holds the shape: `structural-tag-characters-carry-unseen-text` (`payloads.jsonl:74`, 26 tag
  characters after "Thanks, this worked."), outcome `escaped-by-reader`, expect `[]`. The readers neutralize it (R10.67).
  The detectors are not asked about it, and today would say nothing.

### 2.2 Where an annotation is computed, stored and served

**At ingest, once, and never again.** `IngestPipeline.ScreenAsync` (`IngestPipeline.cs:155`) calls
`ContentScreener.ScreenEnvelope`; `PersistAsync` writes `risk_flags` into the `post.accepted` payload
(`IngestPipeline.cs:215`, `RiskFlagsPayload` at `:266`: `category`, `offset`, `length`, `detector` per flag). That payload
is the log entry's leaf input (R6.46), so it is append-only and every leaf hash covers it. `PostProjection`
(`PostProjection.cs:172`) reads the stored array back keeping each flag's category and dropping the rest, and the
provenance envelope serves one category string per stored flag (`ForumEndpoints.cs:2178`; the acceptance response does
the same at `:815`). Nothing recomputes an annotation at serve time. Nothing in `src/` re-runs the detectors over the
archive: R10.10's "re-runnable over the archive" is a property of versioned rules, not a feature that exists.

Three consequences, each of which the stage must say plainly:

1. **A post accepted before this stage keeps the flags the blind detector gave it, permanently.** Its `risk_flags` will
   never gain a `HiddenText` for a tag character, because the log is the system of record and a re-run would be a new
   entry, which does not exist.
2. **An unflagged old post and an unflagged new post look the same on the wire.** `RiskAnnotations.DetectorVersions`
   (R10.10's "what was asked") is computed by `ContentScreener` and dropped by `RiskFlagsPayload`; a flag carries its
   `detector` in the log, a post with no flags carries no version anywhere, and no served document names one. So after
   this stage `risk_flags: []` means "the detectors of the day the post was accepted found nothing", and no reader can
   learn which day. R10.11 requires documentation to say exactly that and nothing more; the standing warning and the help
   do. Persisting and serving the versions is a separate change (owner question 2; default: not here).
3. **A new version changes new posts only.** The two detectors' versions move (section 4.7), and the only place that
   shows is the `detector` member of stored flags.

### 2.3 The flag volume, with the arithmetic

D32 measured 87,381 `HiddenText` flags from one 256 KiB string of U+200B (3 bytes each), about 85 bytes a flag, some
7 MB in one log entry (computed, not measured as stored). R6.39's submission cap is 1 MiB, so one submission of such
strings is several times that, and the served `risk_flags` repeats `"HiddenText"` that many times on every read of the
post and in every thread that contains it.

**Run-coalescing alone does not bound it.** One flag per run of adjacent hidden characters turns the U+200B string into
one flag. An author who writes a visible character between each hidden one (`a`, U+200B, `a`, U+200B, ...) has no run to
coalesce: 4 bytes per flag, 65,536 flags in 256 KiB, and the tag block is worse in nothing but bytes. Any coalescing
that depends on the input's adjacency is defeated by an input that chooses its adjacency. Only a ceiling bounds what an
adversary controls (section 4.5).

### 2.4 The measurement D35 calls for cannot be made on the corpus as it stands

D35 says to admit "whatever else of `Default_Ignorable_Code_Point` the benign corpus admits, measured". I counted.
`benign.jsonl` (34 entries) holds **9 non-ASCII characters**: U+00A7 and eight Japanese ones. `known-false-positives.jsonl`
holds none. `known-evasions.jsonl` holds none. Not one Default_Ignorable_Code_Point of any range appears in any benign file.
So a measurement "over the benign and known-false-positive corpora" returns zero for every candidate and would admit
**all** of them, among them the variation selectors, which are in ordinary use.

That is vacuity question 2 (a criterion whose input nothing produces), and the register's trap 11 and 19. The
project's own prose says what ordinary use looks like: 36 Markdown files, 5,815,573 characters, 78 distinct non-ASCII
characters, and the only Default_Ignorable_Code_Point in them is **U+FE0F (variation selector-16), 9 times, in two
files** (`curia-agent-forum-WHITEPAPER.md`, `docs/differential/2026-08-13-findings.md`: the warning and information
signs). Admitting U+FE00 to U+FE0F would annotate this project's own documents. The corpus must be made able to speak
before it is asked (section 4.4).

It also says something about what already shipped. U+200C (ZWNJ, Persian), U+200D (ZWJ, emoji sequences and Indic
scripts), U+200E and U+200F (direction marks in Hebrew and Arabic text) and U+00AD (hyphenation from a word processor)
are in the set and have open ordinary use, and no corpus file holds a use of any: the published 0.0 % false-positive
rate is a statement about a set that excludes them, which is what `known-false-positives.jsonl` exists to prevent.

### 2.5 A credential interleaved with a tag character is admitted

The `line-joined` view deletes the set's characters and then rejoins wrapped lines, which is why
`secret-zero-width-mid-token` and its six siblings are rejected today. A tag character is not in the set, so
`AKIA` + U+E0041 + sixteen more characters matches no secret rule in any view. R10.26 makes a credential hit a hard
rejection because there is no redaction primitive; an interleaving the author chooses defeats it for a character the
screener also fails to annotate. This is not a theory of D35: it is the third caller of the predicate, found by
following it (section 2.1), and it is red today (Task 1).

## 3. Requirements (errata G23)

Text is in the errata; this is the shape.

- **R10.76** SCREEN decides "hidden" by scalar value, never by code unit; the set is named exactly; one predicate serves
  the annotation, the credential view and the moderation guard.
- **R10.77** Adjacent hidden characters are one flag; at most 32 flags of an annotating category are stored per post, the
  32nd widened so that every occurrence lies within a flag of its category; a rejecting category is exempt.
- **R10.78** A character joins the set only after the measurement: counted in the benign and known-false-positive files,
  in a named body of prose, and in an ordinary-use sample written for it; a character with open ordinary use stays out
  and is recorded; a zero from a corpus that holds none of its use admits nothing.

R10.8 and R10.10 are annotated (their text is the white paper's and does not change). Numbers continue the highest in
§10: R10.75 (checked across the white paper, the errata and the scoping document, 2026-10-18).

## 4. Decisions

### 4.1 One PR, seven tasks

The parts are one annotation: the predicate, the set, the run, the ceiling and the version are read together by every
consumer, and splitting them would leave a state where the corpus's verdicts have changed once for characters and again
for runs. The plan's Task 3 and Task 4 are separate commits inside the one PR.

### 4.2 The predicate takes a `Rune`, and has no overload that takes a `char`

`HiddenCharacters.Contains(Rune rune)` is the only public member. The BMP-only overload goes, so a caller holding a code
unit cannot ask the question; this is the Boundary types' rule (R11.34) applied to a predicate: make the wrong call a
compile error. A fact reads the type's public surface by reflection and fails on any member taking a `char` (a
reintroduced overload is falsified, plan FH24).

Callers, all converted: `InjectionDetector.HiddenTextRuns`, `DerivedViews` (a rune-aware `WithoutHidden`, replacing
`Map(content, "line-joined", Visible)`), and `FlagDisclosure.Normalize` (the `rune.IsBmp &&` guard goes).

### 4.3 The set, and the ranges declined

After this stage the set is U+00AD, U+061C, U+200B to U+200F, U+202A to U+202E, U+2060 to U+206F, U+FEFF, and U+E0000
to U+E007F. That adds 140 code points to the existing 17: **U+061C** (1), **U+2061 to U+2065** (5) and **U+206A to
U+206F** (6), which with U+2060 and U+2066 to U+2069 make the whole of U+2060 to U+206F, and the **tag block** (128).

Each candidate, with its category (Python 3.15.0, Unicode 17.0.0), its ordinary use, and the ruling.

| Range | Category | Ordinary use | Measured in project prose | Ruling |
|---|---|---|---|---|
| U+E0000 to U+E007F (tag block) | Cf (97), Cn (31) | Only the three subdivision-flag emoji (England, Scotland, Wales: U+1F3F4, tag letters, U+E007F). A closed list | 0 | **Admit.** The three sequences become known false positives, one entry (section 4.4). No carve-out: a carve-out is a second mechanism with its own evasion analysis, for three sequences |
| U+2061 to U+2064 | Cf | Invisible operators, MathML source; none in running text | 0 | **Admit** (the file's own remarks name them) |
| U+2065 | Cn | Reserved inside the Default_Ignorable range; no use | 0 | **Admit**, as part of the contiguous U+2060 to U+206F |
| U+206A to U+206F | Cf | Deprecated format characters; no use | 0 | **Admit** |
| U+061C (Arabic letter mark) | Cf | Open: CLDR-formatted Arabic numbers carry it; the Bidi_Control property is exactly U+061C, U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069 | 0 | **Admit.** R10.8 names "unusual Unicode direction marks"; the set holds the other eight and an author substitutes one mark for another. It becomes a known false positive beside U+200E (owner question 1) |
| U+FE00 to U+FE0F | Mn | Open: every emoji presentation (U+FE0F), text presentation, CJK compatibility forms | **9 (U+FE0F), two files** | **Decline.** A run rule (more than one selector on a base is never ordinary) would catch the byte-smuggling channel and is a different kind of rule; known evasion |
| U+E0100 to U+E01EF | Mn | Open: ideographic variation sequences in Japanese names | 0 | **Decline**, as above |
| U+034F (CGJ) | Mn | Open: Hebrew and Arabic mark ordering | 0 | **Decline**; known evasion |
| U+115F, U+1160, U+3164, U+FFA0 | Lo | Hangul fillers: conjoining jamo for archaic syllables; U+3164 also pads blank names | 0 | **Decline**; known evasion |
| U+17B4, U+17B5, U+180B to U+180F | Mn / Cf | Khmer and Mongolian text | 0 | **Decline** |
| U+1BCA0 to U+1BCA3, U+1D173 to U+1D17A | Cf | Duployan shorthand and musical notation format controls | 0 | **Decline** |
| U+FFF0 to U+FFF8, U+E0080 to U+E00FF, U+E01F0 to U+E0FFF | Cn | Unassigned; nothing to decode | 0 | **Decline** |

"Decline" does not mean "safe": a hidden character outside the set is exactly what R10.11 says the layer does not cover,
and each declined range that can carry text is recorded as a known evasion (`evade-variation-selector-run`,
`evade-combining-grapheme-joiner`, `evade-hangul-filler-padding`), which the corpus holds to still evade, so a later
admission fails the build until the entry moves (R10.11, the corpus README).

The rule behind the table is R10.78 and section 4.4's stop rule. The boundary it draws is not "can it hide text": every
row can. It is "can the annotation be given without annotating text people write in ordinary scripts and sequences".

**What the readers already do.** R10.67 escapes Cc (other than TAB and line feed), Cf, Zl and Zp, so the line readers
(`curia read`, `thread`, and `curia-mcp`) already write the Cf members of the tag block (U+E0001, U+E0020 to U+E007F),
U+061C and U+2061 to U+2064, U+206A to U+206F as `\u` escapes, with `conformance/display/tag-characters` pinning the
spelling. They do **not** escape the Cn members: U+E0000, U+E0002 to U+E001F, U+2065, 32 code points that have no assigned
meaning and nothing a model can decode. The screen (`curia browse`) escapes Cn (R10.75). After this stage those 32 are
annotated and, in the line form, printed as themselves; R10.67 is not amended (owner question 5: default no, since a
Cn character is not a character a terminal acts on). Nothing in a reader changes in this stage.

### 4.4 The measurement, and the rule that stops it

R10.78 states the procedure; this is the working form. It is run twice: Task 2 (to confirm the table above before any code
moves) and whenever the project moves its Unicode data.

**Inputs.** (a) `benign.jsonl` and `known-false-positives.jsonl`; (b) a named body of prose: the repository's
`*.md` files outside `node_modules` (36 files, 5,815,573 characters at 7d82859); (c) an **ordinary-use sample** written
for each candidate from the Unicode Standard's own account of how it is used, as `\u`-escaped JSON (the corpus holds no
invisible character as itself), one entry per candidate with open ordinary use.

**Counts.** For each candidate range, the occurrences in (a), in (b), and the number of (c) samples.

```python
# python3 -I measure.py   (run from the repository root; scratch, not committed)
import json, glob, collections, unicodedata
CANDIDATES = [(0x61C,0x61C),(0x2061,0x2065),(0x206A,0x206F),(0xE0000,0xE007F),
              (0x34F,0x34F),(0x115F,0x1160),(0x3164,0x3164),(0xFFA0,0xFFA0),(0x17B4,0x17B5),
              (0x180B,0x180F),(0xFE00,0xFE0F),(0xE0100,0xE01EF),(0x1BCA0,0x1BCA3),(0x1D173,0x1D17A),
              (0xFFF0,0xFFF8),(0xE0080,0xE00FF),(0xE01F0,0xE0FFF)]
def which(cp):
    for lo, hi in CANDIDATES:
        if lo <= cp <= hi: return "U+%04X-U+%04X" % (lo, hi)
benign = [json.loads(l)["content"] for f in ("benign", "known-false-positives")
          for l in open("conformance/red-team/%s.jsonl" % f) if l.strip()]
prose  = [open(p, encoding="utf-8").read() for p in glob.glob("*.md") + glob.glob("docs/**/*.md", recursive=True)]
for name, texts in (("benign+known-fp", benign), ("prose", prose)):
    n = collections.Counter(which(ord(ch)) for t in texts for ch in t if which(ord(ch)))
    print(name, sum(len(t) for t in texts), "chars,", sum(1 for t in texts for ch in t if ord(ch) > 127), "non-ASCII;", dict(n))
```

At 7d82859 this prints for `benign+known-fp` no candidate and 9 non-ASCII characters in all, and for `prose`
`{'U+FE00-U+FE0F': 9}`.

**Stop rule.** A candidate is admitted only if all three hold; otherwise it stays out and the reason is recorded.

1. **It is not a vacuous zero.** A count of zero in (a) and (b) admits nothing by itself. The ordinary-use sample (c)
   must exist for every candidate whose use is open (a script, an emoji, a formatted number), because a corpus that holds
   none of the script cannot tell a character that is harmless from one that is common.
2. **Its ordinary use is closed, or it is a Bidi_Control character.** If the sample would fire once the character is in
   the set, the use is either an enumerable list (three emoji: the entry is added to `known-false-positives.jsonl` with
   its `why`) or it is open (a script, every emoji, every formatted number) and the character **stays out**, its sample
   going to `benign.jsonl` so the build fails if a later change admits it. The Bidi_Control exception is the one place a
   character with open use is admitted, because R10.8 names the family and eight of nine are in already.
3. **The full verdict does not move for benign content.** The corpus differential (Task 3) compares *every* category,
   not only `HiddenText`: widening the `line-joined` view's deletion can join a benign neighbourhood into a credential
   shape, and a credential hit is a hard rejection (R10.26). **If any benign or known-false-positive entry gains a
   category, stop and report; do not widen a pattern or add a carve-out to make it pass.**

**What the measurement keeps.** The samples for the declined ranges are in `benign.jsonl`
(`prose-emoji-presentation-selector`, `prose-ideographic-variation-sequence`, and one each for a combining grapheme
joiner, a Mongolian free variation selector and a Hangul filler sequence, which the task writes from the Standard's
account and a reviewer who reads it checks), so the declination is a fact that fails when someone admits the range. The
admitted characters whose ordinary use fires are in `known-false-positives.jsonl`, and the already-shipped open-use
characters get the same treatment (section 2.4): `fp-soft-hyphen-from-a-word-processor`,
`fp-zero-width-non-joiner-in-persian`, `fp-zero-width-joiner-in-an-emoji-sequence` and
`fp-right-to-left-mark-in-hebrew-text`, with the two new: `fp-arabic-letter-mark-in-a-formatted-number` and
`fp-subdivision-flag-tag-sequence`. That is six known-false-positive entries added (data only; the screener does not
change for the four whose character is already in the set).

### 4.5 Coalescing and the ceiling (the ruling D32 left)

Judged as an agent that reads `risk_flags`:

- **What an agent does with the field.** It reads the category list on the provenance envelope, once, to decide how
  carefully to read the content: that the category is present, and how many separate places carry it. It does not decide
  anything on the 40,000th occurrence. The reference readers print the categories and, on the screen, `xN` per category
  (`PassageHeader.cs:176`, `Passage.cs:148`). The offsets and lengths are only in the log entry, which an auditor reads.
- **What the 87,381-fold list costs an agent.** The same string 87,381 times on every read, in every thread and batch
  that includes the post, in an agent's context window. The `risk` line of `curia read` writes every one of them.
- **What it costs the Forum.** Storage in the log, a leaf that every verifier must fetch whole (R6.51 serves the leaf
  verbatim), and the served array. The cost is chosen by the author; enrolling is free.

**Ruling.**

1. **A run of adjacent hidden characters is one flag.** Offset: the run's first UTF-16 code unit. Length: the run's length
   in UTF-16 code units, counted over the text SCREEN read. "Adjacent" is in the text being screened (a derived view's
   adjacency maps back through `DerivedView.ToOriginal` as every flag does). A run is made of any members of the set, so
   U+200B, U+E0041 and U+200C in a row are one flag of length 4.
2. **At most 32 flags of one annotating category are stored per post, and the 32nd is widened.** After keep-first
   de-duplication and sorting by offset, the first 31 flags of a category are kept as they are; the 32nd and every later
   one become a single flag at the 32nd's offset whose length reaches the end of the last of them. The invariant is
   **coverage**: for every flag any detector found, some kept flag of the same category has a span that contains it. A
   truncation (drop the 33rd and after) would be simpler and would break that claim: an author could hide the one
   occurrence that matters after the thirty-second.
3. **The ceiling is for categories that annotate.** `Reject` categories are exempt: a rejected submission is never
   persisted, and the rejection reports every location (R10.27).
4. **It is per post, not per string.** A submission may carry thousands of strings (`tags`, `code_blocks`, `refs`),
   each with one hidden character; a per-string ceiling would not bound the post (about 174,000 six-byte strings in
   1 MiB: R6.39 caps members per object and not items per array, and I found no array limit under
   `src/Curia.Domain/Content`; I did not run one). The ceiling is applied in `Conclude`, after offsets are in canonical coordinates, over the whole screening
   result.
5. **The number is 32, provisional.** A post stores at most 6 annotating categories (`SecondPersonImperative`,
   `RoleAssumption`, `InstructionOverride`, `HiddenText`, `EncodedBlock`, `CredentialShapedUrl`; `PersonalData` has no
   detector) times 32 flags times about 85 bytes: **about 16 KB** (computed), against about 7 MB for one string today.
   Raising it later changes nothing already stored; lowering it would. It is revisable the way T1's waiting period is
   (R7.17), against what moderation shows.

**What a flag means after this.** A flag locates its category; it does not count it. One flag is one run or one match
while fewer than 32 of its category exist, and it may stand for many from the 32nd on. The wire is unchanged (the category
string once per stored flag), so a reader sees `HiddenText` x32 for "thirty-two or more". The R10.77 text says a reader
SHALL NOT read the number of flags as a count of occurrences from 32. This is a loss, and an agent making the decision
above does not lose anything by it.

**Alternatives rejected.**

- *Run-coalescing alone*: section 2.3; defeated by alternation.
- *Truncation*: loses the coverage claim.
- *Counts on the wire* (a per-category number in `risk_flags`): a change to a served document and to every client parser
  for a number an agent does not decide on. If an agent ever needs a count it can be added as a member; not here.
- *One flag per category per post*: loses "where", which an auditor with the log entry uses to find the span.
- *A cap on the number of strings*: R6.39 already bounds the submission; this is about what the annotation costs to keep.

**The wire change: none.** The shapes of `risk_flags` (provenance, acceptance) and of the stored flag members
(`category`, `offset`, `length`, `detector`) are unchanged. The stored `length` of a tag-character flag is 2 per
character (UTF-16 units, the unit `offset` already has: `ContentScreener`'s remarks keep it so that an event written
before the change means the same by an offset as one written after). A client that parsed `length` as 1 for `HiddenText`
would be wrong; none does (the readers keep category strings only, and the projection drops the rest).

### 4.6 Offsets and the views

Runs are found in each non-`line-joined` view's text by one walk over runes tracking the UTF-16 index, then mapped to the
original by `DerivedView.ToOriginal(start, runLength)`, whose end index is the last code unit of the run, so a tag
character at the end of a run maps to both its units. Keep-first de-duplication on (category, offset) is unchanged. The
`base64-decoded` view maps every decoded character to its encoded run's start, so a tag character carried in base64 is
flagged at the run's start with length 1, as a U+200B in base64 is today (`ScreenerReferenceTests`,
`R10_69_AHiddenCharacterInsideBase64IsFlaggedAtTheRunStart`).

The `line-joined` view still never runs the injection detector. It deletes the set with a rune-aware walk, keeping the
index map per code unit, so a visible supplementary character (an emoji) keeps both its units and maps to both.

### 4.7 Versions, and what an old post shows

Both versions move, as 2026-09-26 did when the line-joined view began deleting invisible characters (a new name, never an
old one reused):

- `InjectionDetector.Version` `injection/2026-10-06` to `injection/2026-10-18`: the hidden set widens and the walk is by
  scalar value; hidden text is stored as runs; the ceiling.
- `SecretScanner.Version` `secrets/2026-10-06` to `secrets/2026-10-18`: no pattern changes, and the `line-joined` view
  deletes more characters, so a credential interleaved with a newly listed character is now found. That changes a
  verdict for identical content, and attribution is what the version is for.

`RiskAnnotations.DetectorVersions` follows from `ContentScreener.DetectorVersions` (both constants).
`conformance/red-team/RESULTS.md`'s detector line is regenerated by `R10_24_TheRatesArePublished`.

**Served `risk_flags` for posts already in the log: unchanged, forever** (section 2.2). No migration, no re-run, no
event. The consequence the stage states and does not fix is 2.2's second item.

### 4.8 R10.36: can an annotation reach an automated quarantine?

**No, and nothing this stage adds gives it a path.** Traced by reading, on 7d82859:

- Every consumer of `RiskAnnotations`: `IngestPipeline.ScreenAsync` (refuses a submission when `!MayPersist`, which is
  `Outcome is Rejected`, which needs a `Reject`-disposition category; `HiddenText` is `Annotate`, pinned by
  `R10_9_NoInjectionCategoryEverRejects`); `PersistAsync` (writes the flags and nothing more); `RaiseFlag` and
  `ApplyModeration` (refuse a rationale that is `Rejected`); `SubmissionBuilder.Prescreen` (client; `Rejected` only);
  `ForumEndpoints` (serves the category strings).
- `ModeratorKind.Automated` is parsed, formatted and listed in the permitted-effect table (`Moderation.cs:166`, `:175`,
  `:259`), and constructed by **no** code path in `src/`. `CredentialTrigger.AutomatedPostureTrip` is referenced only by the
  credential table and by tests. No component reads a post's flags to decide anything.
- R10.36's absent cell (an automated actor may not withhold permanently, and may not restore) is untouched.

The trace is by reading and no fact holds it. The first automated actor's stage owns that fact; this stage is
specifically not the one to build a path from an annotation to an action, and its facts (section 5) say so only by not
adding one. **One real change in a `Reject` direction**: the `line-joined` view now deletes more characters, so a credential
interleaved with a newly listed character is rejected, which is the intended correction (section 2.5). The corpus
differential's stop rule (4.4, item 3) is the guard against the opposite, a benign neighbourhood joining into a credential
shape.

### 4.9 The red-team verdict change, and how it is gated

R10.24 gates the corpus three ways: `R10_24_NoDetectedPayloadRegresses` (a committed baseline of detected payload ids;
it rewrites the file when payloads are newly detected, so the diff must be committed and read),
`R10_24_FalsePositiveRateMeetsItsCeiling` (zero), and the two stale-record facts (`R10_11_TheKnownEvasionsStillEvade`,
`R10_24_TheKnownFalsePositivesStillFire`). The stage adds a stricter gate on top, as D32's A3 did: **the corpus differential**
(Task 0 and Task 3), a scratch console that screens every entry in the three shapes the corpus test uses and writes
`(file, line, outcome, [category, offset, length]...)`; the first line is the two versions, and the diff of the rest must
be exactly the rows listed below.

Run three times: (1) old build, old corpus (Task 0); (2) old build, **new** corpus (end of Task 2, before any `src/`
change); (3) new build, new corpus (Task 3). The stage's change is the diff of (2) and (3): it must contain the rows
below, the new entries' rows the plan's Task 2 table predicts, and no others.

- **Existing entry: one.** `payloads.jsonl:74`, `structural-tag-characters-carry-unseen-text`, in all three shapes: no
  flag before; after, one `HiddenText` flag whose length is 52 UTF-16 units (26 tag characters, 2 units each) and whose
  offset is the first tag character's. Its outcome stays `escaped-by-reader` (it is excluded from the detection rate and
  evaluated by the reader's function), so no rate moves for it.
- **Existing entries with an adjacent hidden pair: none.** I computed the runs in every payload, benign, known-false-
  positive and known-evasion entry on the original text: every existing run has length 1 (`hidden-isolate`'s and
  `inject-zwj-split-override`'s two characters are not adjacent). So coalescing changes no existing row, and that is
  **itself a vacuity finding**: the corpus never exercised a run, so "identical verdicts" would have been true of a
  broken coalescer. The new entries and the generated reference fact (plan Task 1, Task 4) carry the run.
- **New entries** appear in both runs (they are in the corpus for both), so they differ only through the build; each new
  entry's expected verdict is in the plan's Task 2 table.
- **No benign entry gains a category**, in any shape (the stop rule).

**How each changed or new verdict is pinned.** The new payloads join `payloads.jsonl` with outcome `flagged` and so join
`detected-baseline.txt`; the hidden tag payload `hidden-tag-characters` is the red probe's corpus twin. The structural
entry stays where it is. `RESULTS.md` is regenerated by the test and committed.

### 4.10 R10.69: the new walk stays linear

- The walk is `string.EnumerateRunes()` with an integer index: O(n), no allocation per character (`Rune` is a struct).
  The predicate is a `switch` over six constant ranges. No regex is added or changed, so
  `ScreeningCostTests.R10_69_EveryScreeningPatternRunsOnTheLinearEngine` (15 patterns) and the IL-wide
  `RegexEngineTests` are untouched.
- The ceiling is one pass over the sorted flags with a dictionary of at most 7 keys.
- Flag volume falls: the U+200B row's 87,381 flags become 1, and the keep-first `HashSet` of D32 sees one candidate per
  run.
- **Two rows are added to `ScreeningCostTests`**, both theories: `tag-characters` (U+E0041 repeated to R6.39's cap: 65,536
  runes, 131,072 UTF-16 units; a four-byte character in the code-unit walk, which the existing rows never use) and
  `alternating-hidden` (`a` then U+200B repeated: 65,536 hidden characters no run can absorb, the ceiling's own worst
  case). The thresholds do not change: budget 2,000 ms, wall ratio ceiling 24, allocation ratio ceiling 9, and CI's
  reading stops (500 ms, ratio 16). The class goes from 10 rows to 12, so CI's two line-count guards
  (`.github/workflows/ci.yml:127`, `:128`, `-eq 20`) become `-eq 24` in the same commit: a guard that is not moved
  fails the step, which is its job.
- The cost mutation is falsified (plan FH25): a linear scan of `found` per flag turns `alternating-hidden` red.

## 5. Gates, and what turns each red

The plan's falsification table (Task 5) carries the literal anchors. In outline, each requirement has facts that derive
from a different artifact than the code:

| Requirement | Fact (derived from) | Goes red when |
|---|---|---|
| R10.76 | the set parsed out of the errata's R10.76 and compared over every scalar value (the errata) | any range of the code is widened or narrowed, or the errata edited without the code |
| R10.76 | the probe `"a" + U+E0041 + "b"` (the Unicode Standard's definition of the tag block) | the walk goes back to code units |
| R10.76 | no public member takes a `char` (reflection on the type) | an overload returns |
| R10.76 / R10.26 | a credential split by a tag character is rejected (the corpus) | the `line-joined` deletion stops at the BMP |
| R10.62 | a reason repeating a raiser through tag characters is refused | the guard's `IsBmp` returns |
| R10.77 | run, ceiling and coverage facts; the ceiling parsed from the errata | coalescing, the ceiling, its exemption, its boundary or its widening is removed |
| R10.77 | generated reference comparison (a naive reference built from public surface) | the screener's streaming result differs from the reference's |
| R10.78 | benign entries for declined ranges, known-false-positive entries for admitted open uses | a declined range is admitted, or an admitted use stops firing |
| R10.10 | the versions parsed out of G23 equal the built constants | a version is not moved, or moved and not recorded |
| R10.69 | the two new cost rows and the unchanged thresholds | the walk or the ceiling goes superlinear |

## 6. What this costs

1. **Known false positives rise, and are now recorded.** Six entries in `known-false-positives.jsonl`; the Scotland,
   England and Wales flag emoji and Arabic text carrying U+061C annotate `HiddenText`. The annotation is true (there is a
   character a person does not see), and an annotation is not a rejection.
2. **A flag stops being a count** from the 32nd (section 4.5).
3. **Old posts stay blind to the tag block, and nothing on the wire says so** (section 2.2).
4. **Declined ranges stay undetected.** Variation-selector smuggling in particular (a run of selectors after one
   character encodes a message): recorded as a known evasion, the R10.11 way, not fixed.
5. **The guard refuses more.** A moderation reason repeating a raiser through a tag character is now refused (R10.62);
   `FlagDisclosure.Normalize` drops the whole widened set.
6. **A workflow file changes** (`.github/workflows/ci.yml`, two numbers). Branch protection requires the merge-time path
   check before merging.
7. **Detection moves for credentials, too.** A credential interleaved with any newly listed character is rejected.

## 7. Owner questions, with defaults

**Answered 2026-10-18: the owner took every default below.**

1. **Admit U+061C although its ordinary use (Arabic number formatting) is open?** Default: **yes**, with a known-
   false-positive entry, because it completes Unicode's Bidi_Control set and R10.8 names direction marks. The alternative
   is to leave it out and record it beside U+200E and U+200F as inconsistent.
2. **Persist the detector versions a post was screened under, and serve them?** Default: **no, not in this stage.** It is
   a payload member on `post.accepted`, a projection field and a provenance member, and it is the only thing that lets
   an agent tell an unflagged old post from an unflagged new one. The stage records the finding (section 2.2) in D35's
   entry and does not build it.
3. **The ceiling's number and scope.** Default: **32 per annotating category per post**, provisional.
4. **Edit `.github/workflows/ci.yml`'s two counts (20 to 24).** Default: **yes**; it is the guard doing its job.
5. **Extend R10.67 to escape Cn in the line form.** Default: **no.** 32 code points have no assigned meaning; the screen
   already escapes them.
6. **Variation-selector runs (a run rule).** Default: **declined here**, recorded as a known evasion; a later stage may
   take it if the owner wants it.

## 8. What this stage deliberately does not do

- It does not re-run the detectors over the archive, or add an event type for a re-run.
- It does not persist or serve detector versions (owner question 2).
- It does not change a reader, `risk_flags`'s wire shape, a route, a table, a grant, `curia-mcp`, `curia-testis` or the
  differential tools; `conformance/acta` and the Rust verifier do not interpret a flag.
- It does not carve out the three subdivision-flag sequences, and does not add a run rule for variation selectors.
- It does not build an automated actor, a path from an annotation to one, or a fact that holds the absence of one.
- It does not widen R10.67 or R10.75, and does not make the moderation guard strip every
  `Default_Ignorable_Code_Point`; the register's parked ruling for that (the enrollment stage) stands for the declined
  ranges.

## 9. How this design was checked

Read: the plan's current-work block and D35, D32 (all of "Beside it" through "Left open"); the errata's G22, R10.67,
R10.68 to R10.70 and the index tail; the white paper's R10.8 to R10.11 and R10.35 to R10.39; `HiddenCharacters.cs`,
`InjectionDetector.cs`, `ContentScreener.cs`, `DerivedViews.cs`, `RiskFlag.cs`, `RiskAnnotations.cs`, `CanonicalStrings.cs`,
`FlagDisclosure.Normalize`, `IngestPipeline.cs` (screen and persist), `PostProjection.cs`, the provenance and acceptance
responses in `ForumEndpoints.cs`, `Passage.cs` and `PassageHeader.cs` (the `risk` lines), `Moderation.cs` (the
automated-actor table), `RedTeamCorpusTests.cs`, `ScreenerReferenceTests.cs`, `ScreeningCostTests.cs`, `ci.yml` (the cost
step), `conformance/red-team/` (all five files and the README). Computed, with Python 3.15.0 and Unicode 17.0.0: the
corpus' non-ASCII and Default_Ignorable_Code_Point counts, the 36 Markdown files' counts, the categories of the candidate
ranges, and the run lengths in the existing corpus.

**Not run:** any C# test, build or console; the HTTP path; a Postgres read of a stored `post.accepted`; the corpus
differential (it is Task 0). **Checked after drafting (2026-10-18):** the existing 17 and the candidate list together equal Unicode 18.0.0's
`Default_Ignorable_Code_Point` exactly (`DerivedCoreProperties-18.0.0.txt`, dated 2026-08-07: 4,174 code points, none
missing and none extra), and its Cn members in the admitted ranges are the 32 section 4 names. **Not checked:** the Unicode text on how
CLDR formats Arabic numbers (the claim that locale data places U+061C in them is from knowledge, not from a file read), and
whether any model reads tag characters (the premise is the exercise's and the literature's, not this stage's).
