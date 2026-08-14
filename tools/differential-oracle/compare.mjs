#!/usr/bin/env node
// compare.mjs — three-way differential comparison driver (Task 7, design spec §9).
//
// Generates an NDJSON corpus via generate.mjs, feeds the *identical* corpus to all
// three endpoints (the C# harness driving Curia.Canon, the Rust curia-differential
// binary driving curia-testis, and the from-scratch node oracle), joins the three
// output streams positionally against the input corpus (each endpoint's own
// contract is "one output line per input line, in order, never reordered, never
// skipped" — see the wire protocol doc in this directory), and reports every
// divergence found, grouped into classes with one minimized reproducer per class.
//
// Comparison rules (see the task brief this driver implements):
//   canonicalize      — all three byte-identical, or all three fail. Node is
//                        authoritative over the two implementations where they
//                        disagree with it.
//   canonicalize_nfc  — C# and Rust must be byte-identical (or both fail). Node is
//                        advisory only (its Unicode version may differ).
//   admit             — C# and Rust must agree on accept/reject AND on which slug.
//                        Node is not consulted (op:"admit" always returns
//                        oracle/unsupported from the node endpoint).
//   CRASH             — a CRASH slug from *any* endpoint is its own divergence
//                        class, regardless of what the other endpoints did.
//
// Usage:
//   node tools/differential-oracle/compare.mjs [--seed N] [--count N]
//     [--workdir DIR] [--report PATH] [--no-supplemental]
//
// All file I/O for the corpus and the three endpoints' raw output happens through
// real file descriptors (spawnSync with stdio: [fd, fd, fd]) so a multi-hundred-
// megabyte corpus (the admit boundary categories legitimately produce single lines
// approaching the 256 KiB string cap) is never held as one JS string. The
// comparison pass itself streams all four files in lockstep with four readline
// interfaces rather than loading anything into memory as a whole.

import { spawn, spawnSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { createReadStream } from 'node:fs';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '../..');

const PATHS = {
  generate: path.join(HERE, 'generate.mjs'),
  oracle: path.join(HERE, 'oracle.mjs'),
  csharpDll: path.join(REPO_ROOT, 'tools/Curia.Differential/bin/Release/net10.0/Curia.Differential.dll'),
  csharpProj: path.join(REPO_ROOT, 'tools/Curia.Differential/Curia.Differential.csproj'),
  rustRelease: path.join(REPO_ROOT, 'rust/curia-testis/target/release/curia-differential'),
  rustDebug: path.join(REPO_ROOT, 'rust/curia-testis/target/debug/curia-differential'),
};

// ---------------------------------------------------------------------------
// CLI args
// ---------------------------------------------------------------------------

function parseArgs(argv) {
  const args = {
    seed: '20260812',
    count: 7500,
    workdir: null,
    report: path.join(HERE, 'DIVERGENCES.md'),
    supplemental: true,
    minimizeBudget: 400,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--seed') args.seed = argv[++i];
    else if (a === '--count') args.count = Number(argv[++i]);
    else if (a === '--workdir') args.workdir = argv[++i];
    else if (a === '--report') args.report = path.resolve(argv[++i]);
    else if (a === '--no-supplemental') args.supplemental = false;
    else if (a === '--minimize-budget') args.minimizeBudget = Number(argv[++i]);
    else { process.stderr.write(`compare.mjs: unrecognized argument '${a}'\n`); process.exit(2); }
  }
  if (!args.workdir) {
    args.workdir = fs.mkdtempSync(path.join(os.tmpdir(), 'curia-differential-'));
  } else {
    fs.mkdirSync(args.workdir, { recursive: true });
  }
  return args;
}

// ---------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------

function ndjsonLine(obj) {
  return JSON.stringify(obj) + '\n';
}

function toB64(bytesOrString) {
  const buf = Buffer.isBuffer(bytesOrString) ? bytesOrString : Buffer.from(bytesOrString, 'utf8');
  return buf.toString('base64');
}

function fromB64(b64) {
  return Buffer.from(b64, 'base64');
}

function hexOf(buf) {
  return buf.toString('hex');
}

function isCrash(result) {
  return !!result && result.ok === false && typeof result.slug === 'string' && result.slug.includes('/CRASH:');
}

// ---------------------------------------------------------------------------
// Step 1: corpus generation
// ---------------------------------------------------------------------------

function runGenerator(seed, count, outFile) {
  const outFd = fs.openSync(outFile, 'w');
  const errFile = outFile + '.stderr.txt';
  const errFd = fs.openSync(errFile, 'w');
  const res = spawnSync(process.execPath, [PATHS.generate, '--seed', String(seed), '--count', String(count)], {
    stdio: ['ignore', outFd, errFd],
  });
  fs.closeSync(outFd);
  fs.closeSync(errFd);
  if (res.status !== 0) {
    throw new Error(`generate.mjs exited with status ${res.status}; see ${errFile}`);
  }
  const summary = fs.readFileSync(errFile, 'utf8');
  return { summary };
}

