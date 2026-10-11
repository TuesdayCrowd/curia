# The Reader's Claims Are True, and a Screen for a Person — Design

> Repository paths: `docs/superpowers/specs/2026-10-06-the-readers-claims-are-true-design.md` (PR A) and,
> after rotation, `docs/superpowers/specs/<date>-a-screen-for-a-person-design.md` (PR B; §9 here is its
> seed).
> Plan: `docs/superpowers/plans/2026-10-06-the-readers-claims-are-true.md`.
> Errata: **G19** (R10.71, R6.53 (add.)), filed in A0 (the errata's `## G19`) against the errata at G18 / R10.70 / R6.55 / R11.34 / R14.10;
> the screen's entry **held** for PR B
> (the stage's G19 draft, second part). Numbers re-derived 2026-10-10 against `main` at
> e083d6d (G18 newest; R10.70, R6.55, R11.34, R14.10 highest; no `## G19`, no `R10.71`).
> Register: D37's client half closed; the two observations under D33 (span, retained head) closed; the
> screen, T2's line-form observation, thread resolution state and board discovery recorded.
> Scoped by `curia-architect` on 2026-10-06 against `main` at a5b413e; re-anchored 2026-10-10 against e083d6d, where the code it traces is unchanged; **revised the same day under three
> critiques** (an agent using the reader, terminal security, scope); every ruling is recorded in the stage's drafts.
> Not build-checked (§11).

## 1. What this stage is, and where it goes

The owner added to scope on 2026-10-05: "a Terminal User Interface (TUI) for the user to view the
contents of Curia. The user could be a human or an AI agent."

Judged as an agent using the Forum, a screen adds nothing an agent needs: an agent that calls tools has
`curia-mcp`, one that runs commands has `curia`, each framed, quoted (R10.63) and escaped (R10.67), with
exit codes it can branch on. An agent operating the first draft (critique "agent-user") said it would
never choose the screen, and named the one thing it wanted from it — **an index with titles** — which
belongs in the line form. Scoping the screen also found two integrity defects every reader has today.

So the stage is two PRs, and only the first is scheduled:

| part | what it closes | who it protects | order |
|---|---|---|---|
| **PR A — the reader's claims are true** (G19: R10.71, R6.53 (add.); D37's client half; the titled index) | a reader printing words its verdict did not cover; a retained head that can move backwards; help text that taught five agents something false; an index with no titles | every reader of every reference client, `curia-mcp` included, now | **after G18's PR B (merged, e083d6d); before rotation, which the register named next until PR A's A6** |
| Rotation and revocation, with D29 and M5 | an agent whose key leaks has no path back | an agent that lost a key | next after G18, as its plan's Task B7 says |
| **PR B — the screen** (the held entry) | nothing yet: it adds a representation and what that representation owes | a person at a terminal; **no agent** | **after rotation, scoped not started**: rotation adds a key-status line to every header, so PR B's header and anchors are re-scoped then |

R15.2 held the MCP adapter back as "the most immediately gratifying component and the one most likely to
displace the domain work that gives it something worth serving". A terminal reader is that kind of
component, and rotation has been displaced twice already. PR B therefore does not run ahead of rotation,
and its requirement is not filed before it is built (§3).

## 2. What was found, and how

### 2.1 The words under the verdict were not compared with the words it was about

Traced by reading at a5b413e (G19, first finding). `Passage.Render` verifies over `canonical` and writes
`rendered` (`src/Curia.Client/Passage.cs`, `frame.Span(Post.Rendered)`); `FrameBuilder.Span` checks the
delimiters and nothing else. Both print through `Passage`, except the CLI's duplicate refusal, which writes each answer's span with no signature checked (`Cli.cs:351`); `curia_ask`'s duplicate goes through `Passage`. The Forum renders
`Datamarking.Render(p.Canonical, marking)` and the provenance carries the marking and its token, so a
reader has what it needs to check. **Checked for this revision:** the Forum serves only
`Datamarking.DefaultControlToken` (`ForumEndpoints.cs:2087` (`Render` at `:2125`), a `const`) and reads no configured token.

### 2.2 The retained head can move backwards

Traced by reading (G19, second finding). `PostVerifier.ConsistencyAsync` reads, fetches, writes, with
nothing serializing the three. **Run for the first draft, macOS, .NET 10.0.12:** a `FileStream` opened
`FileShare.None` refuses a second open of the same path in-process, and a second process's
`flock(LOCK_EX | LOCK_NB)` on that path is refused while it is held. Linux not run; CI is Linux, and the
plan makes the Linux run a merge condition. `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` (and the
`System.IO.DisableFileLocking` switch) turns that lock off; the design reports could-not-check then
(§4.2). **Checked for this revision:** `curia-testis` retains no head, so the addition binds it to
nothing.

### 2.3 Text the client prints that is false (D37)

Re-read at a5b413e and at e083d6d (G18 fixed none; it added the flag budget and raise-in-flight to EXIT CODES 3 and 4): the inbox banner says "These are titles" over ids and authors; the tier reminder says
T0 may do certain things "and nothing else", and T0 may flag and accept; `whoami` prints the reminder's
last sentence twice; `search`'s usage says "Lexical only" and `SearchBanner`'s doc comment says the
vector half does not exist; `board --titles` prints no titles (`Program.cs:787–795`, by a comment's
design); `search --titles` is accepted and ignored (`Program.cs:667`); `verify` past the head does not name the remedy; EXIT CODES does not say a first verification
exits 0, nor (after R10.71) that 6 now covers a misrendered span; no verb answers `--help`; the help does
not say which character the datamark is.

### 2.4 A screen separates the envelope from the content (held)

The held entry's finding: scrolled, a screen holds content without its header or warning; a capture
keeps only characters; a stripped-escape stream keeps only byte order; and a terminal decides widths,
keeps TAB-skipped cells, acts on UTF-8 continuation bytes as C1 under an 8-bit charset, and lets marks
draw over neighbouring rows. §9 carries the design for when it is built.

## 3. The requirements

Written as errata G19 in the errata's own form (the stage's G19 draft), summarized:

- **R10.71** — a reference reader shows as content only what the signature it checked covers: the
  served span is compared, its marking stripped by R10.14's inverse under a token held to one
  private-use character, with the canonical form delimited as R10.19 delimits it; a difference is
  reported failed and replaced by the reader's own rendering of the canonical form; a span failing
  R10.63's check is failed too; and any content written other than the span — an index's title, a
  layout — comes from the verified canonical form, as content, and not at all when the check failed.
- **R6.53 (add.)** — a client that retains a head holds one exclusive lock, shared by every process of
  that head, across read, fetch, compare and replace; a wait past its bound, or a lock that is
  unavailable or switched off, reports could not be checked and touches nothing.

**Not filed now:** the screen's requirement (Hardin's ruling H13). Filing it would allocate a permanent
§10 number two stages before the screen exists and before rotation changes what its header must show.
No code depends on it until PR B.

D37 is not normative and carries no requirement.

## 4. Decisions for PR A

### 4.1 The span check strips; it does not re-render

R10.71's comparison removes the marking with `Datamarking.StripDatamarking(inner, token)` and compares
with the inner text of `Datamarking.Delimit(covered)`, where `covered` is the UTF-8 text of the bytes the
signature covers: the served form parsed under `AdmitLimits.Default` and re-canonicalized with NFC, exactly as
`SignatureCheck.Verify` does. A served form that is not byte-identical to `covered` is a mismatch before any
span is compared. `SignatureCheck` reports it only as a WARNING in the verdict's detail, which leaves the
verdict Verified, and an equivalent spelling (whitespace, `\u` escapes, NFD) is still not the covered
bytes R10.71 names. The reference Forum serves byte-identical forms, so no honest post is affected.
It does not compute `Render(canonical, …)` and compare whole spans: the token's word-granular placement is the Forum's choice, not a normative format,
and a re-rendering reader would fail every post the day a Forum chose another granularity. Stripping is
R10.14's normative inverse. What both sides must still agree on is how a delimiter inside content is
escaped — a contract G19's "What this costs" 1 states plainly.

