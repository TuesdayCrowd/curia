#!/usr/bin/env node
// oracle.mjs — the node endpoint of the RFC 8785 differential harness (Plan 2,
// Task 7 / design spec §9, R14.6).
//
// This is a from-scratch RFC 8785 implementation, written directly from the RFC
// text and independent of both the C# implementation (`Curia.Canon`) and the Rust
// implementation (`curia-testis`). It exists to catch the case a two-implementation
// diff cannot: both real implementations agreeing because they made the same
// mistake. Node earns that role on two specific, load-bearing facts, not on general
// trustworthiness:
//
//   - `Array.prototype.sort()` on strings compares by UTF-16 code unit — exactly
//     RFC 8785 §3.2.3's member-name ordering rule. No custom comparator needed;
//     the wrong thing to do here would be to write one.
//   - `String(x)` for a `number` is ECMA-262's `Number::toString`, which RFC 8785
//     §3.2.2.3 references normatively. Calling it directly *is* implementing the
//     spec, not approximating it.
//
// Everything else here (the JSON tokenizer, string escaping, structural
// recursion) is ordinary hand-written code with no special claim to authority —
// it is exercised against the RFC author's own six vendored vectors in
// conformance/rfc8785/ before this file is trusted for anything else. See
// tools/differential-oracle/validate.mjs.
//
// Wire protocol (stdin/stdout, NDJSON, one line out per line in):
//   in:  {"id":"<string>","op":"admit"|"canonicalize"|"canonicalize_nfc","input_b64":"<base64 of raw input bytes>"}
//   out: {"id":"<same id>","ok":true,"out_b64":"<base64 of raw output bytes>"}
//     or {"id":"<same id>","ok":false,"slug":"<error slug>"}
//
// This endpoint never fetches, writes, or shells out to anything; it reads
// stdin and writes stdout. Diagnostics (nothing in the success/failure path)
// go to stderr only.

import { createInterface } from 'node:readline';

// ---------------------------------------------------------------------------
// Node tree. Object members are [key, value] pairs in an array, deliberately
// never a plain JS object. A plain object silently reorders integer-looking
// string keys ("0", "1", "10", ...) to ascending numeric order ahead of
// insertion order for enumeration (Object.keys / for-in / JSON.stringify) —
// which is not RFC 8785 §3.2.3's UTF-16-code-unit order (e.g. "10" sorts
// before "2" there, not after). Pair-arrays make every ordering decision in
// this file the one explicit sort below, and nothing implicit.
// ---------------------------------------------------------------------------

/** @typedef {{t:'null'}|{t:'bool',v:boolean}|{t:'num',v:number}|{t:'str',v:string}|{t:'arr',v:Node[]}|{t:'obj',v:[string,Node][]}} Node */

const N_null = () => ({ t: 'null' });
const N_bool = (v) => ({ t: 'bool', v });
const N_num = (v) => ({ t: 'num', v });
const N_str = (v) => ({ t: 'str', v });
const N_arr = (v) => ({ t: 'arr', v });
const N_obj = (v) => ({ t: 'obj', v });

// ---------------------------------------------------------------------------
// Hand-rolled JSON parser (JSON.parse is deliberately not used here).
//
// JSON.parse has two behaviors that are wrong for this purpose:
//   1. It silently collapses duplicate object member names, keeping only the
//      last. JCS (via I-JSON, RFC 7493 §2.3) assumes unique member names;
//      silently resolving a violation hides exactly the kind of input a
//      differential fuzzer exists to surface. This parser rejects it instead.
//   2. Its result is a plain JS object, which reintroduces the integer-key
//      reordering problem the pair-array Node type exists to avoid — an
//      object literal built via property assignment already carries the
//      "wrong" enumeration order by the time any code sees it.
//
// Operates on a JS string (UTF-16 code units), produced by a strict UTF-8
// decode of the raw input bytes upstream — see decodeUtf8Strict. Indexing a
// JS string by code unit is exactly right here: JSON's \uXXXX escapes are
// themselves per-UTF-16-unit (an astral character is written as a surrogate
// pair of two escapes), and a literal astral character embedded directly
// already occupies two code units in the string, copied through unchanged.
// ---------------------------------------------------------------------------