// Supplemental, hand-constructed cases covering conditions the property-driven
// generator's category set cannot reach by construction (documented per-case
// below), plus deterministic instances of a few conditions the generator *can*
// reach only probabilistically — so the report's headline findings never depend
// on a particular --seed getting lucky.
function buildSupplementalCases() {
  const cases = [];
  let n = 0;
  const add = (name, op, bytes) => {
    cases.push({ id: `supplemental.${String(n++).padStart(3, '0')}.${name}.${op}`, op, input_b64: toB64(bytes) });
  };

  // 1. ADMIT's overall submission-size cap (AdmitLimits.MaxBytes / ADMIT_MAX_SUBMISSION_BYTES
  //    = 1_048_576 bytes). No generator category ever approaches this size — the
  //    largest generated documents are the 256 KiB *string*-cap cases, which sit
  //    well under the 1 MiB whole-submission cap. Content is irrelevant to this
  //    check on both sides (both implementations check raw byte length before any
  //    parsing), so the minimal trigger is exactly cap+1 arbitrary bytes.
  add('size-exceeded', 'admit', Buffer.alloc(1_048_577, 0x61));

  // 2. Generic JSON syntax error with no more specific classification on either
  //    side — the smallest possible malformed document.
  add('malformed-generic', 'admit', Buffer.from('!', 'utf8'));

  // 3. A raw (unescaped) control byte inside a string that is NOT NUL. Found via
  //    source review (src/Curia.Canon/Json/JsonReader.cs vs
  //    rust/curia-testis/src/json.rs): curia-testis's parser has a dedicated
  //    ParseError::RawControlInString -> "curia/admit/raw-control-character"
  //    branch; .NET's Utf8JsonReader rejects the same byte as an ordinary
  //    JsonException, which JsonReader.Parse's catch-all folds into the generic
  //    "curia/admit/malformed-json" slug. No generator category produces this
  //    (malformed-nul only ever uses 0x00).
  add('raw-control-character', 'admit', Buffer.from('{"a":"x\x01y"}', 'binary'));

  // 4. ADMIT's generic JsonReader used by this harness's own "admit" op enforces
  //    no integer-only / safe-range rule on numbers at all (JsonReader.ReadNumber
  //    only checks double.IsFinite); curia-testis's admit() applies check_number
  //    to every number in the tree (R6.33/errata D5: "-(2^53-1) <= n <= 2^53-1",
  //    integers only). Confirmed directly (see task report) before adding this.
  add('non-integer-number', 'admit', Buffer.from('{"n":1.5}', 'utf8'));
  add('unsafe-integer', 'admit', Buffer.from('{"n":9007199254740992}', 'utf8')); // 2^53, already unsafe

  // 5a. Three more ADMIT-only rules that this harness's C# "canonicalize"/
  //    "canonicalize_nfc" ops inherit (because Program.cs's Canonicalize() always
  //    pre-parses with the same JsonReader.Parse ADMIT uses) but that curia-testis's
  //    canonicalize()/canonicalize_with_nfc() never apply at all (they call the
  //    bare RFC 8259 `json::parse`, never `json::admit`, per
  //    curia-differential.rs's dispatch — see the "systemic root cause" section of
  //    the report): depth (cap 32), member count (cap 1024), string length
  //    (cap 262144 bytes), and Unicode noncharacters. Each probed on both
  //    canonicalize and canonicalize_nfc, since the pre-parse is shared by both.
  {
    const depth33 = '{"a":'.repeat(33) + '0' + '}'.repeat(33);
    add('depth-exceeded', 'canonicalize', Buffer.from(depth33, 'utf8'));
    add('depth-exceeded', 'canonicalize_nfc', Buffer.from(depth33, 'utf8'));
  }
  {
    const entries = [];
    for (let i = 0; i <= 1024; i++) entries.push(`"k${i}":0`);
    const members1025 = '{' + entries.join(',') + '}';
    add('members-exceeded', 'canonicalize', Buffer.from(members1025, 'utf8'));
    add('members-exceeded', 'canonicalize_nfc', Buffer.from(members1025, 'utf8'));
  }
  {
    const string262145 = '{"s":"' + 'a'.repeat(262_145) + '"}';
    add('string-too-long', 'canonicalize', Buffer.from(string262145, 'utf8'));
    add('string-too-long', 'canonicalize_nfc', Buffer.from(string262145, 'utf8'));
  }
  {
    const noncharacter = '{"a":"' + String.fromCodePoint(0xFFFE) + '"}';
    add('noncharacter', 'canonicalize', Buffer.from(noncharacter, 'utf8'));
    add('noncharacter', 'canonicalize_nfc', Buffer.from(noncharacter, 'utf8'));
  }

  // 5. Pure "canonicalize" (no NFC): C#'s harness routes this op through the same
  //    JsonReader.Parse ADMIT uses (Program.cs's Canonicalize()), which rejects a
  //    byte-identical duplicate key; curia-testis's *pure* canonicalize() calls the
  //    bare RFC 8259 `json::parse`, which src/json.rs's own module doc comment
  //    states explicitly does not reject duplicates ("Task 4's concern, not this
  //    pure function's") — it renders both members, adjacent, undeduplicated.
  add('duplicate-key-pure-canonicalize', 'canonicalize', Buffer.from('{"a":1,"a":2}', 'utf8'));

  // 6. canonicalize_nfc and an NFC-manufactured duplicate key: two byte-distinct
  //    wire keys ("café" precomposed U+00E9 vs "café" decomposed e+U+0301) that
  //    collide only after NFC. curia-testis's canonicalize_with_nfc has a
  //    dedicated two-pass check for this (nfc.rs, "Fix rounds 1-3") and rejects
  //    with curia/canon/duplicate-normalized-key; C#'s CanonicalizeWithNfc
  //    (CanonicalJson.cs) has no such check at all — NormalizeToNfc simply maps
  //    each member's key through Normalize(FormC) with no uniqueness check, and
  //    Write()'s OrderBy is stable but does not deduplicate, so the emitted
  //    "canonical" bytes contain two members sharing one key: not valid I-JSON.
  add('nfc-collision-duplicate-key', 'canonicalize_nfc', Buffer.from('{"café":1,"café":2}', 'utf8'));

  return cases;
}

function writeSupplementalCorpus(cases, outFile) {
  const fd = fs.openSync(outFile, 'w');
  for (const c of cases) fs.writeSync(fd, ndjsonLine(c));
  fs.closeSync(fd);
}

function concatFiles(parts, outFile) {
  const outFd = fs.openSync(outFile, 'w');
  for (const p of parts) {
    const data = fs.readFileSync(p);
    fs.writeSync(outFd, data);
  }
  fs.closeSync(outFd);
}

// ---------------------------------------------------------------------------
// Step 2: batch-run each endpoint against the whole corpus file, via real fds
// (no JS-level buffering of the corpus or the output — exactly like shell
// redirection).
// ---------------------------------------------------------------------------

function resolveCsharpInvocation() {
  if (fs.existsSync(PATHS.csharpDll)) {
    return { cmd: 'dotnet', args: [PATHS.csharpDll] };
  }
  throw new Error(
    `C# endpoint not built: ${PATHS.csharpDll} does not exist.\n` +
    `Run: dotnet build ${PATHS.csharpProj} -c Release`);
}

