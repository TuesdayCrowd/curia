#!/usr/bin/env node
// generate.mjs — property-driven document generator for the differential
// canonicalization harness (design spec §9 / docs/superpowers/plans/2026-08-11-canon-testis.md
// Task 7, "Step 1: Generate documents ... Property-driven generation, not a fixed list").
//
// Writes NDJSON command lines to stdout, one per (document, op) pair, in the exact wire
// protocol tools/Curia.Differential, rust/curia-testis/src/bin/curia-differential.rs and
// tools/differential-oracle/oracle.mjs all already implement:
//
//   in:  {"id":"<string>","op":"admit"|"canonicalize"|"canonicalize_nfc","input_b64":"<base64>"}
//   out: {"id":"<same id>","ok":true,"out_b64":"<base64>"}
//     or {"id":"<same id>","ok":false,"slug":"<error slug>"}
//
// This file only ever writes valid wire-protocol lines to stdout — nothing else, so its
// output can be piped straight into any endpoint:
//
//   node tools/differential-oracle/generate.mjs --seed 20260812 --count 900 \
//     | dotnet run --project tools/Curia.Differential -- \
//     > csharp-results.ndjson
//
// A distribution summary (per-category counts, benign/adversarial split, total lines) is
// written to stderr after generation, never to stdout, matching the same "diagnostics go
// to stderr" hygiene the three endpoints hold themselves to.
//
// ## Determinism
//
// No Math.random() anywhere in this file. All randomness flows through one SplitMix64
// instance seeded from --seed; every generator function reads from that single instance in
// a fixed call order determined solely by --count, so a given (seed, count) pair always
// walks the PRNG the same number of times in the same sequence and therefore always
// produces byte-identical stdout. See the README's "Reproducibility" note below for how
// this was confirmed empirically, not just argued.
//
// ## What "one document" means here
//
// Each generated document is emitted under all three ops (admit, canonicalize,
// canonicalize_nfc) with three distinct ids sharing one document-index prefix, so a
// comparison script can line up "did all three endpoints admit this?" and "did the two
// that canonicalize it produce the same bytes?" without re-deriving the grouping itself.
//
// ## Where the boundary numbers come from
//
// The 32-container depth cap, 1024-member cap, and 262,144-byte string cap are
// AdmitLimits.Default (src/Curia.Canon/Json/JsonReader.cs) / ADMIT_MAX_* (rust/curia-testis/
// src/json.rs) — frozen by R15.1. The safe-integer range (±(2^53−1)) and integer-only rule
// are R6.33/errata D5. The noncharacter, duplicate-key, and raw-NUL/invalid-UTF-8 rules are
// R6.15/errata D7. NFC normalization is R6.9; the pinned Unicode version is R6.34.

'use strict';

// ---------------------------------------------------------------------------
// PRNG: splitmix64. Deterministic, small, and good enough for corpus generation
// (this is a fuzzer's input generator, not a security primitive).
// ---------------------------------------------------------------------------

const MASK64 = (1n << 64n) - 1n;
const GOLDEN = 0x9e3779b97f4a7c15n;

class SplitMix64 {
  constructor(seed) {
    this.state = BigInt.asUintN(64, BigInt(seed));
  }

  nextU64() {
    this.state = (this.state + GOLDEN) & MASK64;
    let z = this.state;
    z = ((z ^ (z >> 30n)) * 0xbf58476d1ce4e5b9n) & MASK64;
    z = ((z ^ (z >> 27n)) * 0x94d049bb133111ebn) & MASK64;
    z = z ^ (z >> 31n);
    return z;
  }

  /** [0, 1) */
  nextFloat() {
    return Number(this.nextU64() >> 11n) / 9007199254740992; // / 2^53
  }

  /** Inclusive of both min and max. Not uniform for huge ranges; fine here. */
  int(min, max) {
    if (max < min) throw new Error(`int(${min}, ${max}): max < min`);
    const range = BigInt(max - min + 1);
    return min + Number(this.nextU64() % range);
  }

  chance(p) {
    return this.nextFloat() < p;
  }

  choice(arr) {
    return arr[this.int(0, arr.length - 1)];
  }

  /** Fisher-Yates, deterministic under this rng. Does not mutate the input. */
  shuffle(arr) {
    const a = arr.slice();
    for (let i = a.length - 1; i > 0; i--) {
      const j = this.int(0, i);
      [a[i], a[j]] = [a[j], a[i]];
    }
    return a;
  }
}

// ---------------------------------------------------------------------------
// A tiny JSON-value builder with per-character escape control, plus a
// stringifier that turns it into JSON *text*. Deliberately not JSON.stringify:
// it needs to (a) preserve object member insertion order exactly as built,
// including duplicates, (b) emit raw, arbitrary-precision number literals
// verbatim, and (c) choose, per code point, whether a string character is
// written literally, as a named short escape, as a \uXXXX escape (or
// surrogate-pair \uXXXX\uXXXX for astral code points), or as raw unchecked
// text (for constructing intentionally *unpaired* \u escapes, which are not
// expressible as a real JS code point at all).
//
// Every node built this way is well-formed JSON text over valid, real
// Unicode scalar values — so the final UTF-8 encode (Buffer.from(text,
// 'utf8')) can never itself produce anything invalid. Genuinely malformed
// byte sequences (invalid UTF-8, raw NUL, truncation, unbalanced brackets)
// are built separately, directly as byte buffers — see "Malformed byte-level
// generators" below.
// ---------------------------------------------------------------------------

