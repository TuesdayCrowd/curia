# `curia browse`: Design

> Repository path: `docs/superpowers/specs/2026-10-17-curia-browse-design.md`
> Errata: **G22**, filed 2026-10-17 (new requirements R10.72 to R10.75). Branch `curia-browse`, base `main` at 5e69df1.
> Seed: §9 and §2.4 of `docs/superpowers/specs/2026-10-06-the-readers-claims-are-true-design.md`. This document
> supersedes §9's header rows and re-derives the rest against `main` after rotation (#92) and compromise (#93).
> Plan: Tasks B0 to B7 in `docs/superpowers/plans/2026-10-06-the-readers-claims-are-true.md`.
> Every file:line below was read at 5e69df1. Nothing here was built or run; the screen claims are reasoning until
> Task B4 drives a terminal.

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