function resolveRustInvocation() {
  if (fs.existsSync(PATHS.rustRelease)) return { cmd: PATHS.rustRelease, args: [] };
  if (fs.existsSync(PATHS.rustDebug)) return { cmd: PATHS.rustDebug, args: [] };
  throw new Error(
    `Rust endpoint not built. Run: cargo build --release --bin curia-differential ` +
    `(in rust/curia-testis)`);
}

function runEndpointBatch(label, cmd, args, inFile, outFile) {
  const inFd = fs.openSync(inFile, 'r');
  const outFd = fs.openSync(outFile, 'w');
  const errFile = outFile + '.stderr.txt';
  const errFd = fs.openSync(errFile, 'w');
  const start = Date.now();
  const res = spawnSync(cmd, args, { stdio: [inFd, outFd, errFd] });
  const elapsedMs = Date.now() - start;
  fs.closeSync(inFd);
  fs.closeSync(outFd);
  fs.closeSync(errFd);
  if (res.status !== 0) {
    const stderrTail = fs.readFileSync(errFile, 'utf8').slice(-4000);
    throw new Error(`${label} endpoint exited with status ${res.status} after ${elapsedMs}ms.\nstderr tail:\n${stderrTail}`);
  }
  return { elapsedMs };
}

// ---------------------------------------------------------------------------
// Classification — the single source of truth for "is this a divergence", used
// both by the streaming main pass and by the interactive minimizer, so a
// candidate during shrinking is judged by exactly the same rule that found it.
// ---------------------------------------------------------------------------

function classifyAdmit(csharp, rust, node) {
  const crashed = [];
  if (isCrash(csharp)) crashed.push(['csharp', csharp.slug]);
  if (isCrash(rust)) crashed.push(['rust', rust.slug]);
  if (node && isCrash(node)) crashed.push(['node', node.slug]);
  if (crashed.length) return { key: `crash::admit::${crashed.map(c => c[0]).sort().join('+')}`, detail: { crashed } };

  if (csharp.ok && rust.ok) return null;
  if (!csharp.ok && !rust.ok) {
    if (csharp.slug === rust.slug) return null;
    return {
      key: `admit/slug-mismatch::${csharp.slug}::${rust.slug}`,
      detail: { csharpSlug: csharp.slug, rustSlug: rust.slug },
    };
  }
  const acceptedBy = csharp.ok ? 'csharp' : 'rust';
  const rejectedBy = csharp.ok ? 'rust' : 'csharp';
  const rejectSlug = csharp.ok ? rust.slug : csharp.slug;
  return {
    key: `admit/accept-reject-mismatch::accepted-by-${acceptedBy}::rejected-by-${rejectedBy}::${rejectSlug}`,
    detail: { acceptedBy, rejectedBy, rejectSlug },
  };
}

function classifyCanonicalize(csharp, rust, node) {
  const crashed = [];
  if (isCrash(csharp)) crashed.push(['csharp', csharp.slug]);
  if (isCrash(rust)) crashed.push(['rust', rust.slug]);
  if (isCrash(node)) crashed.push(['node', node.slug]);
  if (crashed.length) return { key: `crash::canonicalize::${crashed.map(c => c[0]).sort().join('+')}`, detail: { crashed } };

  const allOk = csharp.ok && rust.ok && node.ok;
  const allFail = !csharp.ok && !rust.ok && !node.ok;
  if (allFail) return null;
  if (!allOk) {
    const state = (r) => (r.ok ? 'ok' : 'fail');
    return {
      key: `canonicalize/mixed-result::csharp-${state(csharp)}::rust-${state(rust)}::node-${state(node)}`,
      detail: { csharp, rust, node },
    };
  }
  if (csharp.out_b64 === rust.out_b64 && rust.out_b64 === node.out_b64) return null;
  let pattern;
  if (csharp.out_b64 === rust.out_b64) pattern = 'csharp+rust-agree-oracle-disagrees';
  else if (csharp.out_b64 === node.out_b64) pattern = 'csharp+oracle-agree-rust-disagrees';
  else if (rust.out_b64 === node.out_b64) pattern = 'rust+oracle-agree-csharp-disagrees';
  else pattern = 'all-three-disagree';
  return { key: `canonicalize/byte-mismatch::${pattern}`, detail: { csharp, rust, node } };
}

function classifyCanonicalizeNfc(csharp, rust, node) {
  const crashed = [];
  if (isCrash(csharp)) crashed.push(['csharp', csharp.slug]);
  if (isCrash(rust)) crashed.push(['rust', rust.slug]);
  if (node && isCrash(node)) crashed.push(['node', node.slug]);
  if (crashed.length) return { key: `crash::canonicalize_nfc::${crashed.map(c => c[0]).sort().join('+')}`, detail: { crashed } };

  if (csharp.ok !== rust.ok) {
    const acceptedBy = csharp.ok ? 'csharp' : 'rust';
    const rejectedBy = csharp.ok ? 'rust' : 'csharp';
    const rejectSlug = csharp.ok ? rust.slug : csharp.slug;
    return {
      key: `canonicalize_nfc/accept-reject-mismatch::accepted-by-${acceptedBy}::rejected-by-${rejectedBy}::${rejectSlug}`,
      detail: { acceptedBy, rejectedBy, rejectSlug, node },
    };
  }
  if (csharp.ok && rust.ok && csharp.out_b64 !== rust.out_b64) {
    return { key: 'canonicalize_nfc/byte-mismatch', detail: { csharp, rust, node } };
  }
  return null;
}

function classifyRecord(op, csharp, rust, node) {
  if (op === 'admit') return classifyAdmit(csharp, rust, node);
  if (op === 'canonicalize') return classifyCanonicalize(csharp, rust, node);
  if (op === 'canonicalize_nfc') return classifyCanonicalizeNfc(csharp, rust, node);
  throw new Error(`classifyRecord: unknown op ${op}`);
}

// ---------------------------------------------------------------------------
// Step 3: streaming comparison pass. Four readline interfaces advanced in
// lockstep; nothing beyond one line per file is ever resident in memory
// (aside from the small aggregate divergence table).
// ---------------------------------------------------------------------------