const Null = () => ({ t: 'null' });
const Bool = (v) => ({ t: 'bool', v });
const Num = (text) => ({ t: 'num', text });
const Arr = (items) => ({ t: 'arr', items });
const Obj = (entries) => ({ t: 'obj', entries }); // entries: [key: string|StrNode, value: Node][]

const NAMED_ESCAPE = { 0x22: '\\"', 0x5c: '\\\\', 0x08: '\\b', 0x0c: '\\f', 0x0a: '\\n', 0x0d: '\\r', 0x09: '\\t' };
const NAMED_CONTROL_CODEPOINTS = new Set([0x08, 0x09, 0x0a, 0x0c, 0x0d]);

/**
 * text -> pieces, one per Unicode code point. Mandatory escapes ('"', '\',
 * every control < 0x20) are chosen automatically and correctly; everything
 * else is literal unless forced into \u form via opts.forceU (a Set of code
 * points).
 */
function piecesFromText(text, opts = {}) {
  const forceU = opts.forceU ?? new Set();
  const pieces = [];
  for (const ch of text) { // iterates by code point; astral chars come through whole
    const cp = ch.codePointAt(0);
    if (forceU.has(cp)) pieces.push({ mode: 'u', cp });
    else if (cp === 0x22 || cp === 0x5c) pieces.push({ mode: 'named', cp });
    else if (cp < 0x20) pieces.push({ mode: NAMED_CONTROL_CODEPOINTS.has(cp) ? 'named' : 'u', cp });
    else pieces.push({ mode: 'lit', cp });
  }
  return pieces;
}

/** Same as piecesFromText, but '/' is written as the (optional, legal) \/ escape. */
function piecesForcingSlash(text) {
  return [...text].map((ch) => {
    const cp = ch.codePointAt(0);
    if (cp === 0x2f) return { mode: 'slash', cp };
    if (cp === 0x22 || cp === 0x5c) return { mode: 'named', cp };
    if (cp < 0x20) return { mode: NAMED_CONTROL_CODEPOINTS.has(cp) ? 'named' : 'u', cp };
    return { mode: 'lit', cp };
  });
}

function S(text, opts) {
  return { t: 'str', pieces: piecesFromText(text, opts) };
}

/** Build a string node from an explicit piece list — bypasses piecesFromText entirely. */
function rawStr(pieces) {
  return { t: 'str', pieces };
}

/** One \uXXXX piece with unchecked hex — used to write unpaired surrogate escapes. */
function uEscape(hex4) {
  return { mode: 'rawtext', text: '\\u' + hex4 };
}

function stringifyStrPiece(p, out) {
  if (p.mode === 'rawtext') return out + p.text;
  const cp = p.cp;
  if (p.mode === 'slash' && cp === 0x2f) return out + '\\/';
  if (p.mode === 'named' && NAMED_ESCAPE[cp] !== undefined) return out + NAMED_ESCAPE[cp];
  if (p.mode === 'u' || p.mode === 'named') {
    if (cp > 0xffff) {
      const c = cp - 0x10000;
      const hi = 0xd800 + (c >> 10);
      const lo = 0xdc00 + (c & 0x3ff);
      return out + '\\u' + hi.toString(16).padStart(4, '0') + '\\u' + lo.toString(16).padStart(4, '0');
    }
    return out + '\\u' + cp.toString(16).padStart(4, '0');
  }
  // 'lit' — but never emit a raw quote/backslash/control literally, even if
  // asked to: that would silently produce invalid JSON text instead of the
  // intended edge case.
  if (cp === 0x22 || cp === 0x5c || cp < 0x20) return out + '\\u' + cp.toString(16).padStart(4, '0');
  return out + String.fromCodePoint(cp);
}

function stringifyStr(node) {
  let out = '"';
  for (const p of node.pieces) out = stringifyStrPiece(p, out);
  return out + '"';
}

function stringify(node) {
  switch (node.t) {
    case 'null': return 'null';
    case 'bool': return node.v ? 'true' : 'false';
    case 'num': return node.text;
    case 'str': return stringifyStr(node);
    case 'arr': return '[' + node.items.map(stringify).join(',') + ']';
    case 'obj':
      return '{' + node.entries.map(([k, v]) => {
        const keyNode = typeof k === 'string' ? S(k) : k;
        return stringifyStr(keyNode) + ':' + stringify(v);
      }).join(',') + '}';
    default:
      throw new Error(`stringify: unknown node type ${node.t}`);
  }
}

/** Every code point involved here is a real, well-formed Unicode scalar value (never a
 * lone surrogate — those are built via uEscape's rawtext, which is plain ASCII), so this
 * UTF-8 encode can never itself introduce malformed bytes. */
function toBytes(node) {
  return Buffer.from(stringify(node), 'utf8');
}

// ---------------------------------------------------------------------------
// Number-literal generation: build JSON number *text* directly (never via a
// JS `number`, which cannot represent most of what's interesting here —
// integers past 2^53, 25-digit decimals, deliberately padded "tie" spellings)
// while staying inside the JSON number grammar:
//   -?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?
// ---------------------------------------------------------------------------

function randomDigits(rng, n) {
  let s = '';
  for (let i = 0; i < n; i++) s += rng.int(0, 9);
  return s;
}