class JsonSyntaxError extends Error {}

function parseJsonText(text) {
  let i = 0;
  const n = text.length;

  function fail(msg) {
    throw new JsonSyntaxError(`${msg} (at UTF-16 offset ${i})`);
  }

  function skipWs() {
    while (i < n) {
      const c = text[i];
      if (c === ' ' || c === '\t' || c === '\n' || c === '\r') i++;
      else break;
    }
  }

  function parseValue() {
    skipWs();
    if (i >= n) fail('unexpected end of input');
    const c = text[i];
    if (c === '{') return parseObject();
    if (c === '[') return parseArray();
    if (c === '"') return N_str(parseStringLiteral());
    if (c === '-' || (c >= '0' && c <= '9')) return parseNumber();
    if (text.startsWith('true', i)) { i += 4; return N_bool(true); }
    if (text.startsWith('false', i)) { i += 5; return N_bool(false); }
    if (text.startsWith('null', i)) { i += 4; return N_null(); }
    fail(`unexpected character ${JSON.stringify(c)}`);
  }

  function parseObject() {
    i++; // '{'
    const pairs = [];
    const seen = new Set();
    skipWs();
    if (text[i] === '}') { i++; return N_obj(pairs); }
    for (;;) {
      skipWs();
      if (text[i] !== '"') fail('expected string object key');
      const key = parseStringLiteral();
      if (seen.has(key)) fail(`duplicate object member name ${JSON.stringify(key)}`);
      seen.add(key);
      skipWs();
      if (text[i] !== ':') fail("expected ':' after object key");
      i++;
      pairs.push([key, parseValue()]);
      skipWs();
      if (text[i] === ',') { i++; continue; }
      if (text[i] === '}') { i++; break; }
      fail("expected ',' or '}' in object");
    }
    return N_obj(pairs);
  }

  function parseArray() {
    i++; // '['
    const items = [];
    skipWs();
    if (text[i] === ']') { i++; return N_arr(items); }
    for (;;) {
      items.push(parseValue());
      skipWs();
      if (text[i] === ',') { i++; continue; }
      if (text[i] === ']') { i++; break; }
      fail("expected ',' or ']' in array");
    }
    return N_arr(items);
  }

  function parseStringLiteral() {
    i++; // opening '"'
    let out = '';
    for (;;) {
      if (i >= n) fail('unterminated string literal');
      const c = text[i];
      if (c === '"') { i++; break; }
      if (c === '\\') {
        i++;
        if (i >= n) fail('unterminated escape sequence');
        const e = text[i];
        if (e === '"') { out += '"'; i++; }
        else if (e === '\\') { out += '\\'; i++; }
        else if (e === '/') { out += '/'; i++; }
        else if (e === 'b') { out += '\b'; i++; }
        else if (e === 'f') { out += '\f'; i++; }
        else if (e === 'n') { out += '\n'; i++; }
        else if (e === 'r') { out += '\r'; i++; }
        else if (e === 't') { out += '\t'; i++; }
        else if (e === 'u') {
          i++;
          const hex = text.slice(i, i + 4);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) fail('invalid \\u escape');
          out += String.fromCharCode(parseInt(hex, 16));
          i += 4;
        } else {
          fail(`invalid escape '\\${e}'`);
        }
        continue;
      }
      const code = c.charCodeAt(0);
      if (code < 0x20) fail('unescaped control character in string literal');
      out += c;
      i++;
    }
    return out;
  }

  function parseNumber() {
    const start = i;
    if (text[i] === '-') i++;
    if (text[i] === '0') {
      i++;
    } else if (text[i] >= '1' && text[i] <= '9') {
      i++;
      while (text[i] >= '0' && text[i] <= '9') i++;
    } else {
      fail('invalid number literal');
    }
    if (text[i] === '.') {
      i++;
      if (!(text[i] >= '0' && text[i] <= '9')) fail('invalid number: digit required after decimal point');
      while (text[i] >= '0' && text[i] <= '9') i++;
    }
    if (text[i] === 'e' || text[i] === 'E') {
      i++;
      if (text[i] === '+' || text[i] === '-') i++;
      if (!(text[i] >= '0' && text[i] <= '9')) fail('invalid number: digit required in exponent');
      while (text[i] >= '0' && text[i] <= '9') i++;
    }
    const raw = text.slice(start, i);
    const num = Number(raw);
    if (!Number.isFinite(num)) fail('number literal is not finite as a JS double');
    return N_num(num);
  }

  const value = parseValue();
  skipWs();
  if (i !== n) fail('trailing content after top-level JSON value');
  return value;
}