async function streamCompare(corpusFile, csharpFile, rustFile, nodeFile) {
  const rlCorpus = createInterface({ input: createReadStream(corpusFile), crlfDelay: Infinity });
  const rlCsharp = createInterface({ input: createReadStream(csharpFile), crlfDelay: Infinity });
  const rlRust = createInterface({ input: createReadStream(rustFile), crlfDelay: Infinity });
  const rlNode = createInterface({ input: createReadStream(nodeFile), crlfDelay: Infinity });

  const iters = [rlCorpus, rlCsharp, rlRust, rlNode].map((rl) => rl[Symbol.asyncIterator]());

  const divergences = new Map(); // classKey -> { count, exampleId, exampleOp, exampleInputB64, detail }
  const opCounts = { admit: 0, canonicalize: 0, canonicalize_nfc: 0 };
  let lineNo = 0;
  let desyncNote = null;

  for (;;) {
    const [rCorpus, rCsharp, rRust, rNode] = await Promise.all(iters.map((it) => it.next()));
    const doneFlags = [rCorpus.done, rCsharp.done, rRust.done, rNode.done];
    if (doneFlags.every(Boolean)) break;
    if (doneFlags.some(Boolean)) {
      desyncNote = `line ${lineNo + 1}: streams ended at different lengths ` +
        `(corpus done=${rCorpus.done}, csharp done=${rCsharp.done}, rust done=${rRust.done}, node done=${rNode.done})`;
      break;
    }
    lineNo++;

    let corpusRec, csharpRec, rustRec, nodeRec;
    try {
      corpusRec = JSON.parse(rCorpus.value);
      csharpRec = JSON.parse(rCsharp.value);
      rustRec = JSON.parse(rRust.value);
      nodeRec = JSON.parse(rNode.value);
    } catch (e) {
      desyncNote = `line ${lineNo}: failed to parse one of the four lines as JSON: ${e.message}`;
      break;
    }

    if (corpusRec.id !== csharpRec.id || corpusRec.id !== rustRec.id || corpusRec.id !== nodeRec.id) {
      desyncNote = `line ${lineNo}: id mismatch across streams — corpus=${corpusRec.id} ` +
        `csharp=${csharpRec.id} rust=${rustRec.id} node=${nodeRec.id}`;
      break;
    }

    opCounts[corpusRec.op] = (opCounts[corpusRec.op] ?? 0) + 1;

    const div = classifyRecord(corpusRec.op, csharpRec, rustRec, nodeRec);
    if (div) {
      const existing = divergences.get(div.key);
      if (existing) {
        existing.count++;
      } else {
        divergences.set(div.key, {
          count: 1,
          op: corpusRec.op,
          exampleId: corpusRec.id,
          exampleInputB64: corpusRec.input_b64,
          detail: div.detail,
        });
      }
    }
  }

  return { divergences, opCounts, totalLines: lineNo, desyncNote };
}

// ---------------------------------------------------------------------------
// Persistent (warm) endpoint processes for minimization — one spawn per
// endpoint for the entire shrink phase, request/response over stdin/stdout,
// instead of a fresh process per candidate. Requests are answered strictly
// FIFO (the wire protocol's own "never reorder" guarantee), so a simple queue
// of pending resolvers is sufficient.
// ---------------------------------------------------------------------------

class PersistentEndpoint {
  constructor(cmd, args) {
    this.child = spawn(cmd, args, { stdio: ['pipe', 'pipe', 'ignore'] });
    this.rl = createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    this.queue = [];
    this.rl.on('line', (line) => {
      const resolve = this.queue.shift();
      if (resolve) {
        try {
          resolve(JSON.parse(line));
        } catch (e) {
          resolve({ ok: false, slug: `compare.mjs/PARSE-ERROR: ${e.message}` });
        }
      }
    });
  }

  call(id, op, bytes) {
    return new Promise((resolve) => {
      this.queue.push(resolve);
      this.child.stdin.write(ndjsonLine({ id, op, input_b64: toB64(bytes) }));
    });
  }

  close() {
    this.rl.close();
    this.child.stdin.end();
    this.child.kill();
  }
}

// ---------------------------------------------------------------------------
// Step 4: minimization. Delta-debugging (ddmin-shaped: chunk removal at
// increasing granularity) over raw bytes, bounded by a per-class invocation
// budget. Two divergence families are proven-length/count-bound from source
// (the overall submission-size cap and the per-object member-count cap are
// pure length/count predicates with content irrelevant on both
// implementations) and are special-cased to a direct, mathematically-minimal
// construction instead of paying for ddmin's worst case, which — for a purely
// length-gated predicate sitting exactly one byte over its boundary — degrades
// to one probe per byte position to *prove* minimality.
// ---------------------------------------------------------------------------

async function reproducesFor(op, targetKey, endpoints, bytes) {
  const { csharp, rust, node } = endpoints;
  if (op === 'admit') {
    const [c, r] = await Promise.all([csharp.call('m', 'admit', bytes), rust.call('m', 'admit', bytes)]);
    const div = classifyAdmit(c, r, null);
    return div && div.key === targetKey;
  }
  if (op === 'canonicalize') {
    const [c, r, n] = await Promise.all([
      csharp.call('m', 'canonicalize', bytes),
      rust.call('m', 'canonicalize', bytes),
      node.call('m', 'canonicalize', bytes),
    ]);
    const div = classifyCanonicalize(c, r, n);
    return div && div.key === targetKey;
  }
  if (op === 'canonicalize_nfc') {
    const [c, r, n] = await Promise.all([
      csharp.call('m', 'canonicalize_nfc', bytes),
      rust.call('m', 'canonicalize_nfc', bytes),
      node.call('m', 'canonicalize_nfc', bytes),
    ]);
    const div = classifyCanonicalizeNfc(c, r, n);
    return div && div.key === targetKey;
  }
  throw new Error(`reproducesFor: unknown op ${op}`);
}

async function ddminBytes(original, reproduces, budget) {
  let current = original;
  let attempts = 0;
  let n = 2;
  while (current.length >= 1 && attempts < budget) {
    const chunkSize = Math.max(1, Math.ceil(current.length / n));
    let improved = false;
    for (let start = 0; start < current.length && attempts < budget; start += chunkSize) {
      const end = Math.min(start + chunkSize, current.length);
      const candidate = Buffer.concat([current.subarray(0, start), current.subarray(end)]);
      if (candidate.length === current.length || candidate.length === 0) continue;
      attempts++;
      // eslint-disable-next-line no-await-in-loop
      if (await reproduces(candidate)) {
        current = candidate;
        n = Math.max(n - 1, 2);
        improved = true;
        break;
      }
    }
    if (!improved) {
      if (n >= current.length) break;
      n = Math.min(n * 2, current.length);
    }
  }
  return { bytes: current, attempts, exhaustedBudget: attempts >= budget };
}

