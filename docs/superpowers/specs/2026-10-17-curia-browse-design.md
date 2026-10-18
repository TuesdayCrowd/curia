# `curia browse`: Design

> Repository path: `docs/superpowers/specs/2026-10-17-curia-browse-design.md`
> Errata: **G22**, filed 2026-10-17 (new requirements R10.72 to R10.75). Branch `curia-browse`, base `main` at 5e69df1.
> Seed: §9 and §2.4 of `docs/superpowers/specs/2026-10-06-the-readers-claims-are-true-design.md`. This document
> supersedes §9's header rows and re-derives the rest against `main` after rotation (#92) and compromise (#93).
> Plan: Tasks B0 to B7 in `docs/superpowers/plans/2026-10-06-the-readers-claims-are-true.md`.
> Every file:line below was read at 5e69df1. Nothing here was built or run; the screen claims are reasoning until
> Task B4 drives a terminal.
> Amended 2026-10-17 after B4: section 14 supersedes any earlier section it contradicts (named in each ruling).

## 1. What this is, and who it is for

`curia browse` is a reference reader that draws a **screen** for a **person** at a terminal. It is not for an agent,
and its duty to an agent is only not to mislead one that ends up on it.

| caller | runs | receives |
|---|---|---|
| a person at a terminal | `curia browse …` | the screen |
| an agent that calls tools | `curia-mcp` | unchanged |
| an agent or script with a redirected stream | `curia browse …` | the line form: `curia <verb> …`'s stdout bytes and exit code, plus one stderr line giving the actual reason |
| an agent holding a pseudo-terminal | `curia browse … --plain` or `CURIA_BROWSE=plain` | the line form, no stderr line; without either, the screen, whose title row says it is a screen and where the line form is |

**In scope.** Three views by argument only (`browse board <b>`, `browse thread <id>`, `browse read <id>`); scrolling,
Tab between passages, Enter on an index row, back, reload, `!` (the equivalent `curia` command), help, quit; a header on
every passage that says what this client established and what it did not; the screen model as pure code; the terminal
layer; the pty and emulator tests.