// ---------------------------------------------------------------------------
// RFC 8785 §3.2.2.2: escape '"' and '\\'; control characters below U+0020
// using the named short forms ECMA-404/JSON defines, else lowercase \u00XX;
// every other character (including '/', U+007F DEL, and everything above
// U+007F) is emitted literally, encoded as UTF-8 at the Buffer.from step.
// ---------------------------------------------------------------------------

const SHORT_ESCAPES = {
  0x08: '\\b',
  0x0c: '\\f',
  0x0a: '\\n',
  0x0d: '\\r',
  0x09: '\\t',
};

function escapeString(s) {
  let out = '"';
  for (const ch of s) { // by Unicode code point (handles astral chars as one unit)
    const cp = ch.codePointAt(0);
    if (ch === '"') out += '\\"';
    else if (ch === '\\') out += '\\\\';
    else if (cp in SHORT_ESCAPES) out += SHORT_ESCAPES[cp];
    else if (cp < 0x20) out += '\\u' + cp.toString(16).padStart(4, '0');
    else out += ch;
  }
  out += '"';
  return out;
}

// RFC 8785 §3.2.2.3, by reference to ECMA-262 Number::toString. String(x) on a
// JS `number` *is* that algorithm — used directly, not approximated.
function formatNumber(x) {
  if (Object.is(x, -0)) return '0'; // RFC 8785 requires -0 to render as "0"
  if (!Number.isFinite(x)) throw new Error('formatNumber: non-finite (unreachable for parsed JSON)');
  return String(x);
}

// RFC 8785 §3.2.3: sort object members by the UTF-16 code unit sequence of
// the member name. JS's default `<`/`>` on strings already compares by UTF-16
// code unit (JS strings *are* UTF-16 sequences) — the one place where "use
// the language's native string comparison" is correct instead of a trap.
// Array.prototype.sort has been stable since ES2019.
function cmpUtf16(a, b) {
  return a < b ? -1 : a > b ? 1 : 0;
}

function canonicalize(node) {
  switch (node.t) {
    case 'null': return 'null';
    case 'bool': return node.v ? 'true' : 'false';
    case 'num': return formatNumber(node.v);
    case 'str': return escapeString(node.v);
    case 'arr': return '[' + node.v.map(canonicalize).join(',') + ']';
    case 'obj': {
      const sorted = node.v.slice().sort((a, b) => cmpUtf16(a[0], b[0]));
      return '{' + sorted.map(([k, v]) => escapeString(k) + ':' + canonicalize(v)).join(',') + '}';
    }
    default: throw new Error('unknown node type ' + node.t);
  }
}

function canonicalizeBytes(node) {
  return Buffer.from(canonicalize(node), 'utf8');
}

// ---------------------------------------------------------------------------
// The Cūria NFC profile (canonicalize_nfc / meta.json profile
// "canonicalize-with-nfc"): NFC-normalize every object member name and every
// string value, recursively, THEN canonicalize. Applied as a tree rewrite
// before canonicalize() runs, so canonicalize() itself stays pure RFC 8785
// with no normalization branch anywhere in it — the two functions the
// conformance README insists must stay separate, staying separate here too.
// ---------------------------------------------------------------------------

function applyNfc(node) {
  switch (node.t) {
    case 'null':
    case 'bool':
    case 'num':
      return node;
    case 'str':
      return N_str(node.v.normalize('NFC'));
    case 'arr':
      return N_arr(node.v.map(applyNfc));
    case 'obj':
      return N_obj(node.v.map(([k, v]) => [k.normalize('NFC'), applyNfc(v)]));
    default:
      throw new Error('unknown node type ' + node.t);
  }
}