function randomIntegerPart(rng, maxDigits) {
  if (rng.chance(0.1)) return '0';
  const digitCount = rng.int(1, Math.max(1, maxDigits));
  const first = rng.int(1, 9);
  return String(first) + randomDigits(rng, digitCount - 1);
}

function randomNumberLiteral(rng, opts = {}) {
  const sign = rng.chance(0.35) ? '-' : '';
  const intPart = randomIntegerPart(rng, opts.maxIntDigits ?? 20);
  const hasFrac = rng.chance(opts.fracChance ?? 0.5);
  const frac = hasFrac ? '.' + randomDigits(rng, rng.int(1, opts.maxFracDigits ?? 18)) : '';
  const hasExp = rng.chance(opts.expChance ?? 0.3);
  let exp = '';
  if (hasExp) {
    const e = rng.chance(0.5) ? 'e' : 'E';
    const expSign = rng.choice(['', '+', '-']);
    exp = e + expSign + randomDigits(rng, rng.int(1, opts.maxExpDigits ?? 3));
  }
  return sign + intPart + frac + exp;
}

// Curated boundary/adversarial-to-serializers literals. Every entry parses as a finite
// JSON number; several deliberately overflow or underflow a double at parse time in ways
// that are legal (round to +/-Infinity is rejected upstream by ADMIT, not here — these
// values are all finite as doubles) — see conformance/numbers/ and conformance/
// admit-reject/{non-integer,unsafe-integer,non-finite}-number for the fixtures this
// mirrors and extends.
const NUMBER_BOUNDARIES = [
  '9007199254740991', '-9007199254740991', // 2^53-1: I-JSON safe-integer boundary
  '9007199254740992', '-9007199254740992', // 2^53: exactly representable, already unsafe
  '9007199254740993', '-9007199254740993', // 2^53+1: rounds to 2^53, precision lost
  '18446744073709551615',                  // 2^64-1
  '123456789012345680000',                 // known "exact expansion" trap (numbers/large-exact-expansion)
  '1e20', '1e21', '1e22', '-1e21',          // ECMAScript's fixed/exponential switch above 1e21
  '9.999999999999999e20', '999999999999999900000',
  '1e-5', '1e-6', '1e-7', '1e-8', '-1e-7',  // ECMAScript's switch is n <= -6, one order off .NET's default
  '1.0e-6', '1.0e-7', '0.000001', '0.0000001',
  '-0', '-0.0', '0', '0.0', '0e0', '-0e0', '0e-5', // RFC 8785 requires -0 -> "0"
  '1.50', '1.5', '1.500000', '100.00', '1.0', '1E2', '1e+2', '1e2',
  '1.23456789012345e+30',
  '-1.7976931348623157e308', '1.7976931348623157e308', // +/- Number.MAX_VALUE
  '5e-324', '2.2250738585072014e-308',                 // smallest subnormal / smallest normal double
];

// "Ties": multiple valid spellings of the same double, which a correct canonicalizer must
// collapse to one shortest form regardless of which spelling it was fed.
const TIE_SPELLINGS = {
  1.5: ['1.5', '1.50', '1.500000', '15e-1', '0.15e1', '1.5e0'],
  1: ['1', '1.0', '1.00', '1e0', '10e-1', '0.1e1'],
  0: ['0', '0.0', '0e0', '-0', '-0.0', '0.00'],
  100: ['100', '1e2', '1.0e2', '0.1e3', '100.0', '1E2'],
  1.25: ['1.25', '1.250', '125e-2', '0.125e1', '1.25e0'],
  10: ['10', '1e1', '1.0e1', '0.1e2', '10.0'],
};
const TIE_KEYS = Object.keys(TIE_SPELLINGS);

// ---------------------------------------------------------------------------
// Shared structural builders — used by both the "must accept" and "must
// reject" sides of the ADMIT boundary categories (they differ only in n).
// ---------------------------------------------------------------------------

/** n nested containers (object/array chosen per level) around a scalar leaf. Depth, as
 * JsonReader/curia_testis's admit() count it, equals n (the outermost container is depth 1). */
function buildNested(rng, n) {
  let node = Num('0');
  for (let i = 0; i < n; i++) node = rng.chance(0.5) ? Obj([['a', node]]) : Arr([node]);
  return node;
}

/** An object with exactly n distinct members k0..k(n-1). Sequential keys guarantee
 * uniqueness cheaply at n up to a few thousand. */
function buildMembers(n) {
  const entries = [];
  for (let i = 0; i < n; i++) entries.push([`k${i}`, Num(String(i % 1000))]);
  return Obj(entries);
}

/** A single string member whose *raw token content* is exactly byteLen bytes — the exact
 * quantity AdmitLimits.MaxStringBytes / ADMIT_MAX_STRING_BYTES caps. width=1 uses a 1-byte
 * ASCII filler ('a'); width=2 uses a 2-byte UTF-8 filler ('é', U+00E9) so the cap is also
 * exercised with genuinely multi-byte content, not just ASCII. */
function buildStringOfBytes(byteLen, width) {
  let text;
  if (width === 1) text = 'a'.repeat(byteLen);
  else if (width === 2) {
    if (byteLen % 2 !== 0) throw new Error('buildStringOfBytes: width=2 requires an even byteLen');
    text = 'é'.repeat(byteLen / 2);
  } else {
    throw new Error(`buildStringOfBytes: unsupported width ${width}`);
  }
  return Obj([['s', S(text)]]);
}