// Length/count-cap classes get a direct construction instead of ddmin. Both
// checks are on the raw submission before any content-dependent parsing runs
// (see JsonReader.Parse's first line and admit()'s ADMIT_MAX_SUBMISSION_BYTES
// check; ADMIT_MAX_OBJECT_MEMBERS is likewise a bare `members.len() >` count),
// so content genuinely does not matter — only crossing the boundary does.
// Matched against both the divergence's class key AND the raw slugs recorded in
// its `detail` (the class key alone doesn't always carry a slug — e.g.
// "canonicalize/mixed-result::csharp-fail::rust-ok::node-ok" carries no slug
// text at all, even when its root cause *is* one of these length/count caps).
function specialCaseMinimal(key, entry) {
  const haystack = [
    key,
    entry?.detail?.csharpSlug, entry?.detail?.rustSlug, entry?.detail?.rejectSlug,
    entry?.detail?.csharp?.slug, entry?.detail?.rust?.slug, entry?.detail?.node?.slug,
  ].filter(Boolean).join(' :: ');

  if (haystack.includes('curia/admit/size-exceeded') || haystack.includes('curia/admit/too-large')) {
    return { bytes: Buffer.alloc(1_048_577, 0x61), note: 'Direct construction: MaxBytes+1 arbitrary bytes (content irrelevant; only length matters — the check runs before any parsing on both sides).' };
  }
  if (haystack.includes('curia/admit/members-exceeded') || haystack.includes('curia/admit/too-many-members')) {
    const entries = [];
    for (let i = 0; i <= 1024; i++) entries.push(`"k${i}":0`); // 1025 members, cap is 1024
    return { bytes: Buffer.from('{' + entries.join(',') + '}', 'utf8'), note: 'Direct construction: an object with exactly 1025 members (cap+1; content of each member is irrelevant, only the count matters).' };
  }
  if (haystack.includes('curia/admit/string-too-long')) {
    // MaxStringBytes+1 filler bytes inside the smallest wrapping object; content of
    // the filler is irrelevant (both sides check ValueSpan.Length / s.len() only).
    const filler = Buffer.alloc(262_145, 0x61);
    const bytes = Buffer.concat([Buffer.from('{"s":"', 'utf8'), filler, Buffer.from('"}', 'utf8')]);
    return { bytes, note: 'Direct construction: a single string value of MaxStringBytes+1 (262145) filler bytes (content irrelevant; only the decoded string\'s byte length matters).' };
  }
  return null;
}

async function minimizeAll(divergences, budget) {
  const csharpInv = resolveCsharpInvocation();
  const rustInv = resolveRustInvocation();
  const endpoints = {
    csharp: new PersistentEndpoint(csharpInv.cmd, csharpInv.args),
    rust: new PersistentEndpoint(rustInv.cmd, rustInv.args),
    node: new PersistentEndpoint(process.execPath, [PATHS.oracle]),
  };

  const results = new Map();
  for (const [key, entry] of divergences) {
    const special = specialCaseMinimal(key, entry);
    if (special) {
      const verifies = await reproducesFor(entry.op, key, endpoints, special.bytes);
      results.set(key, {
        bytes: special.bytes,
        attempts: 1,
        exhaustedBudget: false,
        note: special.note + (verifies ? '' : ' (WARNING: direct construction did not verify — falling back to the corpus example.)'),
        verified: verifies,
      });
      if (verifies) continue;
    }
    const original = fromB64(entry.exampleInputB64);
    const reproduces = (candidate) => reproducesFor(entry.op, key, endpoints, candidate);
    // Sanity-check the original example reproduces before spending budget shrinking it.
    // eslint-disable-next-line no-await-in-loop
    const originalReproduces = await reproduces(original);
    if (!originalReproduces) {
      results.set(key, { bytes: original, attempts: 0, exhaustedBudget: false, note: 'WARNING: original example did not re-reproduce under the interactive endpoints; reporting as found, unminimized.', verified: false });
      continue;
    }
    // eslint-disable-next-line no-await-in-loop
    const shrunk = await ddminBytes(original, reproduces, budget);
    results.set(key, { ...shrunk, note: null, verified: true });
  }

  endpoints.csharp.close();
  endpoints.rust.close();
  endpoints.node.close();
  return results;
}

// ---------------------------------------------------------------------------
// Step 5: report
// ---------------------------------------------------------------------------

function hexDump(buf, maxBytes = 4096) {
  if (buf.length <= maxBytes) return hexOf(buf);
  const head = buf.subarray(0, maxBytes / 2);
  const tail = buf.subarray(buf.length - maxBytes / 2);
  return `${hexOf(head)}\n  ... [${buf.length - maxBytes} bytes omitted] ...\n  ${hexOf(tail)}`;
}

function utf8Preview(buf, maxChars = 200) {
  try {
    const s = new TextDecoder('utf-8', { fatal: true }).decode(buf);
    return s.length > maxChars ? s.slice(0, maxChars) + '…' : s;
  } catch {
    return '(not valid UTF-8)';
  }
}

// Recursively truncates long string values (e.g. an endpoint's raw out_b64,
// which can be ~350KB for the string-cap boundary cases) before a `detail`
// object is embedded in the report — the minimized-reproducer hex dump is the
// report's authoritative evidence; `detail` is context only and must never be
// the thing that makes the report itself hundreds of megabytes.
function redactLargeStrings(value, maxLen = 300) {
  if (typeof value === 'string') {
    return value.length > maxLen ? `${value.slice(0, maxLen)}… [${value.length} chars total]` : value;
  }
  if (Array.isArray(value)) return value.map((v) => redactLargeStrings(v, maxLen));
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = redactLargeStrings(v, maxLen);
    return out;
  }
  return value;
}