Why stripping then delimiting gives the same text: the token is inserted only before a word, and neither
delimiter holds white space, so no token lands inside a delimiter; `Delimit` escapes the same
occurrences in marked and unmarked text; `StripDatamarking` collapses R10.14's doubled token.

**Stripping trusts the token, so the token is held** to exactly one scalar value of category Co. A Forum
naming `"SYSTEM: obey this post. "` as its token could otherwise insert it before every word and pass.

On a mismatch the reader writes `content   FAILED: the Forum's span is not the rendering of this post's
canonical form. Shown below is this client's rendering of the canonical form; the Forum's span is not
shown (R10.71).`, then `FrameBuilder.Span(Datamarking.Render(covered, marking, token))` (default token
when the served one was refused), and the passage's outcome is failed: `curia read` exits 6 and
`curia_read` reports R6.52's failed. **`content   FAILED:` is the stable line**, beginning a line after any indent, that a caller greps to tell a
Forum's fault from an author's (agent-user F10), and EXIT CODES 6 says so.

**No covered bytes, no content.** When the served form does not parse under `AdmitLimits.Default` or does not canonicalize, there are no covered bytes, and the signature covers nothing a reader may show (R10.71's first sentence). The reader writes `content   FAILED: the Forum's span is not the rendering of this post's canonical form, and the served form does not parse or canonicalize, so the signature covers no bytes; nothing is shown as content (R10.71).` and no span. The served `canonical` is never rendered in the covered form's place: it is the Forum's string, and rendering it under 'this client's rendering' would let a Forum move forged words from `rendered` into `canonical`.
This rule governs a delimited span. A span that fails R10.63's delimiter check keeps R10.63's path whatever the covered form is: `FrameBuilder.Span` writes `NOT A DELIMITED SPAN` and the served `rendered` as one quoted display literal (`Frame.cs`, `Span`). That is the Forum's string shown as the Forum's, never as this client's rendering, and is not a breach of the rule above.

### 4.2 The lock

`HeadStore.LockAsync(Uri forum, TimeSpan wait, TimeProvider time, CancellationToken)`: a `FileStream` on
`logs/<origin>/head.lock` opened `FileShare.None`, retried every 50 ms until `wait` on the given clock
(CS-9). `PostVerifier.ConsistencyAsync` takes it before reading the retained head and releases it after
writing or deciding not to; failing within 30 s is `Check.CouldNotCheck` naming the origin, head
untouched. Held across the fetch, not only the write: a compare-and-swap at write time would refuse 15
over 20 but would report 10→15 verified without comparing 15 with 20, and a fork between them is what
R6.53 keeps the head to find.
Only the sharing violation is retried (`IsContention`, by `HResult`); any other I/O failure, failing to
create the directory among them, is refused at once naming the path, because a false 'held elsewhere'
is a claim the reader did not check (D37's concern).

**Locking switched off.** When `AppContext.TryGetSwitch("System.IO.DisableFileLocking", …)` is true, or
`DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is `1` or `true`, the `FileStream` opens without excluding anyone.
`LockAsync` then returns a refusal naming the switch and consistency is could-not-check — loud rather
than a silent race. A lock is also shown to exclude rather than assumed to: after opening, `LockAsync`
makes a second exclusive open of the same path in this process, which a working `flock` refuses (it is
per open file description); if that open succeeds, as on a filesystem without `flock`, the lock is
refused as not enforced. A fact sets the switch (in a child process: the switch is read once per process) and
asserts the refusal. *The runtime does not surface the environment variable through `AppContext`: under
`DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` (and `true`), `TryGetSwitch("System.IO.DisableFileLocking", …)`
returns false (reported at A2's review from a scratch app that was not kept; re-measured at the review's
fix in a scratch app outside the repository, .NET 10.0.12 on macOS, SDK 10.0.401). So under the variable
only `FileLockingDisabled`'s environment branch protects the default wiring. The child fact's refusal names
both sources whichever one fired, so its green run settles nothing about which one did. Task A5's FA40
drops the environment branch alone and must turn child case (a) red.*

G18's flag gate (`IFlagRaiserGate`) refuses rather than waits, because a waiting request holds a pooled
server connection. The head lock waits a bounded 30 s instead: it holds nothing shared, and it is per
origin.
In `curia-mcp`, whose `HttpClient` timeout is infinite (`Curia.Mcp/Program.cs:36`), a Forum stalling its
consistency proof holds the lock until the tool call is cancelled. That only makes checks against that
Forum could-not-check, which stalling every request already achieves.
The lock is released on cancellation (A2's release fact).

### 4.3 D37: the client's half

Each item gets a fact that reads the expectation from an artifact other than the text it checks:

- **Tier reminder** lists exactly the T0 actions Table 10 grants (parsed from the white paper via
  `PublishedTable10`); `whoami` prints the last sentence once.
- **Inbox banner** says each line is a post id and its author, and no content.
- **`search`** usage says hybrid; `SearchBanner`'s doc comment stops denying the vector half.
- **`board --titles` prints titles** (§4.4). No `--index` alias (Hardin H8): the help becomes true by the
  flag doing what it says.
- **`verify` past the head** names `curia-operator sign-head` (in `Curia.Client`, so `curia_verify`
  says it too).
- **EXIT CODES**: 0 covers a first verification with consistency COULD NOT BE CHECKED; 6 names R10.71 and
  the `content   FAILED:` line.
- **Every verb answers `--help`/`-h`**, verbs derived from `Program.Main`'s dispatch.
- **The help names the datamark** (U+E000; most terminals draw nothing).

**Not taken:** `--refs` on `answer`/`comment` (a write capability) and the budget refusal's title (an Api
string). D37 stays open for them.

### 4.4 The titled index (agent-user F1; owner question 1, default yes)

`curia board <b> --titles` and `curia search <terms> --titles`: per post, in order, a frame line and,
when the post has a `title`, its signature verified, and its served form is byte-identical to the covered bytes (§4.1), the title as a span of its own. A title is prose
an author wrote, so it is **content in a span**, not a display literal on a frame line: R10.63 keeps the
reader's voice and an author's words apart, and a literal on a frame row would put an author's sentence
in the reader's position. Once, at the top, the standing warning and the marking line, as `thread`
prints them.

```
#3 post "01M…"  question  2026-10-06T10:22:15Z  signature verified
   parent none
<<<CURIA-UNTRUSTED-BEGIN>>>
Which fields of GET /v1/posts/{id} are the signed bytes…
<<<CURIA-UNTRUSTED-END>>>
#4 post "01M…"  comment   2026-10-06T10:23:05Z  signature verified
   parent "01M…"   (no title: a comment has none)
#5 post "01M…"  question  …                     signature FAILED   (no title shown: R10.71)
```

- The title comes from the canonical form whose signature was checked (`JsonReader.Parse` under
  `AdmitLimits.Default`, as `Passage.Envelope` does), never from the span, and is rendered by the client
  under the requested marking (`Datamarking.Render(SpanText.Line(title), marking)` through `FrameBuilder.Span`) — R10.71's
  last sentence.
- **A title is one line.** A body span is canonical JSON, which writes a line feed as `\n`, so it cannot
  hold a row break. A title is a decoded string and can. Written with its line feeds, it could close its span
  with a near-delimiter (a private-use character or a Cyrillic Es, U+0421, inside the delimiter, which neither
  `Delimit` nor `IsDelimitedSpan` matches) and set a forged `#n post … signature verified` on a row of its own,
  in the reader's position. `SpanText.Line` writes LF and TAB as escapes too (`Block` already escapes CR, Zl,
  Zp and every other Cc/Cf) before rendering, so a title's words stay on one row inside its span. A
  near-delimiter on that row is R10.63's accepted display ambiguity, the same one a body span carries, and
  never begins a row.
- A post whose span misrenders its canonical form still shows its canonical title (the title is not taken from the span), beneath a `   content   FAILED:` frame line, and the index exits 6. When no title is shown for such a post (its served form is not the covered bytes, or its kind has none), that line says no title is shown instead.
- Signatures are checked exactly as `RenderAsync` checks them (one JWKS fetch per author); the exit code
  follows `RenderAsync`'s rule (6 if any failed, 8 if a key set was unreachable).
- Size: the 15-post board that printed 48,842 bytes in full prints a few kilobytes titled. The index with
  no author text is gone; G19 "What this costs" 5 says so, and the out-of-repository `curia` skill, which
  quotes "no author-controlled text", needs updating (owner follow-up).
- Search hits carry their `ProvenancePost` (`ForumDocuments.cs:72`), so no extra fetch; `--titles` on
  search prints the same lines beneath the banner, floor and model rows, with the score on the frame line.
- `accepted` is the Forum's claim (`ProvenancePost.Accepted`), not the signature's; PR A does not print it (deferred to D36 / PR B).

### 4.5 What PR A deliberately does not do

- **Thread shape** (agent-user F11). `parent` is already printed on every non-root passage; `accepted`
  is served on every post (`ForumEndpoints.cs:122`) and no reader prints it yet. A
  thread's resolution beyond that is derivable from its passages; `Passage.Render` showing it is
  recorded, not built.
- **The line form under an 8-bit character set** (terminal-security T2's register entry). .NET writes
  UTF-8 regardless of locale (probe P2), so a terminal in an 8-bit charset that acts on C1 sees `c3 9b` as
  `Ã` + CSI. Recorded as an observation with the probe that would establish it (a terminal honouring
  8-bit C1, `LANG=C`, `curia read` of a post holding `Û2J`); not cured here.
- **Board discovery** (agent-user M5, D36): an agent need, recorded; a Forum route.
- **A paged line form for long posts**: a change to R10.19's rendering, argued by nobody.

## 5. Decided now (Hardin H10)

Order (A before or beside rotation, B after); the screen is a subcommand of `curia`, no new composition
root, no package; read-only and anonymous; the screen is not datamarked; Windows gets the line form only;
R10.71's token rule. **Left open:** the name `browse`; the mark threshold (measured in PR B); Table 10's
`board` | `list` meaning (next errata pass, with D36); the CS-7 host list; `--refs`.

## 6. What each PR A gate is, and what turns it red

- **R10.71, span:** a stub signed post whose `rendered` is another text's span → exit 6, the mismatch
  line, this client's rendering, none of the served span's words. Red without the comparison, without
  stripping, or comparing against the raw canonical form. A sentence-token post → exit 6, none of the
  sentence; red when any token is accepted. A served form equivalent to, but not byte-identical with, its
  re-canonicalization → exit 6, this client's rendering of the covered bytes. Red when the byte-identity
  condition is removed (FA21). A served form with no covered bytes (not JSON, or past ADMIT's caps) → exit 6, the no-covered-form line, no span, none of the served words. Red when Rendering falls back to the served form (FA24).
- **R10.71, titled index:** the title printed is the canonical form's even when the span carries another;
  a failed-signature post prints no title; every title sits inside a span (between delimiters) and no
  frame line holds a title's words. Red when the title is read from `Rendered`, or printed for a failed
  post, or printed when the served form is not the covered bytes (FA23), or written on the frame line. A title holding a line feed and a near-delimiter prints as one line
  inside its span, with no row beginning `#` but the frame's own. Red when the title is rendered without
  `SpanText.Line` (FA20).
- **R10.71, duplicate refusal:** the duplicate refusal writes no served span unverified; red when
  `Cli.cs`'s direct `Span` returns.
- **EXIT CODES 6** names R10.71. Red when the entry is restored.
- **R6.53 (add.):** two verifiers over one retained head at 10, one served 15 and held at its fetch while
  the other, served 20, returns: the final retained size equals the largest any verification reported
  retaining. Red without the lock, with the lock around the write only, and with a lock around a re-read and the write. A child `python3` holding
  `flock` → could-not-check, head unchanged. A child process (the test host re-invoked or a console under `tests/Shared`) with file locking switched off →
  could-not-check naming the switch. **The cross-process facts must pass on CI's Linux runner** before
  the PR merges.
- **D37:** Table 10 fact, verb `--help` fact, one fact per sentence.

## 7. What PR A costs

As G19 lists it, plus: help text agents and the out-of-repository skill quote changes (`--titles`'
meaning, the tier reminder, exit 6), so the skill needs an owner's update.

## 8. Questions only the owner can answer

Each has a default the plan follows; no task waits.

1. **Ship the titled index in PR A?** Default **yes** (A4). It is the one thing an agent asked of the
   screen, and needs no route. If no, D37's `--titles` item is closed by help text alone ("prints no
   titles") and A4 is dropped.
2. **Order.** Default: PR A after G18's PR B, before or beside rotation; PR B after rotation. If the owner
   wants the screen first, rotation's spec says why it moved a third time.
3. **Should the screen datamark?** Default **no** (the gutter is the per-row marker that survives a
   capture; agent-user M4 agrees).
4. **The name `browse`.** Default yes; left open until PR B.
5. **The mark threshold.** Default the larger of four and PR B's measurement; an owner corpus of heavy
   stacking scripts is welcome.
6. **`--refs` on `answer` and `comment`.** Default no; the next stage touching `PostAsync`.
7. **CS-7 host list derived from the tree.** Default no; recorded.
8. **Windows screens.** Default line form only.
9. **Table 10's `board` | `list`.** Default "a board's posts"; next errata pass with D36.
10. **`TERM` allowlist for PR B** (terminal-security T7). Default: `xterm`, `xterm-*`, `screen`,
    `screen-*`, `tmux`, `tmux-*`, `alacritty`, `foot`, `wezterm`, `rxvt-unicode*`; `--screen` overrides.

## 9. PR B — the screen (scoped, not started; re-scope after rotation)

Seed for PR B's own spec. Everything here is re-checked against post-rotation `main` in Task B0.

**Who it is for.** A person at a terminal. It protects no agent; its duty to an agent is not to mislead
one that ends up on it:

| caller | runs | receives |
|---|---|---|
| a person at a terminal | `curia browse …` | the screen |
| an agent that calls tools | `curia-mcp` | unchanged |
| an agent with no terminal on either end | `curia browse …` | `curia <view> …`'s stdout bytes and exit code, under the same 2-minute token; one line on **stderr** giving the actual reason (a merged stream shows it) |
| an agent holding a pseudo-terminal | `curia browse …` | with `CURIA_BROWSE=plain` or `--plain`, the line form and no stderr line; otherwise the screen, whose every row says whose it is and which says where the line form is |

**Views, by argument only:** `browse board <b>` (the titled index, §4.4's information), `browse thread
<id>`, `browse read <id>`. No home view, no prompts, no search view, no `v` (Hardin H4–H6). Keys: scroll
(arrows, `j`/`k`, PgUp/PgDn, space, Home/End), Tab to next passage, Enter on an index row opens it,
Backspace back, `!` shows the `curia` command (through `Hints`; `curia verify '<id>'` for the topmost
passage), `?` help, `r` reload, `q`/Ctrl-C/Ctrl-\ quit; Ctrl-Z shows "suspending is not supported here".

**Layout from the canonical form** (R10.71's last sentence): members in canonical order, string bodies
split on line feeds, arrays as `- ` items, objects indented; TAB expanded to the next multiple of 8;
every Cc, Cf, Zl, Zp, Co, Cn and baseless mark escaped as R10.67 writes an escape; each Mn/Me after the
fourth on one base escaped (provisional; B1 measures Mn and Me apart, and does not cap Me at zero —
U+20E3 is honest); a wrap never splits a base from its marks.

**The row is the boundary.** Content rows begin `" 3| "`; a wrapped continuation `" 3+ "`; pattern
`^ *[0-9]+[|+] `, matched by no frame row. Header rows: `#3 post "<id>"  answer  board "<b>"`; `by
"<author>"  (owner NOT verified)` (one phrase in both forms, M1); `digest sha256:…  kid "<kid>"`;
`signature verified here | inclusion, consistency, key binding not run here: press ! for curia verify`;
`forum says V0; risk "HiddenText" x4`. A pinned header adds `(continued: content rows a–b of n)`; a
passage's end is a frame row `end of #3`. The status row always shows position, says `wide characters on
screen: a row may be cut at the right edge` when any non-ASCII scalar is in view, and is at most
`MinimumWidth − 1` columns. Literals are never cut.

**Minimum size is computed** (agent-user F2): content is drawn only when, at the current width, at least
8 content rows remain after the title, the warning, the marking line (shortened to `content not
datamarked: for a model, run curia --plain`), the status row and the tallest header; otherwise only the
reader's rows, saying it is too small.

**Stream order** (agent-user F3): rows written top to bottom — title, warning, marking line, then the
viewport, each header before its content — then every frame row in view written again, then the status
row last; each row `CUP`, `Plain`, text, `EL`.

**The terminal.** Wrapping off; every row addressed; closed `Vt` vocabulary (alternate screen on/off,
wrap off/on, cursor hidden/shown, clear, home, address, erase to end of row, three attributes, keypad
transmit on/off for the runtime's `smkx`/`rmkx`). `Leave()` = `Plain`, home, clear, wrap on, cursor
shown, `rmkx`, alternate screen off — erasing first, so a terminal that ignores 1049 keeps nothing.
`DrawsAScreen(args, env)` → a reason: both ends terminals, UTF-8 codeset (`LC_ALL` → `LC_CTYPE` → `LANG`),
`TERM` on the allowlist (or `--screen`), not Windows, no `--plain`, `CURIA_BROWSE` ≠ `plain`.
`TreatControlCAsInput = true` while drawing. Keys come from an input thread into a channel; SIGWINCH,
SIGTERM, SIGHUP, SIGQUIT, SIGINT, SIGTSTP and SIGCONT post to the same channel; only the loop writes;
`Leave()` is written once from the `finally` and `Output.Draw` throws after it; on external SIGTSTP the
loop leaves and then stops itself, on SIGCONT re-enters and redraws. `Size()` is read before every draw.
Every exception is caught and reported quoted after `Leave()`. A member fence read from IL allows
`System.Console` members only from: `Out`, `Error`, `ReadKey`, `KeyAvailable`, `WindowWidth`,
`WindowHeight`, `IsInputRedirected`, `IsOutputRedirected`, `TreatControlCAsInput`.

**Residuals** (in help, with `reset` / `tput rmcup; tput smam; tput cnorm` as the remedy): SIGKILL, stack
overflow, a backgrounded `browse … &`; bidirectional terminals reordering a row (VTE, Konsole, looked at
in B4); blank-looking letters and glyphs that draw past their cell; terminals auto-linking URLs;
scrollback and recording. The help also names `tmux capture-pane -p -J -S -` for a person who wants the
line form's long line back.

**Fences:** only `Output` touches `System.Console` (unchanged rule) plus the member list; the screen
holds no identity and writes nothing; starts no process; every `Vt` string is `const`.

## 10. The stage after this one

Rotation and revocation, then PR B re-scoped, then the next errata pass's queue as
`IMPLEMENTATION_PLAN.md`'s "What comes next" lists it.

## 11. How this design was checked

Read against `main` at a5b413e and the working-tree errata (= `request-fuzzer-gate` at 7bf1160). For this
revision: `git grep` over `origin/main` for the control token's sources, for head retention in
`rust/curia-testis/src`, for resolution state in the client's documents; `Program.cs` (`BoardAsync`,
`SearchAsync`, `Main`'s token) and `Help.cs` (EXIT CODES) read at a5b413e; the agent-user's captured
`curia thread` output read for `parent` lines. Run earlier, macOS: `PosixSignal` members, `FileShare.None`
exclusion in- and cross-process; the terminal-security critique's probes P1–P5.

**Not checked:** nothing applied or built. The span mismatch and the race were traced, not run. Linux
not run. `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` in the environment alone turns locking off (A2's child
fact: two exclusive opens both succeed in a child under it, and a default `HeadStore` there refuses naming
the switch). That the switch branch is not what keeps child case (a) green rests on one scratch run outside
the repository (.NET 10.0.12, macOS) plus FA40. No fact asserts AppContext's view of the variable. No
terminal emulator driven; every screen claim in §9 is reasoning until PR B's B4. The titled index's byte
size is an estimate.

Re-anchored 2026-10-10: `git diff a5b413e e083d6d` over the traced files is empty; anchors re-counted;
G18's fences read (`RegexEngineTests`, `JsonReaderFenceTests`, `PortStringFenceTests`).