/** A small, all-ASCII, string-value-free JSON skeleton (only objects/arrays/numbers) of
 * random shape. String-value-free is deliberate: it makes every '{','}','[',']' byte in
 * the rendered text unambiguously structural, so the truncation/unbalanced-bracket
 * mutators below can operate on raw bytes without risk of mistaking a bracket that
 * happens to sit inside string *content* for a structural one. */
function buildAsciiSkeleton(rng) {
  const depth = rng.int(2, 6);
  const width = rng.int(2, 5);
  function rec(d) {
    if (d === 0) return Num(String(rng.int(0, 999)));
    const n = rng.int(1, width);
    if (rng.chance(0.5)) {
      const entries = [];
      for (let i = 0; i < n; i++) entries.push([`k${i}`, rec(d - 1)]);
      return Obj(entries);
    }
    const items = [];
    for (let i = 0; i < n; i++) items.push(rec(d - 1));
    return Arr(items);
  }
  return rec(depth);
}

// ---------------------------------------------------------------------------
// Category: key-ordering. Supplementary-plane keys mixed with BMP keys near
// the surrogate boundary, so UTF-16-code-unit order (what RFC 8785 §3.2.3
// mandates, and what a naive UTF-8-byte-order sort disagrees with) is
// actually exercised, not merely present.
// ---------------------------------------------------------------------------

const SPECIAL_KEY_CANDIDATES = [
  '', 'a', 'A', 'Z', 'z', '0', '9', '_',
  '', '', '�', '퟿', // private-use start, private-use, replacement char, just-below-surrogates
];

function randomAstralCodepoint(rng) {
  let cp;
  do {
    cp = 0x10000 + rng.int(0, 0xfffff);
  } while ((cp & 0xfffe) === 0xfffe); // skip the two noncharacters at the end of every plane
  return cp;
}

function genKeyOrderingDoc(rng) {
  const n = rng.int(4, 8);
  const keys = new Set();
  while (keys.size < Math.min(2, n)) keys.add(rng.choice(SPECIAL_KEY_CANDIDATES));
  while (keys.size < n) {
    if (rng.chance(0.5)) keys.add(String.fromCodePoint(randomAstralCodepoint(rng)));
    else keys.add(rng.choice(SPECIAL_KEY_CANDIDATES));
  }
  const order = rng.shuffle([...keys]);
  const entries = order.map((k, i) => [k, Num(String(i))]);
  return { node: Obj(entries), note: `${order.length} keys mixing supplementary-plane and BMP surrogate-boundary keys` };
}

// ---------------------------------------------------------------------------
// Category: unicode-nfd. NFD sequences, the composition-exclusion character,
// the OHM SIGN singleton, and a recently-assigned code point, in both keys
// and values (R6.9 / R6.34).
// ---------------------------------------------------------------------------

const NFD_SNIPPETS = [
  'é', // -> é
  'Å', // -> Å
  'ö', // -> ö
  'ñ', // -> ñ
  'ü', // -> ü
  'ç', // -> ç
];
const SINGLETON_OHM = 'Ω'; // OHM SIGN -> GREEK CAPITAL LETTER OMEGA (U+03A9) under NFC
const COMPOSITION_EXCLUSION = 'דּ'; // Hebrew letter DALET WITH DAGESH — on the exclusion list, must NOT recompose
// Todhri block, assigned in Unicode 16.0 (the version R6.34 pins) — same code point family
// as conformance/unicode/unicode16-recent-codepoint.
const RECENT_CODEPOINTS = ['\u{10740}', '\u{10741}', '\u{10747}', '\u{1074a}'];

function genUnicodeDoc(rng) {
  const pick = rng.choice(['nfd-value', 'nfd-key', 'nfd-both', 'ohm', 'exclusion', 'recent', 'multi-mark', 'already-nfc']);
  switch (pick) {
    case 'nfd-value': {
      const snip = rng.choice(NFD_SNIPPETS);
      return { node: Obj([['k', S('word_' + snip)]]), note: `NFD sequence in value: ${JSON.stringify(snip)}` };
    }
    case 'nfd-key': {
      const snip = rng.choice(NFD_SNIPPETS);
      return { node: Obj([['key_' + snip, Num('1')]]), note: `NFD sequence in key: ${JSON.stringify(snip)}` };
    }
    case 'nfd-both': {
      const a = rng.choice(NFD_SNIPPETS);
      const b = rng.choice(NFD_SNIPPETS);
      return { node: Obj([['k_' + a, S('v_' + b)]]), note: 'NFD sequence in both key and value (not just values)' };
    }
    case 'ohm':
      return { node: Obj([[SINGLETON_OHM, S('ohm-sign-singleton')]]), note: 'U+2126 OHM SIGN, a singleton decomposition to U+03A9' };
    case 'exclusion':
      return { node: Obj([[COMPOSITION_EXCLUSION, Num('1')]]), note: 'U+FB33, on the NFC composition-exclusion list' };
    case 'recent': {
      const cp = rng.choice(RECENT_CODEPOINTS);
      return { node: Obj([['k', S(cp)]]), note: `recently-assigned code point U+${cp.codePointAt(0).toString(16).toUpperCase()} (Unicode 16.0, Todhri block)` };
    }
    case 'multi-mark': {
      const base = rng.choice(['a', 'o', 'u', 'e']);
      const marks = rng.shuffle(['́', '̀', '̈', '̊']).slice(0, rng.int(2, 3));
      const text = base + marks.join('');
      return { node: Obj([['k', S(text)]]), note: 'multiple combining marks in sequence (composition + reordering by combining class)' };
    }
    case 'already-nfc':
    default:
      return { node: Obj([['k', S('Å')]]), note: 'already-NFC input; CanonicalizeWithNfc must be idempotent on it' };
  }
}