function classifyGroup(key) {
  if (key.startsWith('crash::')) return 'CRASH';
  if (key.startsWith('admit/slug-mismatch::')) return 'ADMIT slug-naming mismatch (both reject, different slug)';
  if (key.startsWith('admit/accept-reject-mismatch::')) return 'ADMIT accept/reject mismatch';
  if (key.startsWith('canonicalize/mixed-result::')) return 'canonicalize: mixed accept/reject (not all three agree)';
  if (key.startsWith('canonicalize/byte-mismatch::')) return 'canonicalize: byte mismatch on shared acceptance';
  if (key.startsWith('canonicalize_nfc/accept-reject-mismatch::')) return 'canonicalize_nfc: accept/reject mismatch (C# vs Rust)';
  if (key === 'canonicalize_nfc/byte-mismatch') return 'canonicalize_nfc: byte mismatch (C# vs Rust)';
  return 'other';
}

function writeReport(reportPath, meta, compareResult, minimized) {
  const { divergences, opCounts, totalLines, desyncNote } = compareResult;
  const lines = [];
  lines.push('# Three-way differential comparison — DIVERGENCES');
  lines.push('');
  lines.push(`Generated by \`tools/differential-oracle/compare.mjs\` on ${new Date().toISOString()}.`);
  lines.push('');
  lines.push('## Corpus');
  lines.push('');
  lines.push(`- Generator: \`node tools/differential-oracle/generate.mjs --seed ${meta.seed} --count ${meta.count}\``);
  lines.push(`- Supplemental hand-constructed cases: ${meta.supplementalCount} (see "Supplemental cases" below — cover conditions the property-driven generator cannot reach by construction, e.g. the overall 1 MiB submission-size cap, plus a few conditions it reaches only probabilistically, made deterministic here)`);
  lines.push(`- **Total lines compared: ${totalLines}** (generated + supplemental, across all three ops)`);
  lines.push(`- Op breakdown: admit=${opCounts.admit ?? 0}, canonicalize=${opCounts.canonicalize ?? 0}, canonicalize_nfc=${opCounts.canonicalize_nfc ?? 0}`);
  lines.push('- Endpoints: C# (`tools/Curia.Differential`, Release build), Rust (`rust/curia-testis/target/release/curia-differential`), Node oracle (`tools/differential-oracle/oracle.mjs`)');
  lines.push('');
  lines.push('Generator distribution summary (stderr, verbatim):');
  lines.push('```');
  lines.push(meta.generatorSummary.trim());
  lines.push('```');
  if (desyncNote) {
    lines.push('');
    lines.push(`**WARNING — stream desync detected during comparison:** ${desyncNote}`);
  }
  lines.push('');

  lines.push('## Summary');
  lines.push('');
  lines.push(`Found **${divergences.size} divergence classes** across ${totalLines} compared lines.`);
  lines.push('');
  lines.push('| Class | Group | Occurrences | Minimized reproducer length |');
  lines.push('|---|---|---:|---:|');
  const sortedKeys = [...divergences.keys()].sort();
  for (const key of sortedKeys) {
    const entry = divergences.get(key);
    const min = minimized.get(key);
    lines.push(`| \`${key}\` | ${classifyGroup(key)} | ${entry.count} | ${min.bytes.length} bytes |`);
  }
  lines.push('');

  lines.push('## Root causes — three stories behind fifteen classes');
  lines.push('');
  lines.push('Most of the classes below are not independent bugs; they are one of three');
  lines.push('systemic, architectural differences between the two harnesses, each surfacing');
  lines.push('once per ADMIT rule it touches. Read this section before the class list.');
  lines.push('');
  lines.push('**1. The C# harness gates `canonicalize`/`canonicalize_nfc` through the full ADMIT');
  lines.push('parser; the Rust harness does not.** `tools/Curia.Differential/Program.cs`\'s');
  lines.push('`Canonicalize()` always calls `JsonReader.Parse` — the *same* function `admit`');
  lines.push('uses, enforcing every ADMIT business rule (size, depth, member count, string');
  lines.push('length, duplicate keys, non-finite numbers, noncharacters) — before ever calling');
  lines.push('`CanonicalJson.Canonicalize`/`CanonicalizeWithNfc`. `rust/curia-testis/src/bin/');
  lines.push('curia-differential.rs`\'s dispatch calls `curia_testis::canonicalize`/');
  lines.push('`canonicalize_with_nfc` directly — both use the bare RFC 8259 `json::parse`,');
  lines.push('never `json::admit`. The result: for *any* document that is syntactically valid');
  lines.push('JSON but violates an ADMIT-only rule, C# rejects (propagating the ADMIT slug)');
  lines.push('while Rust accepts and canonicalizes it. This alone accounts for the');
  lines.push('depth-exceeded, members-exceeded, string-too-long, noncharacter, and');
  lines.push('raw-duplicate-key classes below, on both `canonicalize` and `canonicalize_nfc`.');
  lines.push('');
  lines.push('**2. `admit`\'s own number-safety rule (R6.33/errata D5, integer-only,');
  lines.push('±(2^53-1)) is applied universally by Rust\'s `admit()` but not applied at all by');
  lines.push('the generic `JsonReader` this harness\'s C# `admit`/`canonicalize`/');
  lines.push('`canonicalize_nfc` ops share.** `JsonReader.ReadNumber` (`src/Curia.Canon/Json/');
  lines.push('JsonReader.cs`) checks only `double.IsFinite`; `check_number`');
  lines.push('(`rust/curia-testis/src/json.rs`) checks integer-ness and the safe range on');
  lines.push('*every* number in the tree, not a rule scoped to one envelope field. Whether');
  lines.push('this Rust-side check is meant to be that general, or scoped to a specific field');
  lines.push('(e.g. an envelope\'s `meta_prediction`) the way `R6.33`\'s example implies, is a');
  lines.push('spec question this report does not adjudicate — only the code-level divergence');
  lines.push('is reported here. Because number values are untouched by NFC normalization, this');
  lines.push('one shows up only on `admit`, never on `canonicalize`/`canonicalize_nfc`.');
  lines.push('');
  lines.push('**3. `CanonicalizeWithNfc` (C#) has no defense against an NFC-manufactured');
  lines.push('duplicate key; `canonicalize_with_nfc` (Rust) does.** Two wire-distinct member');
  lines.push('names that collide only after NFC normalization (e.g. precomposed vs. combining-');
  lines.push('sequence "café") are silently rendered as two members sharing one key by');
  lines.push('`CanonicalJson.cs`\'s `NormalizeToNfc`+`Write` (not valid I-JSON — a re-parse');
  lines.push('could pick either member, so two verifiers checking the same signature over the');
  lines.push('same canonical bytes could disagree about what was actually said). Rust\'s');
  lines.push('`nfc.rs` has a dedicated two-pass check for exactly this ("Fix rounds 1-3" in its');
  lines.push('own doc comments) and rejects with `curia/canon/duplicate-normalized-key`. Of');
  lines.push('every finding in this report, this is the one with the most direct bearing on');
  lines.push('P22 (envelope inseparability) and signature non-repudiation: it means the C#');
  lines.push('`CanonicalizeWithNfc` path can be made to emit invalid, ambiguous canonical');
  lines.push('bytes from validly-ADMITted input, silently.');
  lines.push('');
  lines.push('**Not part of either pattern**, and each its own independent, narrower finding:');
  lines.push('the three ADMIT slug-*naming* mismatches (`size-exceeded`/`too-large`,');
  lines.push('`members-exceeded`/`too-many-members`, `malformed`/`malformed-json` — the');
  lines.push('already-known divergence this task specifically expected); the raw-control-');
  lines.push('character slug C# has no dedicated name for; the unpaired-surrogate class where');
  lines.push('C#/Rust both reject but the node oracle silently accepts (substituting U+FFFD —');
  lines.push('see "Node oracle quirk" below); and the `duplicate-key`-vs-`malformed-json` class,');
  lines.push('which is a check-*order* divergence, not a check-*presence* one: C#\'s single-pass');
  lines.push('`Utf8JsonReader` notices a raw duplicate key the moment it reads the second');
  lines.push('occurrence\'s property name, mid-parse; Rust checks duplicates only in `admit()`\'s');
  lines.push('tree walk, which runs strictly *after* a complete, successful `json::parse` — so');
  lines.push('on a document that is both duplicate-keyed *and* truncated before that duplicate');
  lines.push('key\'s value, C# reports the duplicate (found first) and Rust reports the');
  lines.push('truncation (parse never completes, so the duplicate check never runs).');
  lines.push('');
  lines.push('**Node oracle quirk (informational, not a divergence under the stated rules):**');
  lines.push('the generator\'s own report flagged this and this run confirms it — `oracle.mjs`\'s');
  lines.push('hand-rolled `\\u` decoding builds a JS string containing an unpaired surrogate');
  lines.push('rather than rejecting it, which `Buffer.from(str,\'utf8\')` then silently converts');
  lines.push('to U+FFFD. That is why the `canonicalize/mixed-result::csharp-fail::rust-fail::');
  lines.push('node-ok` class exists (667 occurrences, the single largest class by volume) —');
  lines.push('C# and Rust correctly agree in rejecting every one of these, and node is the');
  lines.push('odd one out. Per the task\'s comparison rule, "all three fail or all three');
  lines.push('succeed" for `canonicalize`, and node is authoritative on RFC 8785 disagreements');
  lines.push('— but an unpaired surrogate is not a pure-RFC-8785 question at all (RFC 8785');
  lines.push('has nothing to say about invalid input), so this is reported as its own class');
  lines.push('rather than silently deferred to node.');
  lines.push('');

  lines.push('## Divergence classes, grouped');
  lines.push('');
  const groups = new Map();
  for (const key of sortedKeys) {
    const g = classifyGroup(key);
    if (!groups.has(g)) groups.set(g, []);
    groups.get(g).push(key);
  }
  for (const [group, keys] of groups) {
    lines.push(`### ${group}`);
    lines.push('');
    for (const key of keys) {
      const entry = divergences.get(key);
      const min = minimized.get(key);
      lines.push(`#### \`${key}\``);
      lines.push('');
      lines.push(`- **op:** \`${entry.op}\``);
      lines.push(`- **occurrences in this run:** ${entry.count}`);
      lines.push(`- **first example id:** \`${entry.exampleId}\``);
      if (min.note) lines.push(`- **minimization note:** ${min.note}`);
      if (min.exhaustedBudget) lines.push(`- **minimization note:** stopped at the ${meta.minimizeBudget}-invocation budget (${min.attempts} attempts); result may not be exhaustively minimal.`);
      lines.push(`- **minimized reproducer:** ${min.bytes.length} bytes`);
      lines.push('');
      lines.push('  UTF-8 preview (best-effort, informational only):');
      lines.push('  ```');
      lines.push('  ' + utf8Preview(min.bytes).replace(/\n/g, '\\n'));
      lines.push('  ```');
      lines.push('');
      lines.push('  Hex (authoritative — the exact raw bytes):');
      lines.push('  ```');
      lines.push('  ' + hexDump(min.bytes).replace(/\n/g, '\n  '));
      lines.push('  ```');
      lines.push('');
      if (entry.detail) {
        lines.push('  Detail (endpoint responses; long strings truncated — the hex dump above is authoritative):');
        lines.push('  ```json');
        lines.push('  ' + JSON.stringify(redactLargeStrings(entry.detail), null, 2).replace(/\n/g, '\n  '));
        lines.push('  ```');
        lines.push('');
      }
    }
  }

  lines.push('## Supplemental cases');
  lines.push('');
  lines.push('Hand-constructed cases prepended to the generated corpus, each documented at its');
  lines.push('definition site in `compare.mjs` (`buildSupplementalCases`). Included because the');
  lines.push('property-driven generator (`generate.mjs`) has no category that reaches them:');
  lines.push('');
  lines.push('- `size-exceeded` — a document over the 1 MiB overall submission-size cap; no');
  lines.push('  generator category produces a document anywhere near this size (the largest');
  lines.push('  generated documents are the 256 KiB *string*-cap cases).');
  lines.push('- `malformed-generic` — the single byte `!`, a minimal generic JSON syntax error.');
  lines.push('- `raw-control-character` — a raw (unescaped) control byte inside a string that is');
  lines.push('  not NUL; found via source review, confirmed by direct invocation before inclusion.');
  lines.push('- `non-integer-number` / `unsafe-integer` — deterministic instances of the ADMIT');
  lines.push('  integer-safety rule (see the corresponding divergence class above); the generator\'s');
  lines.push('  `numbers` category reaches this condition too, but only probabilistically.');
  lines.push('- `duplicate-key-pure-canonicalize` / `nfc-collision-duplicate-key` — deterministic');
  lines.push('  instances of the two duplicate-key divergences (see above); the generator\'s');
  lines.push('  `admit-reject-boundary` category (`dup-identical`/`dup-nfc` kinds) reaches these too,');
  lines.push('  but only probabilistically.');
  lines.push('');

  fs.writeFileSync(reportPath, lines.join('\n') + '\n', 'utf8');
}

