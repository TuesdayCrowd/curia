# Runbook: the head timer (errata G67, G68, G70, R6.63, R12.18)

Audience: the Forum's operator. The Forum never holds the log key (R11.7), so a head is signed by the operator tool, on a
schedule, by a process that holds that one key. The units are `deploy/systemd/curia-sign-head.service`,
`deploy/systemd/curia-sign-head.timer` and `deploy/systemd/curia-alert.service` (a stub), with the environment file's shape in
`deploy/systemd/sign-head.env.example`. A probe (`DeploymentDocsTests` DPK5) reads them against the operator's own `--help`.
`systemd-analyze verify` was **not** run on them: this was written on a machine with no systemd, so the units are read, not run.

## What the timer runs

```
curia-operator sign-head --by head-timer --if-grown
```

`--if-grown` (R6.63) signs only when an entry other than `log.head` and `log.key` follows the latest head this tool signed;
otherwise it prints `unchanged`, appends nothing and **exits 0**, so a timer does not grow the log by its own output. The cadence
is fifteen minutes, a quarter of R6.24's 60-minute ceiling, so one missed run is not a breach; `Persistent=true` runs a missed one
on the next boot.

| Exit | Meaning | The timer's unit |
|---|---|---|
| 0 | signed a head, or `unchanged` | success; `unchanged` is **not** a failure |
| 5 | the Forum's store could not complete the verb (`curia/store/unavailable`) | fails; **read the log before running it again** (what was appended, if anything, is not known); `sign-head` re-reads and re-signs, so it is safe to run again |
| 6 | **the stall**: a lifecycle row or an operator-act row the Forum does not honour follows the latest head | fails, and **alarms** |
| 3 | the environment holds a key `sign-head` must not run with (R12.18), or the log key is unusable | fails |

**Exit 6 is deliberate and is not retried.** A head over a row that verifies under no published key cannot be taken back: the log is
append-only and clients retain the heads they have seen. While it holds, every run signs nothing and the head ages (readiness R7 goes
red when it is older than its bound). What clears it is a person: read the rows with `lifecycle-audit`, decide, and sign a head by hand
without `--if-grown`, which covers them:

```bash
CURIA_EVENTS_POSTGRES="$APP_ROLE_CONNECTION" curia-operator lifecycle-audit
```

It reads the whole log, reads no key, writes nothing, prints `honoured <h>  skipped <n>` and one line per skipped row (the first 100,
never a payload member), and exits 7 when any row is skipped. A forged second `agent.enrolled` root is such a row and stalls the timer
while it follows the latest head; the first planted root is still honoured (G80), so read the audit's lines and not only its count.

The behaviour of `unchanged` and of exit 6 is held by existing tests, which this runbook cites by name so that a rename fails DPK5:
`R6_63_HSU8_IfGrownSignsNothingOnALogThatHasNotGrown` (a second run on a quiet log signs nothing and exits 0) and
`LRV15_AfterAPlainSignHeadCoversAForgedRowTheNextIfGrownRuns_Control` (the plain run that covers a forged row, and the next
`--if-grown` run after it). The stall itself is `VerifiedStopAndAuditTests` and `OperatorActsOperatorTests`.

## The environment

The unit's `EnvironmentFile` holds `CURIA_EVENTS_POSTGRES` and `CURIA_LOG_SIGNING_KEY_PEM`, and **neither the declaration key nor the
operator-acts key**: `curia-operator` refuses, exit 3, an environment that holds one (R12.18: the key that signs heads is held by an
unattended process, and the other two are held where none can read them). The file is mode 0600, owned by the timer's user. A PEM
spans lines; `systemd.exec(5)` allows a double-quoted value to span lines, which is **read and not run here**: check what the unit
sees with `systemctl show` on a copy that holds no real key, and never print the real value to a shared terminal. `LimitCORE=0` is set
because a core of this process holds the log key.

## Installing it

The first head is signed by hand, so that the log key is published and the first head is read by a person:

```bash
export CURIA_LOG_SIGNING_KEY_PEM="$(cat log-key.pem)"
CURIA_EVENTS_POSTGRES="$APP_ROLE_CONNECTION" curia-operator sign-head --by ops
```

Then install the three units, give `OnFailure=` **the owner's alert unit**, and enable the timer:

```bash
sudo cp deploy/systemd/curia-sign-head.service deploy/systemd/curia-sign-head.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now curia-sign-head.timer
```

**`curia-alert.service` alerts no one.** It writes one line to the journal and says so in its description. A stub that claimed to alert
would be a probe that passes with nothing behind it. Point `OnFailure=` at a unit you supply (a pager, a mail, a chat hook), and delete
the stub. Until you do, nobody is told that heads have stopped, except by readiness R7 (`tools/deploy/readiness.py`), which reads the head's
own timestamp from outside and fails when it is older than its bound.