// ---------------------------------------------------------------------------
// Category: numbers.
// ---------------------------------------------------------------------------

function genNumberDoc(rng) {
  const shape = rng.choice(['boundary', 'biginteger', 'decimal', 'exponent', 'tie-spelling']);
  let text;
  let note;
  switch (shape) {
    case 'boundary':
      text = rng.choice(NUMBER_BOUNDARIES);
      note = 'curated boundary literal';
      break;
    case 'biginteger':
      text = (rng.chance(0.5) ? '-' : '') + randomIntegerPart(rng, rng.int(16, 30));
      note = 'long integer literal, likely to lose precision as a double';
      break;
    case 'decimal':
      text = randomNumberLiteral(rng, { maxIntDigits: rng.int(1, 5), fracChance: 1, maxFracDigits: rng.int(10, 25), expChance: 0.1 });
      note = 'long decimal expansion';
      break;
    case 'exponent':
      text = randomNumberLiteral(rng, { expChance: 1, maxExpDigits: rng.int(1, 3) });
      note = 'random exponent-notation literal';
      break;
    case 'tie-spelling':
    default: {
      const key = rng.choice(TIE_KEYS);
      text = rng.choice(TIE_SPELLINGS[key]);
      note = `alternate spelling of ${key} (a tie in shortest-round-trip representation)`;
      break;
    }
  }
  return { node: Obj([['n', Num(text)]]), note: `numbers/${shape}: ${note}` };
}

// ---------------------------------------------------------------------------
// Category: escaping.
// ---------------------------------------------------------------------------

const HIGH_RANGE_POOL = ['é', '中', '∑', '😀', ' ', ' ']; // é 中 ∑ 😀 LS PS

function genEscapingDoc(rng) {
  const theme = rng.choice([
    'c0-controls', 'del', 'quote-backslash', 'slash', 'literal-highrange', 'u-equivalents', 'line-paragraph-sep',
  ]);
  switch (theme) {
    case 'c0-controls': {
      const pieces = [];
      for (let cp = 0x00; cp <= 0x1f; cp++) {
        pieces.push({ mode: NAMED_CONTROL_CODEPOINTS.has(cp) && rng.chance(0.5) ? 'named' : 'u', cp });
      }
      return { node: Obj([['controls', rawStr(pieces)]]), note: 'every C0 control (U+0000-U+001F), named-escape and \\u forms mixed' };
    }
    case 'del':
      return {
        node: Obj([
          ['lit', S('ab')],
          ['esc', S('ab', { forceU: new Set([0x7f]) })],
        ]),
        note: 'U+007F DEL: RFC 8785 requires it literal; \\u escaped form must canonicalize identically',
      };
    case 'quote-backslash': {
      const text = 'a"b\\c';
      return {
        node: Obj([
          ['auto', S(text)],
          ['uspelled', S(text, { forceU: new Set([0x22, 0x5c]) })],
        ]),
        note: '" and \\ via mandatory named escape vs. equivalent \\u0022/\\u005C spelling',
      };
    }
    case 'slash': {
      const text = 'path/to/thing';
      return {
        node: Obj([
          ['lit', S(text)],
          ['esc', rawStr(piecesForcingSlash(text))],
        ]),
        note: "'/' unescaped (must stay that way in canonical output) vs. input spelled with the legal \\/ escape",
      };
    }
    case 'literal-highrange': {
      const n = rng.int(2, 4);
      const chars = rng.shuffle(HIGH_RANGE_POOL).slice(0, n);
      return { node: Obj([['s', S(chars.join(''))]]), note: 'characters that must NOT be escaped, written literally' };
    }
    case 'u-equivalents': {
      const ch = rng.choice(HIGH_RANGE_POOL);
      const cps = [...ch].map((c) => c.codePointAt(0));
      return {
        node: Obj([
          ['lit', S(ch)],
          ['esc', S(ch, { forceU: new Set(cps) })],
        ]),
        note: '\\uXXXX escape(s) (surrogate pair, if astral) vs. the literal character — must canonicalize identically',
      };
    }
    case 'line-paragraph-sep':
    default: {
      const text = 'a b c';
      return {
        node: Obj([
          ['lit', S(text)],
          ['esc', S(text, { forceU: new Set([0x2028, 0x2029]) })],
        ]),
        note: 'U+2028/U+2029 — legal unescaped in a JSON string, historically special in JS source text',
      };
    }
  }
}

// ---------------------------------------------------------------------------
// Category: admit-accept-boundary — right at the caps, must be admitted.
// Category: admit-reject-boundary — one past the caps (or a duplicate key),
// must be rejected. Both share the structural builders above; only the
// magnitude (and, for duplicates, the key-collision shape) differs.
// ---------------------------------------------------------------------------

const ADMIT_MAX_DEPTH = 32;
const ADMIT_MAX_MEMBERS = 1024;
const ADMIT_MAX_STRING_BYTES = 262144;