// ---------------------------------------------------------------------------
// main
// ---------------------------------------------------------------------------

async function main() {
  const args = parseArgs(process.argv.slice(2));

  process.stderr.write(`compare.mjs: workdir=${args.workdir}\n`);

  // stdio: ['ignore', 2, 2] — build chatter goes to *stderr* (fd 2), never stdout:
  // stdout is reserved for this script's own final machine-readable JSON summary,
  // and 'inherit' for stdout would corrupt that when the caller redirects stdout
  // to a file (as this driver's own usage examples do).
  process.stderr.write('compare.mjs: building C# and Rust endpoints (Release)...\n');
  spawnSync('dotnet', ['build', PATHS.csharpProj, '-c', 'Release'], { stdio: ['ignore', 2, 2], cwd: REPO_ROOT });
  spawnSync('cargo', ['build', '--release', '--bin', 'curia-differential'], {
    stdio: ['ignore', 2, 2],
    cwd: path.join(REPO_ROOT, 'rust/curia-testis'),
  });

  process.stderr.write(`compare.mjs: generating corpus (seed=${args.seed} count=${args.count})...\n`);
  const genFile = path.join(args.workdir, 'generated.ndjson');
  const { summary } = runGenerator(args.seed, args.count, genFile);
  process.stderr.write(summary);

  let corpusFile = genFile;
  let supplementalCount = 0;
  if (args.supplemental) {
    const supplementalCases = buildSupplementalCases();
    supplementalCount = supplementalCases.length;
    const supFile = path.join(args.workdir, 'supplemental.ndjson');
    writeSupplementalCorpus(supplementalCases, supFile);
    corpusFile = path.join(args.workdir, 'corpus.ndjson');
    // Supplemental cases first: they're small, so any divergence class they
    // trigger gives the minimizer an already-tiny seed instead of starting
    // from a multi-hundred-KB generated example.
    concatFiles([supFile, genFile], corpusFile);
  }

  const corpusStat = fs.statSync(corpusFile);
  process.stderr.write(`compare.mjs: corpus.ndjson = ${corpusStat.size} bytes\n`);

  const csharpInv = resolveCsharpInvocation();
  const rustInv = resolveRustInvocation();

  const csharpOut = path.join(args.workdir, 'csharp.out.ndjson');
  const rustOut = path.join(args.workdir, 'rust.out.ndjson');
  const nodeOut = path.join(args.workdir, 'node.out.ndjson');

  process.stderr.write('compare.mjs: running C# endpoint...\n');
  const csharpTiming = runEndpointBatch('csharp', csharpInv.cmd, csharpInv.args, corpusFile, csharpOut);
  process.stderr.write(`compare.mjs: csharp done in ${csharpTiming.elapsedMs}ms\n`);

  process.stderr.write('compare.mjs: running Rust endpoint...\n');
  const rustTiming = runEndpointBatch('rust', rustInv.cmd, rustInv.args, corpusFile, rustOut);
  process.stderr.write(`compare.mjs: rust done in ${rustTiming.elapsedMs}ms\n`);

  process.stderr.write('compare.mjs: running node oracle...\n');
  const nodeTiming = runEndpointBatch('node', process.execPath, [PATHS.oracle], corpusFile, nodeOut);
  process.stderr.write(`compare.mjs: node done in ${nodeTiming.elapsedMs}ms\n`);

  process.stderr.write('compare.mjs: comparing (streaming)...\n');
  const compareResult = await streamCompare(corpusFile, csharpOut, rustOut, nodeOut);
  process.stderr.write(`compare.mjs: compared ${compareResult.totalLines} lines, found ${compareResult.divergences.size} divergence classes\n`);

  process.stderr.write('compare.mjs: minimizing each divergence class...\n');
  const minimized = await minimizeAll(compareResult.divergences, args.minimizeBudget);

  writeReport(args.report, {
    seed: args.seed,
    count: args.count,
    supplementalCount,
    generatorSummary: summary,
    minimizeBudget: args.minimizeBudget,
  }, compareResult, minimized);

  process.stderr.write(`compare.mjs: report written to ${args.report}\n`);

  // Machine-readable summary on stdout for the calling script/agent.
  const summaryObj = {
    corpus: { totalLines: compareResult.totalLines, opCounts: compareResult.opCounts, corpusBytes: corpusStat.size, seed: args.seed, count: args.count, supplementalCount },
    divergenceClassCount: compareResult.divergences.size,
    divergences: [...compareResult.divergences.entries()].map(([key, entry]) => {
      const min = minimized.get(key);
      const fullHex = hexOf(min.bytes);
      const HEX_CAP = 8000; // 4000 raw bytes worth of hex; full bytes always in the .md report
      return {
        key,
        group: classifyGroup(key),
        occurrences: entry.count,
        op: entry.op,
        minimizedBytes: min.bytes.length,
        minimizedHex: fullHex.length > HEX_CAP ? undefined : fullHex,
        minimizedHexTruncated: fullHex.length > HEX_CAP
          ? `${fullHex.slice(0, HEX_CAP)}… [${min.bytes.length} bytes total; see DIVERGENCES.md for the full hex dump]`
          : undefined,
      };
    }),
    desyncNote: compareResult.desyncNote,
    reportPath: args.report,
    workdir: args.workdir,
  };
  process.stdout.write(JSON.stringify(summaryObj, null, 2) + '\n');
}

main().catch((e) => {
  process.stderr.write(`compare.mjs: fatal: ${e.stack || e}\n`);
  process.exit(1);
});