**Out of scope** (each is Hardin's H4 to H6 or later): a home view, prompts, a search view, any write, any identity,
`curia-mcp` changes, Windows screens, mouse, colour beyond three attributes, and a semantic change to any existing
reader's output. One exception to the last, in §8.4, is Task B1's step 0.

**Why a screen needs requirements at all.** Scrolled, a screen holds content without its header or warning; a capture
keeps characters only and a stripped-escape stream keeps byte order only; and a terminal decides widths, keeps cells a
TAB skipped, acts on UTF-8 continuation bytes as C1 controls under an 8-bit charset, and lets combining marks draw over
neighbouring rows. The envelope's inseparability from content (R10.18, property P22) must survive all of that. That
finding was held (the register's "A screen separates the envelope from the content") and is filed as errata G22.

## 2. Preconditions, and the revocation ruling

**Merged at 5e69df1** (`git log origin/main`): PR A, the reader stage, as #86 (52eea3a); rotation as #92 (f9dc1eb);
compromise as #93 (7921b4a); D49 (#96), D48 (#97), D45 (#98). `KestrelForumFixture` exists
(`tests/Curia.Api.Tests/Fuzz/RequestFuzzKestrelTests.cs:19`, used by `CliRevisionEndToEndTests`). The branch
`curia-browse` exists and is empty (`but status`).

**Ruling: PR B does not wait on revocation.** The plan's sentence "runs only after rotation and revocation merge" is
stale shorthand, and the register's "waits on rotation only" and "after PR 2" are the later and correct readings. Three
reasons, each read:

1. G21's table of annotations (`curia-whitepaper-ERRATA-AND-ADDENDUM.md`, "§4.4, R4.19") says "revocation" *is*
   retirement (R4.39) or a compromise declaration (R6.26 (revised)). Both merged. No third primitive is scheduled; the
   register's "key revocation (R4.19) does not exist yet" in `CLAUDE.md` is older than that annotation.
2. The header's key row is **data-driven from the key set's status vocabulary**. `ForumJwk.Status` documents
   `active`, `retired`, `compromised` and `unbound` (`src/Curia.Client/ForumDocuments.cs:234`-`:238`), and
   `Passage.Render` already ends in a branch for any other word (`Passage.cs:96`-`:105`). The screen's key row has the
   same last branch (§4.2), so a later status word renders as the Forum's word, labelled, with no code change.
3. Waiting on an unscheduled item would hold the screen behind work that may never exist. If revocation later adds a
   status, the row reads it as data and a new fact adds the case.

## 3. What rotation and compromise changed in what a reader shows per post

Read at 5e69df1.

- **The verdict has four outcomes, not three.** `CheckOutcome` gained `Disputed` (`ActaCheck.cs:47`). A passage's
  outcome is `Passage.Outcome` (`Passage.cs:55`): `Failed` when the span does not render the canonical form, otherwise
  `SignatureVerdict.ReadOutcome` (`SignatureCheck.cs:90`-`:94`): not-verified outcomes as they are, then
  `ReadFailedNote` (a compromised key's declaration is at or before the post) as `Failed`, then `DisputedNote` (the post
  is inside `[from_index, declaration)`) as `Disputed`. `ExitCode.ForOutcomes` ranks 6, then 8, then 9, then 0
  (`Cli.cs:68`-`:75`). `VerifyAllAsync` feeds it `passage.Outcome` and nothing else (`Program.cs:1052`).
- **A key row exists on the line form** only for a non-`active` status (`Passage.cs:96`-`:105`): retired at a log index;
  compromised at a log index with content disputed from another; any other word as "is marked". For an absent
  `curia_status` it prints nothing, which is also what `active` prints. The two are different claims (§4.2).
- **The key row is not a verdict.** A key the set calls `compromised` signed posts below `from_index` that stand
  (verified, exit 0), posts inside the window (disputed, exit 9) and posts at or after the declaration (failed,
  exit 6). The same key row sits over all three. D37's reopening records this (the `key_status` comment; the register's
  "Reopened and extended", fourth item).
- **Retired.** A signature that holds under a key the set says was retired at or before the post's log index reads
  `NOT VERIFIED` (`SignatureCheck.cs:230`-`:241`); before it, the key is still the right key and the post is verified.
- **Titles.** `TitledIndex` withholds a title unless `ReadOutcome` is `Verified` and writes `DISPUTED` in the signature
  word (`TitledIndex.cs:36`, `:56`-`:63`, `:93`-`:94`).
- **Exit 9 is already in every read verb** (`read`, `thread`, `verify`, `board --titles`, `search --titles`) and in
  `recheck` (D49). `curia-testis log author` exits 4.
- **A second Forum word exists.** A served post carries `disputed_by_log_index` in its provenance
  (`ForumDocuments.cs:556`, `Provenance.DisputedByLogIndex`). Only `recheck` reads it (`Program.cs:594`, `:600`). The
  read verbs derive `Disputed` from the key set alone. See §8.4.
- **`accepted`** is parsed (`ProvenancePost.Accepted`, `ForumDocuments.cs:191`) and printed by no reader (D36's note).
  Acceptance is a token-only write with no signed envelope (`ForumEndpoints.cs:369`-`:370`), so it is the Forum's word
  and nothing the client can check.
- **`--agent`** on a read verb turns on `ForeignWatch`: FOREIGN and MISSING lifecycle lines on stderr and exit 6
  (`Program.cs:993`-`:999`). The screen holds no identity (§6), so it cannot run the watch.

## 4. The header

### 4.1 The rows

A header is frame rows: each begins with this client's own text, and every value it did not compose is a display
literal (R10.63, R10.64). Row text that is already a client sentence is **reused, not reworded**: the verdict row is
`SignatureVerdict.DescribeRead` itself, so the screen and the line form cannot say different things about one verdict.

| row | content |
|---|---|
| H1 | `#3 post "<id>"  "<kind>"  board "<b>"` with ` parent "<p>"` when present, then the **outcome tag** (below) |
| H2 | `by "<author>"  (owner NOT verified)` or `(owner verified)`; ` owner "<o>"` when served; ` server_ts <instant>` |
| H3 | `digest sha256:…  (computed here)  kid "<kid>"`; a second row `the Forum reported a different value for digest: "<d>"` only when it differs |
| H4 | `signature ` + `DescribeRead`, then `checked here: the signature. Not checked here: inclusion, consistency, the key's binding and lifecycle (press ! for curia verify)` |
| H4b | only when the span check failed (R10.71): `content   FAILED: the Forum's own rendering of this post is not its signed bytes; this screen lays out the signed bytes, never the Forum's span` (one wording for a delimited and an undelimited span) |
| H5 | the **key row** (§4.2) |
| H6 | `forum says V<level>` then, each only when present: `; accepted (the Forum's word: no signature covers an acceptance)`, `; CONTRADICTED by "<digest>", …`, `; reproduced by "<digest>", …`, `; risk "HiddenText" x4` (category literal, count of that category's flags), `; marked disputed by log index N` (§8.4) |

The **outcome tag** ends H1 and is the same word H4 states, taken from `Passage.Outcome` (§4.3): nothing when
`Verified`; `[DISPUTED]`, `[NOT VERIFIED]`, `[COULD NOT BE CHECKED]` or `[CONTENT FAILED]` otherwise. Absence is not a
badge: H4 always states the word, and no field renders as a green badge or any other safety rating (R10.50). The tag
exists so that the one row a short window always keeps carries the verdict when the verdict is not the plain case.

A **pinned** header (a scroll that leaves the passage's top above the window) is the same rows with `(continued:
content rows a-b of n)` after H1; a passage's end in view is the frame row `end of #3`. The warning
(`Provenance.StandardWarning`, on the screen as on the line form, by `Passage.Standing`'s rule: this client's own text
when the Forum's equals it, the Forum's quoted beneath a line saying so when not) is on every screen that holds
content.

### 4.2 The key row

The key row states **facts about the key and the post's position, never a conclusion**. The conclusion is H4's.

| key set says (`ForumJwk` for the signing `kid`) | row |
|---|---|
| no member, or no matching key | `key "<kid>": the Forum's key set does not state a status` |
| `active` | `key "<kid>": active (as the Forum's key set says); this post: log index <n or "not stated">` |
| `retired` with an index | `key "<kid>": retired at log index <r> (as the Forum's key set says); this post: log index <n>` |
| `compromised` with both indices | `key "<kid>": compromised, declaration at log index <d>, content disputed from log index <f> (as the Forum's key set says); this post: log index <n>` |
| `retired` or `compromised` missing an index, or any other word | `key "<kid>": marked "<status>" (as the Forum's key set says); this post: log index <n>` |

Two choices are made on purpose:

1. **Absent is not `active`.** The line form prints nothing for either (`Passage.cs:96`), so a reader cannot tell a Forum
   that says `active` from one that said nothing. A person cannot diff two outputs, so the screen always prints a
   row, and the first row above is its own sentence. A screen must not claim more than the line form established.
2. **The post's log index is on the row.** A key `compromised` at 40 with content disputed from 30 reads the same over
   a post at 12 (stands), at 35 (disputed) and at 41 (failed). Without the index the row looks like a verdict and
   the person cannot see why H4 differs. The screen does not compute which of the three applies; `DescribeRead` does.

The status vocabulary is read from the document and not enumerated by the screen, so only the three states with
indices have a template and every other word falls to the last row.

### 4.3 One outcome

The tag, H4, the board index's signature word, the content rule (§5.4) and the session's exit code (§8) read **one**
value, `Passage.Outcome`. A header that derived its word from the key row, from `Verdict.Verified`, or from its own
comparison would be a second implementation of R6.57 inside the screen.

### 4.4 The board index row

`#3 post "<id>"  "<kind>"  <server_ts>  signature <word>` as `TitledIndex.Write` writes it, then the title as content
only when `Passage.Outcome` is `Verified` and `TitledIndex.Title` returns one; otherwise the same explanatory line
`TitledIndex` writes (`(no title: …)`, including the disputed text). The index holds the information `board --titles`
holds (agent-user F1 parity): every id and title on the screen appears in `TitledIndex`'s output for the same posts.

## 5. The screen model

### 5.1 Views and keys

`browse board <b>`, `thread <id>`, `read <id>`. Keys: arrows, `j`/`k`, PgUp/PgDn, space, Home/End (scroll); Tab (next
passage); Enter (open the index row's post as a read view, from the passages the index already verified: no second
fetch); Backspace (back); `!` (show the equivalent command through `Hints`; `curia verify '<id>'` for the topmost
passage; withheld by `Hints.Withheld`'s rule if the id holds a character a shell could act on, R10.65); `?` help; `r`
reload; `q`, Ctrl-C, Ctrl-\ quit; Ctrl-Z shows "suspending is not supported here". The view is a closed hierarchy with a
`Match` method (CS-11), so a view or transition added later breaks every call site, including the exit table in §8.2.

### 5.2 Layout from the canonical form

Content is laid out from `SpanCheck.CoveredForm(post)` (R10.71's last sentence), never from `Post.Rendered`: members in
canonical order, string bodies split on line feeds, arrays as `- ` items, objects indented. Per row: TAB expands to the
next multiple of 8; every Cc, Cf, Zl, Zp, Co, Cn, unpaired surrogate and baseless mark is escaped the way R10.67 writes
an escape (`\u` and four lowercase hex digits per UTF-16 unit); each Mn or Me after the Nth on one base is escaped
(N is 4, provisional: Task B1 step 0 measures Mn and Me separately and does not cap Me at zero, since U+20E3 is honest);
a wrap never splits a base from its marks; the width estimate is 1 for printable ASCII and 2 for everything else, so it
never undercounts. These are R10.75.

### 5.3 The row is the boundary

Content rows begin `" 3| "`, a wrapped continuation `" 3+ "`; the pattern is `^ *[0-9]+[|+] ` and no frame row matches
it. A content row's text holds no character a terminal acts on, so nothing in it can begin a row of this client's.
Literals are never cut: a frame row longer than the width wraps on its own continuation rows and each literal is whole
(R10.72).

### 5.4 When content is drawn

- **Verified or Disputed:** laid out under the passage's header. A disputed post is served and marked (R6.57), and a
  person may need to read it to know what not to cite; H4 and the tag say so.
- **Failed or Could-not-be-checked:** **no content**. A frame row says the signature did not verify (or could not be
  checked), that nothing it would cover is shown (R10.71), and names `curia read '<id>'` to see the Forum's span under
  its own failing line. *This is stricter than `read`*, which prints the span under a failing verdict. R10.71's last
  sentence names "a layout for a person" and says not at all when the check did not verify; `read`'s span path
  predates it and is not changed here.
- **Too small:** content is drawn only when, at the current width, at least 8 content rows remain after the title, the
  warning, the marking row, the status row and the tallest header; otherwise only this client's rows, saying so, and the
  line form's command. The minimum is computed from the header actually present (a 300-character author wraps and
  raises it), never a constant. A person whose window is too small for a hostile header gets the line form's command
  and not a clipped screen (§11, risk 3).
- **The marking row** is `content shown here is laid out from the signed bytes and is not datamarked: for a model, use
  curia read`. (The seed's text, `run curia --plain`, named a flag `curia` does not have.)

### 5.5 Stream order

Rows are written top to bottom (title, warning, marking row, then the viewport, each header before its content), then
every frame row in view is written **again**, then the status row last; each row as `Address`, `Plain`, text,
`EraseToEndOfRow`. The second pass is why a terminal that skips cells (TAB, a wide character) cannot leave an earlier
row's text standing inside a frame row. The status row always shows position, says `wide characters on screen: a row
may be cut at the right edge` when any non-ASCII scalar is in view, and is at most `MinimumWidth - 1` columns.

## 6. The terminal

As the seed's §9, unchanged except where marked.

- A closed `Vt` vocabulary of `const` strings: alternate screen on and off, wrap off and on, cursor hidden and shown,
  clear, home, address, erase to end of row, three attributes, keypad transmit on and off (the runtime's `smkx`/`rmkx`).
  Wrapping off. Every row addressed. `Leave()` is `Plain`, home, clear, wrap on, cursor shown, `rmkx`, alternate screen
  off: erasing first, so a terminal that ignores mode 1049 keeps nothing.
- `DrawsAScreen(args, env)` returns a reason: both ends terminals, a UTF-8 codeset (`LC_ALL`, then `LC_CTYPE`, then
  `LANG`), `TERM` on the allowlist (`xterm`, `xterm-*`, `screen`, `screen-*`, `tmux`, `tmux-*`, `alacritty`, `foot`,
  `wezterm`, `rxvt-unicode*`; owner question 10 of the seed, default) or `--screen`, not Windows, no `--plain`,
  `CURIA_BROWSE` other than `plain`. **New rows, from §3:** `--agent` and `--marking` also decline the screen, with the
  reason stated on stderr. `--agent` runs a foreign-act watch and reads a profile, and the screen holds no identity;
  `--marking` selects a transformation the screen does not apply. `--forum` and `CURIA_FORUM` are accepted.
- Input thread into a channel; SIGWINCH, SIGTERM, SIGHUP, SIGQUIT, SIGINT, SIGTSTP and SIGCONT post to the same channel
  (registered only when drawing); only the loop writes; `Leave()` is written once, from `finally`, and `Output.Draw`
  throws after it; `TreatControlCAsInput = true` while drawing; `Size()` is read before every draw; every exception is
  caught and reported quoted after `Leave()`.
- Fences, read from IL: `System.Console` members only from `Out`, `Error`, `ReadKey`, `KeyAvailable`, `WindowWidth`,
  `WindowHeight`, `IsInputRedirected`, `IsOutputRedirected`, `TreatControlCAsInput`; the screen holds no identity
  (`ProfileStore` and the signer are absent from its assembly's reachable types), writes nothing, starts no process; every
  `Vt` string is `const`. These are R10.73.
- Residuals, in the help with `reset` / `tput rmcup; tput smam; tput cnorm` as the remedy: SIGKILL, stack overflow, a
  backgrounded `browse … &`; bidirectional terminals reordering a row (looked at in B4); blank-looking letters and glyphs
  that draw past their cell; auto-linked URLs; scrollback and recording. The help names `tmux capture-pane -p -J -S -`.

## 7. The line form

Dispatched after `Main`'s 2-minute token, reusing the verbs' handlers: `browse board b` is `board b --titles`, `browse
thread t` is `thread t`, `browse read r` is `read r`. Identical stdout bytes and exit code; stderr gets one line giving the
reason (none under `--plain`). A parity fact runs both through the real binary, including a stalled-stub row with the
same exit code as `curia`'s.

## 8. Exit codes, and exit 9's coverage

### 8.1 The contract

- The screen's exit code is `ExitCode.ForOutcomes` over **every passage displayed at any time in the session**, so a
  person who quits after seeing a disputed post gets 9, and one who saw a failed post gets 6 even after scrolling away.
  A reload adds outcomes and never removes one.
- An initial load that is refused exits as `ExitCode.For(refusal)` does, with the refusal printed on the line form's
  stderr, before anything is drawn. A load refused after the screen is up (Enter, `r`) shows its summary on the status
  row, and its code joins the session's by the rank 6, 8, 4, 5, 9, 0 (a check that failed or did not run outranks a
  refusal, which outranks a mark). The rank is provisional and stated in the help.
- Quitting by `q`, Ctrl-C or Ctrl-\ exits with that code. Death by SIGTERM or SIGHUP exits `128 + n`, after `Leave()`.
  Exit 1 is a usage error before the screen is drawn.
- The help's EXIT CODES entry for 9 gains `browse`.

### 8.2 Every surface the screen adds, against exit 9 (D49's item 4)

A surface that omits this rule fails silently, which is what D49 was. The table is a fact over the view hierarchy's
transitions (§5.1), so a transition without a row breaks the build.

| surface | where its post comes from | outcome counted |
|---|---|---|
| `browse read` initial view | `VerifyAllAsync` | yes |
| `browse thread` initial view, every passage | `VerifyAllAsync` | yes, each |
| `browse board` index, every row | `VerifyAllAsync` over the whole board | yes, each (a title withheld is still counted) |
| Enter on an index row | the passage the index already verified | already counted; no second fetch |
| Tab, scroll, Home/End, `?`, `!` | no load | none |
| Backspace | a cached view | already counted |
| `r` reload | a fresh `VerifyAllAsync` | yes, added |
| the line form | the verbs' handlers | their existing coverage (D49, G21 PR 2) |

### 8.3 What the screen does not claim

Everything the line form's `read` establishes: the signature over re-canonicalized bytes against the Forum's key set, the
key's status as the Forum's word. Everything `curia verify` establishes (inclusion, consistency, the key's binding and
lifecycle from the log) is **not run on a screen**, and H4 says so on every passage. The screen never tells a person a
post is safe, citable or the author's.

### 8.4 Task B1's step 0: the Forum's second word

Today `recheck` exits 9 on a post whose provenance carries `disputed_by_log_index` and the read verbs do not read it
(§3). In an honest Forum the key set and the post's marking come from one fold and agree. Where they disagree, the read
verbs report the key set's word and a reader of the screen would report whichever it was built on. **Default: fold it.**
Step 0 of Task B1 adds a failing fact first (a served post whose provenance marks it disputed while the key set says
`active`), then makes `Passage.Outcome` `Disputed` for it, and makes `Render`, `TitledIndex.Write` and the screen's H4
take that word from `Passage.Outcome`; H6 then says `marked disputed by log index N`. This touches `curia-mcp`'s
reported outcome through the same property. It is the one change to an existing reader's meaning in PR B, it is its own
commit, and the owner may strike it (§12, question 1); if struck, the screen shows the H6 text, exits by
`Passage.Outcome` as before, and a register entry records that the read verbs and `recheck` disagree.

## 9. How it stays within R10.63 to R10.71

| rule | how |
|---|---|
| R10.63 / R10.64 | every served or chosen value on a frame row is a display literal through `FrameText`; the client's sentences are `OwnText` from constants. `ScreenRow` text is built only from those, never from a `string` parameter (the `[ConstantExpected]` rule `Output` follows) |
| R10.65 | `!` prints a command only through `Hints`; a value with a character a shell could act on withholds the command and says where the value is |
| R10.66 | `browse` takes a name as the verbs do: the display literal it printed is accepted back |
| R10.67 | stronger on the screen: also Co, Cn, TAB (expanded, not passed), marks beyond the cap and baseless marks; the same escape spelling |
| R10.71 | content is laid out from the canonical form the signature covers; none when the check did not verify; the span is never drawn, so a mismatch is a header row (H4b) and exit 6, not a quoted literal |
| R10.72 to R10.75 | G22 |

## 10. Tests

**Approach (the plan's).** Pure code first: `ScreenRow`, `Screen`, `CellWidth`, `ContentLayout`, `PassageHeader`,
`Viewport`, driven by CsCheck over a hostile pool (TAB, U+009B, `" 3| "` and `" 3+ "` at a line start, 40 marks, baseless
marks, U+E000, an unassigned code point, a 300-character author, 3,000 lines). Then the terminal layer against a
test-only `TestTerminal` emulator (tab stops, wrapping on and off, cell widths 1 and 3, a mode that ignores 1049). Then
real pseudo-terminals through `tests/Shared/pty-run.py` (`--signal NAME@ms`, `--resize C×R@ms`; `python3` is already a
gate prerequisite). Then an independent emulator (`pyte` in a scratch venv or `tmux capture-pane`; record which and its
version) and a human look in two terminal applications, plus VTE or Konsole with rows beginning in Hebrew and in Arabic.

**Facts** (about 28; the plan's 22, plus six this re-scope adds, marked +):

- B1, pure: every row begins with the reader's text and every content row with its gutter (all sizes at or above the
  minimum, every offset; no Cc, Cf, Zl, Zp, Co, Cn, unpaired surrogate or leading mark in a content row; non-vacuity: a
  content row and a continuation row exist); header and warning on every screen with content, an `end of #k` row where a
  passage ends in view; eight content rows fit at every size above the minimum; a literal is never cut; the fifth mark on
  a base is an escape, astral bases included, a base and its marks stay on a row; the width estimate never undercounts;
  the screen lays out the canonical form and not the span; the board index holds what `TitledIndex` holds.
- **+ B1, header:** the key row is identical over a post that stands, a disputed one and a failed one under one
  compromised key, while H4 and the tag differ and equal `Passage.Outcome`'s word; **+** a key set that states no status
  prints the "does not state a status" row, never `active` (§4.2 choice 1); **+** the retired, compromised and
  unknown-word templates; **+** a Failed and a Could-not-be-checked passage draw no content (§5.4); **+** `accepted` is
  printed as the Forum's word and only in H6; **+** (if §8.4 stands) the provenance-marked post reads disputed on
  the screen, `Render` and `TitledIndex`.
- B2, terminal: the byte vocabulary (every ESC begins a `Vt` constant or an `Address`; the decoded stream has no C1 and no
  Cc but ESC); stream order; `TestTerminal` (no scroll, rows begin as drawn, a stale-cell TAB case, nothing left after
  `Leave()` in the 1049-ignoring mode, no attribute carried into content); the `DrawsAScreen` truth table with a reason
  per row, including the two new rows; process parity with the line form; the fences. **+** the surface table of §8.2
  is a fact over the view hierarchy.
- B3, pty: Ctrl-\ exits within 2 s ending with `Leave()`; Ctrl-Z shows its sentence; SIGTERM without a key exits within
  2 s; SIGHUP after the master closes exits within 2 s; a resize to the computed minimum with no key redraws a full frame
  within 500 ms. **+** a pty session that opens a disputed post (the permissive-gate composition `CompromiseDeclarationTests`
  uses) and quits exits 9; one that opens a failed post exits 6.
- B4: the 80×24 fact over a 3,000-line hostile answer; RED with `DrawsAScreen` inverted, then GREEN.

## 11. Falsification

FB1 to FB22 as the plan lists them (FB18 split into a and b), plus:

| # | Patch | Must go red |
|---|---|---|
| FB23 | H4 and the tag derived from the key row's status | header-word fact (the same key row over three outcomes) |
| FB24 | an absent `curia_status` rendered as `active` | the unstated-status fact |
| FB25 | the Disputed word dropped from the tag or H4 | header-word fact; the pty exit-9 fact |
| FB26 | session exit taken from the last view only | the pty exit fact (disputed seen, then another view) |
| FB27 | Enter on an index row skipping the outcome table | the surface-table fact |
| FB28 | content drawn for a Failed passage | the no-content fact |

Each is applied, the named fact seen red, the patch restored by a plain copy (never `git checkout --`), the file's mtime
bumped, a rebuild with `--no-incremental` and the suite run unpatched once. The count is 29 cases, or 28 if §8.4 is
struck. FB23 to FB28 are the vacuity questions this re-scope raised: the key row cannot be allowed to *look* like the
verdict (question 4, same-artifact derivation), nor an absent status like an `active` one (question 3).

## 12. Risks, and the owner's questions

**Risks.**

1. **The first terminal claim is unproved until B4.** Mode 1049, `smkx`, wrapping off and the stream order are reasoning
   from the seed's probes P1 to P5 (macOS only; Linux not run). Task B4's emulators are the first real observation. If a
   claim fails there, the fix is in this document before it is in code.
2. **Bidirectional terminals** (VTE, Konsole) reorder a row beginning in Hebrew or Arabic; the gutter may move. B4 looks
   and the help states the residual; the design cannot prevent it.
3. **A hostile header can exceed the window.** A 300-character author, `kid` or board name wraps to many rows; at 80×24
   the computed minimum may not be met and the screen says "too small" for that post. That is the honest result, and the
   line form's command is on the row.
4. **`--agent` declines the screen.** A person who habitually passes it gets the line form and a stderr reason. A flag
   the screen cannot honour cannot be quietly dropped. `curia keys` is how a person checks lifecycle acts.
5. **The cap on marks (4) is a guess** until B1 step 0 measures it against `benign.jsonl` and `known-false-positives.jsonl`.
   It is provisional in R10.75 and G22 is amended with the measured number before PR B merges.
6. **Size.** Eight tasks, about 28 facts, 29 cases, two pty helpers and a test emulator: the plan's honest size plus six
   facts. Anything that grows it further (a search view, a home view) is a new stage.

**Questions only the owner can answer** (each has a default; none blocks B1).

1. **§8.4: fold the post's `disputed_by_log_index` into every reader's outcome?** Default **yes**, as Task B1's step 0:
   a post the Forum marks disputed is not reported citable on any surface, which is D49's property. It changes
   `curia-mcp`'s reported outcome in the Forum-disagrees case.
2. **§5.4: the screen shows no content for a failed or unverifiable post, where `read` shows the span under the
   failing line.** Default **yes** (R10.71's last sentence, literally). The alternative is parity with `read`.
3. **§6: `--agent` and `--marking` decline the screen.** Default **yes**. The alternative is accepting `--agent` for
   Forum selection only and saying on the status row that the foreign-act check did not run.
4. **Seed question 10 (the `TERM` allowlist) and question 8 (Windows: line form only)** stand at their defaults.

## 13. How this was checked, and what was not

Read at 5e69df1: the plan's PR B block and the seed §9 and §2.4; `Passage.cs`, `SignatureCheck.cs` (the verdict, the
key row inputs, `ReadOutcome`), `TitledIndex.cs`, `Cli.cs` (`ExitCode`, `Output`), `Program.cs` (`Main`, `ReadAsync`,
`ThreadAsync`, `BoardAsync`, `RenderAsync`, `VerifyAllAsync`, `RecheckAsync`), `ForumDocuments.cs` (`ForumJwk`,
`ProvenancePost`), `Help.cs` (EXIT CODES); G19, G21 and its annotation table, the consolidated index; the register's D36,
D37 and D49 entries and the held entry. Nothing was run or built.

**Not checked:** `Frame.cs`'s `FrameBuilder.Span` and `SpanText.Line` (the plan's FB10 depends on their exact
behaviour); `ProgramOutput.cs`; `Curia.Mcp`'s use of `Passage` and `Provenance` (relevant to §8.4's reach); `Help.cs`
beyond EXIT CODES; whether `ForumJwk` for a key in a cascade carries the indices the key row needs for a cascaded key
(`ForumDocuments.cs:242`-`:245` says it does; the server side was not read); no terminal, emulator or pty was driven;
the Linux behaviour of every terminal claim; `tests/Curia.Client.Tests` (whether a fixture already builds a compromised
key set the header facts can reuse).

## 14. Rulings after B4 (2026-10-17)

Made by `curia-architect` on the deviations and findings of B1, B2/B3 and B4 (commits 3f89aee to eee334e), read against
`src/Curia.Client.Cli/Screen/` and `src/Curia.Client/Passage.cs`. Where this section contradicts an earlier one, this
section governs, and the earlier section is named. Row counts below were computed with a Python port of `RowWrap.Frame`
over the literals in B4's capture (a 26-character post id, a 36-character author URI, a `kid` of 12 characters); they are
reasoning, not a run of the binary. The B1 facts below are the probe that settles each one.

### 14.1 B4 finding 1: at 80 by 24 no post's content is shown

**Ruling: fix it in this PR, before B5.** Three reasons. (a) B5 falsifies the final tree; a layout change after it voids
FB1 to FB3, the header facts, and every pty fact, and they would be run twice. (b) The disputed and failed pty cases were
moved to 120 by 50 (B2/B3 item 15) and the hostile case to 80 by 28 (B4) to get content on the screen: a fact whose size
is chosen so that it passes is a fact about the size, and 80 by 24 is the size a person gets (vacuity question 2). (c) The
fix needs no new requirement, only a compact form that R10.74 already binds.

**Why 28.** At 80 columns the chrome is title 2, warning 3, marking 2 and status 1 = 8 rows, and a header is 11 to 12
(H1 2 with a parent, H2 2, H3 2, H4 2, the checked row 2, H5 1 to 2, H6 1). 8 + 12 + 8 = 28.

**The change, five parts.**

1. **Chrome is 6 rows at 80 columns.** The warning stays 3 rows: it is the Forum's published text (`Provenance.StandardWarning`,
   R10.49) and the screen does not shorten what the Forum is bound to say. The title and the marking row each become one
   row at 80 columns (at most 80 cells; they wrap, computed, below that):
   - `Screen.Title` = `curia browse: a screen for a person. A model or script: a curia verb or --plain` (79 cells).
     It still says it is a screen and where the line form is; it no longer names `curia read` for a board.
   - `Screen.MarkingRow` = `content is the signed bytes laid out, not datamarked: a model uses curia read` (77 cells).
2. **H4's second row becomes a single row and states only what R10.74 requires, the unchecked list.** Replace `checked here:
   the signature. Not checked here: inclusion, consistency, the key's binding and lifecycle (press ! for curia verify)` with
   `Not checked here: inclusion, consistency, key binding, lifecycle (! for verify)` (79 cells). The signature row already
   says `verified locally`; "checked here: the signature" restated it. This supersedes §4.1's H4.
3. **A compact header form.** The screen has two header forms and uses **one of them for the whole document**, chosen from
   the width and height alone (so scrolling never changes a header's form and a resize is the only thing that does):
   the full form when `Height >= chrome + tallestFullHeader + 8`, otherwise the compact form. The compact form is the full
   form without H3's `digest ... kid` row and without ` owner "<o>"` and ` server_ts <t>` on H2. Everything else stays,
   in particular H1 with its outcome tag and `(continued ...)`, H2's `by "<author>"  (owner [NOT ] verified)`, the verdict
   row (`DescribeRead`), the not-checked row, the key row (§4.2) with the post's log index, H6 in full (level, acceptance,
   contradiction, reproduction, risk flags, disputed mark) and, when the Forum's digest differs from the one computed here,
   the `the Forum reported a different value for digest:` row, which is a warning and is never behind a window size. The
   pinned header is the same form with `(continued ...)`. The rows dropped are not in R10.74's list; the digest and
   `server_ts` are on the full header and in `curia read` (and `server_ts` on the board index row), and the help says the
   compact form exists (14.7).
4. **The minimum is computed per form, and the too-small screen says what it needs.** `MinimumHeight` is
   `chrome + tallestCompactHeader + 8`. The too-small sentence becomes `this window is too small to draw content under its
   header: it needs at least <R> rows and <C> columns at this width. Make it larger, or use the line form: <command>`, with
   `<R>` the computed minimum at the current width and `<C>` `MinimumWidth`. A person told only "too small" cannot act; a
   person told the number can.
5. **A document with no drawable content needs one row, not eight.** `MinimumContentRows` (8) applies when any passage in
   the document draws content; a document whose passages are all Failed or Could-not-be-checked needs the chrome, the
   tallest header and one row for the frame row that names `curia read`. (As built, such a document at 80 by 24 is reported
   "too small to draw content" when there is no content to draw.)

**Numbers (to be confirmed by the new fact, not trusted from here).** Verified post with a parent and an active key, at 80
columns: chrome 6, full header 11 (minimum 25), compact header 8 (minimum 22). So at 80 by 24 the compact form is used and
10 content rows show; at 80 by 25 the full form. A disputed post under a compromised key, with a parent: the verdict sentence
is 5 rows at 80 and the key row 2, so the compact header is 12 and the minimum 26: **a disputed post at exactly 80 by 24 is
still "too small", and says it needs 26.** That is accepted and recorded as a residual, not fixed: its lever is a shorter
verdict row for a disputed post, which would be a second wording of R6.57's sentence (§4.1's "reuse, not reworded" exists
to prevent that) and so a decision of its own. The disputed and failed pty cases run at 80 by 30.

**New facts (B1, pure):**

| Fact | Asserts |
|---|---|
| `…_AtEightyByTwentyFourAVerifiedPostWithAParentDrawsAtLeastEightContentRows` | a verified, parented, active-key passage at `ScreenSize(80, 24)` draws content, at least 8 Content rows, header and warning present; and at `ScreenSize(80, Minimum - 1)` draws none and says `needs at least <Minimum> rows`. Non-vacuity: the same passage at 80 by 25 uses the full form (the digest row is present) and at 80 by 24 does not |
| `…_ChromeIsSixRowsAtEightyColumns` | title, warning, marking and status are 1 + 3 + 1 + 1 rows at width 80 |
| `…_TheCompactHeaderStatesEverythingR10_74Requires` | for each of the four outcomes, an active, a retired, a compromised, an unstated and an unread key set, and an accepted passage: the compact form holds the outcome word equal to `Passage.Outcome`'s, the key row exactly as the full form's, the not-checked row, `accepted (the Forum's word ...)` when `Accepted`, and every risk flag; the compact and full forms differ only in H3 and H2's owner and `server_ts`. The expected strings are the published constants of R10.74's list, not the header's own output (vacuity question 4) |
| `…_OneFormForTheWholeDocument` | at a given size every passage of a thread uses the same form, and scrolling does not change it |
| `…_ADocumentWithNoDrawableContentNeedsOneRow` | all-Failed document at 80 by 24 draws header and the `curia read` frame row |

### 14.2 B4 finding 2: the status row must always say how to leave

**Ruling: change.** `Screen.cs:271` drops `q quits  ? help` when a wide character is in view, so the screen can be on a
terminal with no stated way out, and `pty-run.py`'s `--ready "q quits"` never appears: a harness hang with no failing
assertion. The status row is built from parts in a priority order and a part is dropped only whole, from the end:
position, then `q quits`, then the wide-character notice, then `? help`. The notice's text is `wide characters on screen: a
row may be cut` (R10.75 quotes `wide characters on screen`; this keeps those words and drops "at the right edge", which
supersedes §5.5's wording). At 80 columns: `rows 1-25 of 25  q quits  wide characters on screen: a row may be cut  ? help` is 77
cells and fits in the 79 the status row has. Below that width the notice shortens to `wide characters on screen`, then
`? help` goes, then the notice goes; **`q quits` is never dropped** and the too-small screen carries it too (it does).

Facts: `…_EveryStatusRowStatesHowToLeave` over every document, size and offset, with a wide character in view (non-vacuity: a
case where the notice is present and a case where `? help` has been dropped); and the pty harness keeps `--ready "q quits"`,
which this makes true of every first frame.

### 14.3 B4 finding 3: End must show the last page

**Ruling: change.** `ScrollBy` clamps to `DocumentRows - 1`, so `End` leaves a window of one row (`end of #1`) and no content,
and so does `j` held down. A pager stops when its last row is at the bottom. The maximum offset is
`max(0, DocumentRows - capacity)`, where `capacity` is the window's rows less the pinned header's rows at that offset (so
the last page is a full one with its pinned header). `Home` and `Tab` are unchanged. Fact: at every size above the minimum,
`End` shows the document's last row on the window's last row and at least `min(8, the last passage's content rows)` Content
rows, or the whole last passage when it is shorter; B1's "eight rows fit" fact is extended to the End offset, which is the
offset it did not cover.

### 14.4 B4 findings 4 to 8

- **4, a mark at the start of a body line attaches to the layout's indent. Accept; no change.** A mark on a space is a mark
  on a base, bounded by the same cap as a mark on a letter. The gutter (`" 3| "`) is never modified: the row's text begins
  after the gutter's own trailing space, and a mark that would be first in the text is escaped as baseless (R10.75). Recorded
  in the help's residuals: a mark on a layout space draws as it would on any base.
- **5, the header lists the Forum's risk flags with counts. Accept.** The set of categories is the detector's closed
  vocabulary, so the row's length is bounded; the minimum is computed from the header present, so a long one yields the
  too-small screen and not a clipped one. The compact form keeps the flags (they are the Forum's warning about this content).
- **6, the hostile answer is posted in-process by a T1 agent with an attested owner. Accept.** A CLI `answer` cannot cross the
  48-hour tenure (R7.17, provisional) in real time. B6 records that the fixture is in-process and why.
- **7, `pyte` 0.8.2 and `wcwidth` 0.9.2 found nothing. Accept as the first independent observation, and re-run it.** It looked at
  the layout this ruling changes. Before B6 records it, the replay (`replay.py`) is run again over the post-14.1 captures at 80
  by 24 and 80 by 28, and B6 records the versions and both runs.
- **8, the human look in two terminal applications and VTE or Konsole was not done. Accept as the owner's, and not a merge
  blocker,** on the condition that B6 and the PR body say it was not done and the help's residuals name bidirectional
  reordering (spec 6, risk 2). A claim that was not observed is stated as not observed.

### 14.5 B1 deviations

1. **Tests after code. Accept.** Nothing to change; B5 is where it is paid for. B5 runs on the final tree, which is why 14.1 to
   14.3 come first, and "M02 first green until re-patched" is exactly the shape B5 exists to catch.
2. **The model is in `Curia.Client.Cli`, internal. Accept, with one fact.** It shares an assembly with `ProfileStore` and the
   signer, so R10.73's identity fence cannot be "the assembly's reachable types". `BrowseFenceTests.ScreenTypes` is a hand
   list, and a screen type added and not listed is outside the fence silently (vacuity question 3). Add
   `…_EveryTypeInTheNamespaceIsClassifiedAsScreenOrNot`: every type in `Curia.Client.Cli` is in `ScreenTypes` or in a named
   `NotScreenTypes` list with a reason per entry, so an unclassified type fails the build.
3. **`Passage.SignatureOutcome` beside `Outcome`. Accept; it is one derivation, not two.** `Outcome` is defined as
   `SignatureOutcome` unless the span does not render the canonical form, so the two cannot disagree where it matters, and
   `SignatureOutcome` is the component R10.71's content rule names (the signature the content is covered by). §4.3 is
   restated: **every word and every exit reads `Passage.Outcome`; the content rule alone reads `Passage.SignatureOutcome`,**
   so that a post whose signature held and whose span did not draws its canonical form under `[CONTENT FAILED]` and exit 6.
   Two edits keep that true. `PassageHeader.Tag` switches on `passage.Outcome` once the span renders (it equals
   `SignatureOutcome` there), so the tag cannot be patched apart from the word. And a fact pins the relation:
   `Outcome` is `Failed` exactly when the span does not render or `SignatureOutcome` is `Failed`; content is drawn exactly
   when `SignatureOutcome` is `Verified` or `Disputed` (FB37).
4. **Eight content rows for the first passage in view, `min(8, its remaining rows)`. Accept.** §5.4's "at least 8" is read so.
5. **`MinimumWidth` = 72. Accept.** §5.4 gains it; below it only the screen's own rows, `q quits` and the command.
6. **The pinned range is zero-padded to the width of N. Accept.** It keeps the header's height still while a number changes.
7. **Board view: minimum is chrome + tallest index row + 1. Accept.** An index row is its own header and its title its
   content, so "eight rows of the first passage's content" is `min(8, 1 or 2)` for a board; this is 14.5 item 4's rule, not an
   exception. §5.4 says so.
8. **The key row for an unreachable key set. Change.** `Verdict.KeyStatus` is null both for a key set that was read and said
   nothing and for one that was never read, and the row says `the Forum's key set does not state a status` for both. For an
   unread key set that is a false statement of fact, not merely an imprecise one: the screen claims to have read a document
   it never obtained. A row exists for the case: when `Verdict.CouldNotCheck` and `KeyStatus` is null, the row is `key
   <kid>: this client has no key set to state a status: the check did not run (see the signature row)`
   (`PassageHeader.NoKeySetSentence`, an `OwnText` constant; `kid` null is shown as a display literal as everywhere). When
   `CouldNotCheck` and a status is present (an unparseable `server_ts` after the key set was read), the status row is written
   as for any status. R10.74 carries the clause (errata G22, amended). §4.2's table gains this row. Fact:
   `…_AnUnreadKeySetIsNotShownAsStatingNone` (non-vacuity: the unread, the absent-status and the `active` rows are three
   different strings). FB34.
9. **Viewport folded into `Screen`, `IndexRow` a formatter. Accept.** The plan's `Viewport` type is not required; its facts
   are on `Screen`.
10. **Spacing combining marks (Mc) are not counted toward the cap. Accept.** An Mc advances the cursor, so it takes a cell
    and the width estimate already counts it as 2; only Mn and Me draw over a neighbour. Recorded in §5.2 with the step 0
    measurement.

### 14.6 B2 and B3 deviations

1. **Ctrl-\ under .NET. Accept the key match; replace FB17's carrier.** With `TreatControlCAsInput` the runtime reads Ctrl-\
   (0x1C) as `ConsoleKey.D4` with Control and no character, and the match covers `\u001c`, `D4` and `Oem5`. Because SIGINT
   and SIGQUIT are registered, a screen without `TreatControlCAsInput` also quits on Ctrl-C and Ctrl-\ through the signal path,
   so **the patch the plan called FB17 (the property not set) is an equivalent mutant: it changes nothing observable and no
   fact can go red on it.** It stays in the code as defence in depth and is recorded as such, not as falsified. The real
   carriers: FB17a removes Interpret's `\u001c`, `D4` and `Oem5` branches (Ctrl-\ pty fact red); FB17c removes the SIGINT and
   SIGQUIT registrations and the property together (the Ctrl-C and Ctrl-\ pty facts, which assert the stream ends with
   `Leave()`, red). Linux was not run: CI is the first Linux run of the pty facts, and its result for the Ctrl-\ fact is read
   at B7, not assumed. `pty-run.py`'s docstring still says Ctrl-C and Ctrl-\ are signals the line discipline raises; with
   input mode raw they are bytes, so the docstring is corrected in B5.
2. **Row bytes `Address, Plain, attribute, text, EraseToEndOfRow, Plain`. Accept.** §5.5 and §6 are read so; the trailing
   `Plain` keeps an attribute (Bold title, Reverse status, Dim `end of #k`) out of the next row; FB22 is worded for it.
3. **The status row is addressed at the terminal's last row, rows between written blank. Accept.**
4. **`--plain` with `--screen` is a usage error, exit 1. Accept.** §6's truth table gains the row.
5. **An empty board writes the line form's "no posts" and exits 0, no draw. Accept.**
6. **Loads time out per load (2 minutes), not by `Main`'s token. Accept the timeout; change what can interrupt a load.** Main's
   token would end an interactive session after two minutes, which is wrong. But as built the loop `await`s the load, so while
   a load is in flight only SIGTERM and SIGHUP are heard; `q`, Ctrl-C, Ctrl-\, SIGINT and SIGQUIT wait up to two minutes behind
   a hung Forum, which is a screen that does not answer `q`. R10.73 asks that the terminal be restored on every exit path
   including the signals it can catch; it does not say how long. **Change:** while a load is in flight the loop keeps reading
   events; `q`, Ctrl-C, Ctrl-\, SIGINT and SIGQUIT cancel the load and leave with the ledger's code, and any other key is
   discarded. The status row says `loading from the Forum: q cancels` while it is in flight. Pty fact: a stub that accepts
   and never answers, `r` then `q`, exits within 2 s with `Leave()` last. FB36. The initial load (before the screen is up)
   is left as it is: nothing is drawn, the platform's default for SIGINT and SIGTERM applies, and nothing needs restoring.
7. **No true stalled-stub parity row. Accept.** The accept-and-hang-up row (both exit 8, identical stdout) stands in; §7's
   "stalled-stub row" and the plan's B2 sentence are read so. A real stall costs about 30 s per run and tests the HTTP
   client's timeout, which `curia`'s own tests already hold.
8. **The failed-post pty fact serves another identity's key set from `BrowseForumFixture` middleware. Accept.** There is no
   honest Forum path to a failed post; the screen's mapping from `Passage.Outcome` is held pure in B1.
9. **`Output.Draw` after `Leave` is not unit-tested. Accept as undischarged, and say so.** The IL fence holds *who* may draw and
   leave, not what happens after; the throw has no probe at all (implemented, not discharged). B6's register records it. The
   cheap discharge, if wanted later, is an `internal` reset beside a serialized test collection; it is not required here.
10. **The pty ready marker is the status row's `q quits`. Accept, on 14.2.**
11. **The disputed fact reuses `CompromiseDeclarationTests`' composition and `OperatorCommands`. Accept.**
12. **The runtime writes `ESC[?1h ESC=` before the first byte. Accept.** The vocabulary tokenizer reads `Vt.KeypadOn`; unchanged.
13. **Exit rank `6, 8, 4, 5, other, 9, 0`. Change to `6, 8, 9, 4, 5, other, 0`.** Against the other readers it is consistent
    where they speak: `ExitCode.ForOutcomes` ranks 6, 8, 9, 0 and says 9 is "below 6 and 8". They never rank a refusal
    against 9, because on a line form a refused load displays nothing. A screen can: a person sees a disputed post, presses
    `r`, and the reload is refused 404 or 403. R10.74's own reason is that the exit code "must carry what the person saw", and
    a refused load changed nothing the person saw; ranking it above 9 makes the one word an agent reads, "do not cite that",
    disappear behind a word about a request that failed. A check that failed (6) or did not run (8, which also carries a
    transport refusal) still outranks a mark, as on the line form. Edits: `OutcomeLedger.Rank`; the help's EXIT CODES line
    (`6 ..., 8 ..., 9 disputed, then 4 or 5 a load was refused`); §8.1's rank sentence; R10.74 (errata G22, amended). Fact:
    `…_ALaterRefusalNeverHidesADisputedOutcome` (disputed then 404 is 9, disputed then 403 is 9, failed then 404 is 6, a
    refusal alone is its own code, a transport timeout after disputed is 8); FB35.
14. **`InternalsVisibleTo Curia.Api.Tests`, `HttpFor` internal, `HelpTextTests` exemptions for `--agent` and `--marking`. Accept.**
    The exemptions are the two options §6 says the screen declines; each has its reason in the test.
15. **Disputed and failed pty cases at 120 by 50. Superseded by 14.1:** they run at 80 by 30, and one verified case runs at 80
    by 24.

### 14.7 Consequences for the documents

- §4.1 (H3, H4, the compact form), §4.3 (restated in 14.5 item 3), §5.4 (14.1, 14.5 items 4, 5, 7), §5.5 (status row, 14.2;
  row bytes, 14.6 item 2), §6 (`--plain` with `--screen`), §8.1 (rank, 14.6 item 13) and §11 (the new cases) are read as
  amended here.
- Errata G22: R10.74 gains "a key set that could not be read shown as not read and never as stating none", "A header MAY take a
  compact form ... every form SHALL state all of this" and "a load refused after the screen was up ranking below a disputed
  outcome"; its index row and its falsification bullet follow. R10.72's "eight" is unchanged; R10.75's mark cap is still
  to be amended with the measured number in B6, as the plan has it.
- The help (`BrowseState.HelpLines`) gains: the compact header exists below a height and what it leaves out; the rank; and the
  residuals of 14.4 item 4.
- The `curia` skill and `CLAUDE.md` are the owner's files; B6 records that they gain `browse` and exit 9 for it.