function genAcceptBoundaryDoc(rng) {
  const kind = rng.choice(['depth', 'members', 'string-1byte', 'string-2byte']);
  switch (kind) {
    case 'depth':
      return { node: buildNested(rng, ADMIT_MAX_DEPTH), note: `depth exactly ${ADMIT_MAX_DEPTH} (the cap) — must be admitted` };
    case 'members':
      return { node: buildMembers(ADMIT_MAX_MEMBERS), note: `${ADMIT_MAX_MEMBERS} members (the cap) — must be admitted` };
    case 'string-1byte':
      return { node: buildStringOfBytes(ADMIT_MAX_STRING_BYTES, 1), note: `${ADMIT_MAX_STRING_BYTES}-byte string, 1-byte filler (the cap) — must be admitted` };
    case 'string-2byte':
    default:
      return { node: buildStringOfBytes(ADMIT_MAX_STRING_BYTES, 2), note: `${ADMIT_MAX_STRING_BYTES}-byte string, 2-byte UTF-8 filler (the cap) — must be admitted` };
  }
}

// Two keys that decompose/recompose to the same NFC form but are byte-distinct on the
// wire — admit's duplicate check compares wire names (never normalized), so these must be
// accepted as two distinct members by 'admit'; canonicalize_nfc then normalizes both keys
// to the same string, which the two implementations may or may not handle identically.
const NFC_COLLISION_PAIRS = [
  { a: 'café', b: 'café' },
  { a: 'Å', b: 'Å' },
  { a: 'önes', b: 'önes' },
  { a: SINGLETON_OHM, b: 'Ω' },
];

function genRejectBoundaryDoc(rng) {
  const kind = rng.choice(['depth', 'members', 'string-1byte', 'string-2byte', 'dup-identical', 'dup-nfc']);
  switch (kind) {
    case 'depth':
      return { node: buildNested(rng, ADMIT_MAX_DEPTH + 1), note: `depth ${ADMIT_MAX_DEPTH + 1} exceeds the ${ADMIT_MAX_DEPTH}-cap — must be rejected` };
    case 'members':
      return { node: buildMembers(ADMIT_MAX_MEMBERS + 1), note: `${ADMIT_MAX_MEMBERS + 1} members exceeds the ${ADMIT_MAX_MEMBERS}-cap — must be rejected` };
    case 'string-1byte':
      return { node: buildStringOfBytes(ADMIT_MAX_STRING_BYTES + 1, 1), note: `${ADMIT_MAX_STRING_BYTES + 1}-byte string exceeds the cap — must be rejected` };
    case 'string-2byte':
      return { node: buildStringOfBytes(ADMIT_MAX_STRING_BYTES + 2, 2), note: `${ADMIT_MAX_STRING_BYTES + 2}-byte string (2-byte filler) exceeds the cap — must be rejected` };
    case 'dup-identical': {
      const key = rng.choice(['dup', 'name', 'kéy', SINGLETON_OHM]);
      return { node: Obj([[key, Num('1')], [key, Num('2')]]), note: `byte-identical duplicate key ${JSON.stringify(key)}` };
    }
    case 'dup-nfc':
    default: {
      const pair = rng.choice(NFC_COLLISION_PAIRS);
      return { node: Obj([[pair.a, Num('1')], [pair.b, Num('2')]]), note: `keys collide only after NFC: ${JSON.stringify(pair.a)} vs ${JSON.stringify(pair.b)}` };
    }
  }
}

// ---------------------------------------------------------------------------
// Malformed byte-level generators. These build Buffers directly rather than
// going through the JSON-tree/stringify path above, because their entire
// point is to be something that path cannot produce (invalid UTF-8, a raw
// NUL byte, a truncated or bracket-unbalanced document).
// ---------------------------------------------------------------------------

// A cross-section of ways a byte sequence can fail RFC 3629: an invalid lead byte, an
// overlong encoding, a truncated multi-byte sequence, a stray continuation byte with no
// leader, and a WTF-8-encoded lone surrogate (surrogate code points are excluded from
// valid UTF-8 even though the 3-byte encoding shape is otherwise well-formed).
const INVALID_UTF8_FRAGMENTS = [
  Buffer.from([0xff]),
  Buffer.from([0xfe]),
  Buffer.from([0xc0, 0x80]), // overlong NUL
  Buffer.from([0xc1, 0xbf]), // overlong
  Buffer.from([0xe0, 0x80, 0x80]), // overlong
  Buffer.from([0xf0, 0x80, 0x80, 0x80]), // overlong
  Buffer.from([0xf5, 0x80, 0x80, 0x80]), // lead byte > U+10FFFF's range
  Buffer.from([0xf8, 0x88, 0x80, 0x80, 0x80]), // obsolete 5-byte form
  Buffer.from([0xed, 0xa0, 0x80]), // WTF-8 lone high surrogate U+D800
  Buffer.from([0xed, 0xb0, 0x80]), // WTF-8 lone low surrogate U+DC00
  Buffer.from([0xc2]), // truncated 2-byte sequence
  Buffer.from([0xe2, 0x82]), // truncated 3-byte sequence
  Buffer.from([0xf0, 0x9f, 0x98]), // truncated 4-byte sequence
  Buffer.from([0x80]), // stray continuation byte
  Buffer.from([0xbf, 0xbf]), // stray continuation bytes
];

function genMalformedUtf8(rng) {
  const frag = rng.choice(INVALID_UTF8_FRAGMENTS);
  const bytes = Buffer.concat([Buffer.from('{"a":"x', 'utf8'), frag, Buffer.from('y"}', 'utf8')]);
  const hex = [...frag].map((b) => '0x' + b.toString(16).padStart(2, '0')).join(' ');
  return { bytes, note: `invalid UTF-8 fragment [${hex}] spliced into a string literal` };
}