// ---------------------------------------------------------------------------
// Strict UTF-8 decode. A JSON document's wire bytes MUST be valid UTF-8;
// TextDecoder with fatal:true throws instead of silently substituting
// U+FFFD, which would corrupt exactly the malformed-input corpus a
// differential fuzzer cares most about.
// ---------------------------------------------------------------------------

const utf8Decoder = new TextDecoder('utf-8', { fatal: true });

function decodeUtf8Strict(bytes) {
  return utf8Decoder.decode(bytes);
}

// ---------------------------------------------------------------------------
// Exported for tools/differential-oracle/validate.mjs.
// ---------------------------------------------------------------------------

export {
  N_null, N_bool, N_num, N_str, N_arr, N_obj,
  parseJsonText, JsonSyntaxError,
  canonicalize, canonicalizeBytes, applyNfc, decodeUtf8Strict,
};

// ---------------------------------------------------------------------------
// Wire protocol runner. Only runs when this file is executed directly (not
// when imported by validate.mjs).
// ---------------------------------------------------------------------------

function runCanonicalizeOp(inputBytes, nfc) {
  const text = decodeUtf8Strict(inputBytes); // throws on invalid UTF-8
  let tree = parseJsonText(text); // throws JsonSyntaxError on malformed JSON
  if (nfc) tree = applyNfc(tree);
  return canonicalizeBytes(tree);
}

function handleRequest(req) {
  const { id, op, input_b64 } = req;
  if (op === 'admit') {
    // The oracle does not model ADMIT; see module header.
    return { id, ok: false, slug: 'oracle/unsupported' };
  }
  if (op !== 'canonicalize' && op !== 'canonicalize_nfc') {
    return { id, ok: false, slug: `oracle/unknown-op: ${String(op)}` };
  }
  const inputBytes = Buffer.from(input_b64, 'base64');
  try {
    const outBytes = runCanonicalizeOp(inputBytes, op === 'canonicalize_nfc');
    return { id, ok: true, out_b64: outBytes.toString('base64') };
  } catch (e) {
    if (e instanceof JsonSyntaxError) {
      return { id, ok: false, slug: `oracle/json-syntax: ${e.message}` };
    }
    if (e instanceof TypeError && /utf-8/i.test(e.message)) {
      return { id, ok: false, slug: `oracle/invalid-utf8: ${e.message}` };
    }
    // TextDecoder's fatal mode throws TypeError on invalid UTF-8 in Node;
    // the check above catches that by message. Anything else, including a
    // genuinely unexpected exception, is a crash finding per the wire
    // protocol contract, not silently swallowed.
    return { id, ok: false, slug: `oracle/CRASH: ${e && e.message ? e.message : String(e)}` };
  }
}

async function main() {
  const rl = createInterface({ input: process.stdin, terminal: false, crlfDelay: Infinity });
  for await (const line of rl) {
    const trimmed = line.trim();
    if (trimmed === '') continue; // not an input record (trailing NDJSON newline etc.)

    let req;
    try {
      req = JSON.parse(trimmed);
    } catch (e) {
      // The request line itself is malformed — a harness bug, not a test
      // input. Emit a best-effort failure line rather than dropping output
      // (which would desync line counts) or crashing the endpoint.
      process.stdout.write(JSON.stringify({ id: null, ok: false, slug: `oracle/CRASH: malformed request line: ${e.message}` }) + '\n');
      continue;
    }

    let out;
    try {
      out = handleRequest(req);
    } catch (e) {
      out = { id: req && req.id !== undefined ? req.id : null, ok: false, slug: `oracle/CRASH: ${e && e.message ? e.message : String(e)}` };
    }
    process.stdout.write(JSON.stringify(out) + '\n');
  }
}

// Only run the stdin/stdout loop when invoked as a script.
if (import.meta.url === `file://${process.argv[1]}`) {
  main().catch((e) => {
    // Last-resort guard: even a failure in the read loop itself must not
    // produce a silent hang or an unhandled rejection with no stdout output.
    process.stderr.write(`oracle.mjs: fatal: ${e && e.stack ? e.stack : e}\n`);
    process.exit(1);
  });
}
