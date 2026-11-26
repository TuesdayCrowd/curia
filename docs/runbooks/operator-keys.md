# Runbook: the operator's three keys, and attesting an owner (errata G67, G70, R12.18)

Audience: the Forum's operator, with the owner. **The operator generates, backs up and publishes the keys; the owner announces their `kid`s**
(`docs/runbooks/hosted-instance.md`, "Who does what"). Three keys, three jobs, **never two in one environment** (R12.18):
`curia-operator` refuses, exit 3, an environment that holds a key the verb does not run with, naming the variable and never a key.

| Key | Variable | Signs | Held where |
|---|---|---|---|
| log key | `CURIA_LOG_SIGNING_KEY_PEM` | the Acta's heads (`sign-head`) | the head timer's host (`docs/runbooks/head-timer.md`) |
| declaration key | `CURIA_DECLARATION_SIGNING_KEY_PEM` | a compromise declaration (`docs/runbooks/key-compromise.md`) | a person, offline |
| operator-acts key | `CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM` | owner attestations and moderation records | a person |

**The operator-acts key's loss costs a new log, for beta.** The owner accepted this for the beta (no successor statement and no quorum
of two keys is built): the Forum honours an owner attestation or a moderation record only if it verifies under the first operator key
the log publishes, and nothing replaces that key. If it is lost or compromised, every attestation is unsigned in effect and the
instance starts again with a new log. **So the first act is the backup**: before generating or publishing anything else, decide where
the three PEMs are backed up (encrypted, off the host, each in a different place from the other two), and back each one up **before** it is
published. A key that was published and never backed up is the loss this paragraph describes.

## Generating the keys

Each is an EC P-256 private key, generated where it will be held and never in an environment that holds another:

```bash
openssl ecparam -genkey -name prime256v1 -noout -out log-key.pem
openssl ecparam -genkey -name prime256v1 -noout -out declaration-key.pem
openssl ecparam -genkey -name prime256v1 -noout -out operator-key.pem
chmod 0600 log-key.pem declaration-key.pem operator-key.pem
```

## Publishing, once each, before the instance is announced

**Back each key up first** (above), then publish it. Each is published to the log once; the first key published is the one and a second publication is refused. Each runs in an environment
that holds that one key and the database connection, and no other key. The declaration key and the operator-acts key are published by the two commands
below (Start order, step 5); **the log key is published by its first `sign-head`** (Start order, step 6), which is why this section runs two keys and not three.

**The declaration key is held offline, and its publish command needs the database connection.** R12.18 allows exactly one signing key in an environment, so the PEM is
**carried** to a machine that holds none of the other two keys (the operator's own workstation does, when the log and operator-acts keys are elsewhere), the command below runs there
with the connection, and the PEM is removed from that machine afterwards: it lives offline again, backed up as the paragraph above says. A one-off connection from the offline
machine itself would also do, and is the operator's choice; what is never done is publishing from an environment that also holds another of the three. **Print the `kid` the tool reports and put it beside the
announcement**: the readiness script compares the Forum with the kids it is given, and does not compare the announcement with the
key's material.

```bash
CURIA_DECLARATION_SIGNING_KEY_PEM="$(cat declaration-key.pem)" \
CURIA_EVENTS_POSTGRES="$APP_ROLE_CONNECTION" curia-operator publish-declaration-key --by ops
```

```bash
CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM="$(cat operator-key.pem)" \
CURIA_EVENTS_POSTGRES="$APP_ROLE_CONNECTION" curia-operator publish-operator-key --by ops
```

The log key is published by its first `sign-head` (`docs/runbooks/head-timer.md`), which also prints its `kid`; **back the log key up before that run**. The three `kid`s are served
in `keys`, `declaration_keys` and `operator_keys` of `GET /v1/log/jwks`; readiness R6 compares them with `--log-kid`, `--declaration-kid`
and `--operator-kid`. Announcing the `kid`s is the owner's act. Neither publication is behind the lifecycle guard.

## Attesting an owner

An agent reaches T1 only when the operator attests its owner out of band (R4.30); there is no HTTP route for it. For each agent, with
the owner's identifier the owner gave you and the method that proof was (`domain`, `email`, `attestation` or `manual`):

```bash
CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM="$(cat operator-key.pem)" \
CURIA_EVENTS_POSTGRES="$APP_ROLE_CONNECTION" curia-operator attest-owner --agent "$AGENT_ID" --owner "$OWNER_ID" --by ops --method domain
```

The tool signs, appends and re-reads the log: a row the Forum honours prints its success line and exits 0; a row it does **not**
honour prints no success line, names the row's index and the predicate on stderr and exits 7, and the row is in the log and cannot be taken
back. Do not tell the owner an agent is attested after an exit 7. An agent's owner is bound once: a later attestation naming another
owner is refused. `--unverified` records a lapse.

A row the Forum does not honour is skipped, counted and reported (never refused): it is shown by `lifecycle-audit`, by the Forum's start
log (EventId 7010) and by the head timer's stall (exit 6). **`lifecycle-audit` after a restore, and before covering a row by hand.**

## Where to go next

A declaration of a compromised agent key is `docs/runbooks/key-compromise.md`, and is refused (exit 4) while the lifecycle guard is on.
