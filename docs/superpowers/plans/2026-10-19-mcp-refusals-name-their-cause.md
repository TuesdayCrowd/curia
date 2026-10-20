# MCP Refusals Name Their Cause: Implementation Plan

> Repository path: `docs/superpowers/plans/2026-10-19-mcp-refusals-name-their-cause.md`

**Goal:** `curia-mcp` words a write refusal by the Forum's reason, as `curia` does. Only `table-10/denied` is worded as a tier refusal; a quarantine, an ownership, a thread-ownership and a delegated-grant refusal are not (closes **D51**).

**Scope:** `src/Curia.Client/ForumResult.cs`, `src/Curia.Mcp/WriteTools.cs`, `src/Curia.Mcp/ToolText.cs`, tests, docs. No requirement, no errata entry, no wire change.

## Rulings (curia-architect, 2026-10-19)

1. The wording is chosen by the Forum's reason. `table-10/denied` keeps today's text and the composed `TierSpan`. Every other authorization reason, and an unknown one, is `"REFUSED: " + refusal.Summary`. `Summary` already quotes the title and detail (G17) and carries the client's per-cause advice, so it is not wrapped in a `FrameBuilder` again. The budget arms are unchanged.
2. Reuse through one public member: `Refusal.IsTierDenial` (Kind is Authorization and the reason is `table-10/denied`). `AuthorizationAdvice` stays private.
3. No new requirement and no errata entry.
4. `AnswerTemplate`'s sentence no longer says every refusal is a tier refusal: "If the Forum refuses, the refusal names its cause; a tier refusal says what reaches that tier, and retrying will not change a refusal of either kind."
5. D50's remainder is out of scope. The `curia/canon/*` 401s are the recommended next small stage; the author mismatch stays 401 (an identity claim the credential does not support; pinned by `An_impersonated_author_is_rejected`).

## Tasks

0. Baselines (Release): Client 695, Mcp 165, Api 600 (main's last recorded figure; not re-run).
1. Tests first, each seen red for its stated reason, then the implementation:
   - `RefusalClassificationTests.D51_OnlyATierDenialIsATierDenial` (Client; red: compile error, no `IsTierDenial`).
   - `WriteToolTests.D51_OnlyATierDenialIsWordedAsATierRefusal`, a Theory over `AccessPolicy.DenialReasons` (red on the four non-tier authorization reasons; the tier row and the two budget rows green).
   - `WriteToolTests.D51_AQuarantinedCredentialIsToldItIsQuarantined`, through `StubLog.DeniesSubmissionsForReason` and the client's real classification (red: "Requires trust tier" present).
   - `ToolDescriptionTests.D51_TheAnswerDescriptionDoesNotCallEveryRefusalATierRefusal` (red: the old sentence).
   - `McpWriteEndToEndTests.D51_AQuarantinedAgentThroughMcpIsToldItIsQuarantined`, against the real Forum with the quarantining PDP of `KeyRotationEndpointTests`.
2. Implementation: `IsTierDenial`; `WriteRefused` internal, guarded tier arm and a following authorization arm; the description sentence.
3. Falsification, restored by plain copy and `touch`, rebuilt `--no-incremental`, unpatched run green at the end.
4. Gates and docs (the plan's current-work block, D51 closed and D50's items ruled, README's MCP section, CLAUDE.md's status paragraph).

## Falsification rows

| Row | Patch | Red |
|---|---|---|
| FR1 | `IsTierDenial => Kind == RefusalKind.Authorization` | the Client Theory's non-tier rows, the Mcp Theory's four non-tier rows, the quarantine fact, the Api quarantine fact |
| FR2 | `IsTierDenial` false for the tier denial | the Client tier row, the Mcp tier row, `R11_26_AT0AnswerIsRefusedWithWhatReachesT1` |
| FR3 | drop `"REFUSED: " +` | the prefix assertion in the Mcp Theory's four non-tier rows, the Api quarantine fact |
| FR4 | give `table-11/quarantined-read-only` the tier paragraph | the quarantine fact and its Theory row, the Api fact |
| FR5 | drop the own-thread-only advice line | the own-thread-only Theory row (the fallback assertion) |
| FR6 | restore the old `AnswerTemplate` sentence | the description fact |

## As run

- **FR2** as written (`IsTierDenial => false`) does not compile: CA1822 asks for the member to be static. It was run as an always-false expression for an authorization refusal instead.
- **FR4** stayed green in `Curia.Mcp.Tests` on its first run, because the Theory reads its advice from `Summary` and so cannot see that advice change. The quarantine fact now holds `DoesNotContain("trust tier")`, and the Theory's non-tier path holds `DoesNotContain("Your trust tier does not permit")`. Rerun, FR4 is red on both. The register's D51 entry has the full record.