// Lone/unpaired \u surrogate escapes — every piece here is plain ASCII (the six
// characters '\','u', and four hex digits), so this is built through the safe
// stringify/toBytes path via rawStr, not as raw bytes.
const SURROGATE_TEMPLATES = [
  () => [uEscape('D800')],
  () => [{ mode: 'lit', cp: 0x78 }, uEscape('D800'), { mode: 'lit', cp: 0x79 }],
  () => [uEscape('DC00')],
  () => [uEscape('DC00'), uEscape('D800')], // reversed order: low, then high — still unpaired
  () => [uEscape('D83D')], // high half of an astral pair, missing its low half
  () => [uEscape('D800'), uEscape('D800')], // two lone highs in a row never combine
];

function genMalformedSurrogate(rng) {
  const pieces = rng.choice(SURROGATE_TEMPLATES)();
  return { node: Obj([['a', rawStr(pieces)]]), note: 'lone/unpaired \\u surrogate escape (R6.15)' };
}

function genMalformedNul(rng) {
  const placement = rng.choice(['in-string', 'after-open-brace', 'buffer-start', 'buffer-end', 'double-in-string']);
  const NUL = Buffer.from([0x00]);
  let bytes;
  switch (placement) {
    case 'in-string':
      bytes = Buffer.concat([Buffer.from('{"a":"x', 'utf8'), NUL, Buffer.from('y"}', 'utf8')]);
      break;
    case 'after-open-brace':
      bytes = Buffer.concat([Buffer.from('{', 'utf8'), NUL, Buffer.from('"a":1}', 'utf8')]);
      break;
    case 'buffer-start':
      bytes = Buffer.concat([NUL, Buffer.from('{"a":1}', 'utf8')]);
      break;
    case 'buffer-end':
      bytes = Buffer.concat([Buffer.from('{"a":1}', 'utf8'), NUL]);
      break;
    case 'double-in-string':
    default:
      bytes = Buffer.concat([Buffer.from('{"a":"', 'utf8'), NUL, NUL, Buffer.from('"}', 'utf8')]);
      break;
  }
  return { bytes, note: `raw NUL byte, placement=${placement}` };
}

function genMalformedTruncation(rng) {
  const full = toBytes(buildAsciiSkeleton(rng));
  const cut = rng.int(1, Math.max(1, full.length - 1));
  return { bytes: Buffer.from(full.subarray(0, cut)), note: `truncated to ${cut}/${full.length} bytes` };
}

function genMalformedUnbalanced(rng) {
  const base = toBytes(buildAsciiSkeleton(rng));
  const bracketIdx = [];
  for (let i = 0; i < base.length; i++) {
    const b = base[i];
    if (b === 0x7b || b === 0x7d || b === 0x5b || b === 0x5d) bracketIdx.push(i);
  }
  const openIdx = bracketIdx.filter((i) => base[i] === 0x7b || base[i] === 0x5b);
  const closeIdx = bracketIdx.filter((i) => base[i] === 0x7d || base[i] === 0x5d);
  const mode = rng.choice(['drop-trailing', 'append-extra', 'flip-bracket', 'remove-open', 'duplicate-close']);
  let bytes;
  switch (mode) {
    case 'drop-trailing': {
      const k = rng.int(1, Math.min(3, closeIdx.length || 1));
      bytes = Buffer.from(base.subarray(0, base.length - k));
      break;
    }
    case 'append-extra':
      bytes = Buffer.concat([base, Buffer.from(rng.chance(0.5) ? '}' : ']', 'utf8')]);
      break;
    case 'flip-bracket': {
      const idx = rng.choice(bracketIdx);
      const flip = { 0x7b: 0x5b, 0x5b: 0x7b, 0x7d: 0x5d, 0x5d: 0x7d }[base[idx]];
      bytes = Buffer.from(base);
      bytes[idx] = flip;
      break;
    }
    case 'remove-open': {
      const idx = rng.choice(openIdx.length ? openIdx : bracketIdx);
      bytes = Buffer.concat([base.subarray(0, idx), base.subarray(idx + 1)]);
      break;
    }
    case 'duplicate-close':
    default: {
      const idx = rng.choice(closeIdx.length ? closeIdx : bracketIdx);
      bytes = Buffer.concat([base.subarray(0, idx + 1), base.subarray(idx, idx + 1), base.subarray(idx + 1)]);
      break;
    }
  }
  return { bytes, note: `structurally unbalanced via ${mode}` };
}

// ---------------------------------------------------------------------------
// Category table, corpus planning, and the CLI/main loop.
// ---------------------------------------------------------------------------

const OPS = ['admit', 'canonicalize', 'canonicalize_nfc'];

// Weights are chosen so the two buckets sum equal (45/45 of 90) — "roughly half
// well-formed, half adversarial" per the brief, made exact rather than approximate.
const CATEGORIES = [
  { name: 'key-ordering', bucket: 'benign', weight: 9, gen: genKeyOrderingDoc },
  { name: 'unicode-nfd', bucket: 'benign', weight: 9, gen: genUnicodeDoc },
  { name: 'numbers', bucket: 'benign', weight: 10, gen: genNumberDoc },
  { name: 'escaping', bucket: 'benign', weight: 10, gen: genEscapingDoc },
  { name: 'admit-accept-boundary', bucket: 'benign', weight: 7, gen: genAcceptBoundaryDoc },
  { name: 'admit-reject-boundary', bucket: 'adversarial', weight: 9, gen: genRejectBoundaryDoc },
  { name: 'malformed-utf8', bucket: 'adversarial', weight: 8, gen: genMalformedUtf8 },
  { name: 'malformed-surrogate', bucket: 'adversarial', weight: 8, gen: genMalformedSurrogate },
  { name: 'malformed-nul', bucket: 'adversarial', weight: 7, gen: genMalformedNul },
  { name: 'malformed-truncation', bucket: 'adversarial', weight: 8, gen: genMalformedTruncation },
  { name: 'malformed-unbalanced', bucket: 'adversarial', weight: 5, gen: genMalformedUnbalanced },
];
const TOTAL_WEIGHT = CATEGORIES.reduce((s, c) => s + c.weight, 0);

function materialize(result) {
  if (result.bytes) return result;
  return { bytes: toBytes(result.node), note: result.note };
}

/** Largest-remainder apportionment of `totalDocs` across CATEGORIES by weight — exact
 * (sums to totalDocs), not just approximately proportional. */
function planCounts(totalDocs) {
  const raw = CATEGORIES.map((c) => (c.weight / TOTAL_WEIGHT) * totalDocs);
  const base = raw.map(Math.floor);
  const used = base.reduce((a, b) => a + b, 0);
  const remainder = totalDocs - used;
  const order = raw
    .map((v, i) => ({ i, frac: v - base[i] }))
    .sort((a, b) => b.frac - a.frac);
  for (let k = 0; k < remainder; k++) base[order[k].i]++;
  return base;
}

/** Round-robin schedule of category indices, deterministic from `counts` alone (no rng
 * consumed) — the rng is spent entirely inside the generator calls, in category order. */
function buildSchedule(counts) {
  const schedule = [];
  const remaining = counts.slice();
  let anyLeft = true;
  while (anyLeft) {
    anyLeft = false;
    for (let i = 0; i < remaining.length; i++) {
      if (remaining[i] > 0) {
        schedule.push(i);
        remaining[i]--;
        anyLeft = true;
      }
    }
  }
  return schedule;
}

function parseSeed(s) {
  if (s === undefined) throw new Error('--seed requires a value');
  return BigInt(s); // accepts decimal or 0x-prefixed hex
}

function parseArgs(argv) {
  let seed = 1n;
  let count = 300;
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--seed') seed = parseSeed(argv[++i]);
    else if (a.startsWith('--seed=')) seed = parseSeed(a.slice('--seed='.length));
    else if (a === '--count') count = Number(argv[++i]);
    else if (a.startsWith('--count=')) count = Number(a.slice('--count='.length));
    else if (a === '-h' || a === '--help') { printHelp(); process.exit(0); }
    else { process.stderr.write(`generate.mjs: unrecognized argument '${a}'\n`); process.exit(2); }
  }
  if (!Number.isInteger(count) || count < 1) {
    process.stderr.write(`generate.mjs: --count must be a positive integer, got '${count}'\n`);
    process.exit(2);
  }
  return { seed, count };
}

function printHelp() {
  process.stderr.write(`usage: node generate.mjs [--seed <int|0xHEX>] [--count <n>]

Writes NDJSON to stdout: for each of <n> generated documents, three lines
(op=admit, op=canonicalize, op=canonicalize_nfc), per the differential-oracle
wire protocol. Deterministic for a given --seed. A distribution summary is
written to stderr.

  --seed   PRNG seed (splitmix64). Default: 1.
  --count  Number of documents to generate (3x this many output lines). Default: 300.
`);
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const rng = new SplitMix64(args.seed);
  const counts = planCounts(args.count);
  const schedule = buildSchedule(counts);

  const emitted = new Array(CATEGORIES.length).fill(0);
  let totalLines = 0;
  let docIndex = 0;

  for (const catIdx of schedule) {
    const cat = CATEGORIES[catIdx];
    const { bytes } = materialize(cat.gen(rng));
    const docId = `${String(docIndex).padStart(6, '0')}.${cat.name}`;
    const inputB64 = bytes.toString('base64');
    for (const op of OPS) {
      process.stdout.write(JSON.stringify({ id: `${docId}.${op}`, op, input_b64: inputB64 }) + '\n');
      totalLines++;
    }
    emitted[catIdx]++;
    docIndex++;
  }

  const benignDocs = CATEGORIES.reduce((s, c, i) => (c.bucket === 'benign' ? s + emitted[i] : s), 0);
  const adversarialDocs = docIndex - benignDocs;

  const lines = [];
  lines.push(`generate.mjs: seed=${args.seed} count=${args.count} documents=${docIndex} lines=${totalLines}`);
  lines.push(`  benign=${benignDocs} (${((100 * benignDocs) / docIndex).toFixed(1)}%)  adversarial=${adversarialDocs} (${((100 * adversarialDocs) / docIndex).toFixed(1)}%)`);
  lines.push('  by category:');
  for (let i = 0; i < CATEGORIES.length; i++) {
    lines.push(`    ${CATEGORIES[i].name.padEnd(24)} ${String(emitted[i]).padStart(6)} docs  [${CATEGORIES[i].bucket}]`);
  }
  process.stderr.write(lines.join('\n') + '\n');
}

main();
