#!/usr/bin/env node
// validate.mjs — confirms oracle.mjs agrees byte-exact with the six vendored
// RFC 8785 vectors in conformance/rfc8785/ before the oracle is trusted for
// anything else. Run: node tools/differential-oracle/validate.mjs
//
// Exercises the oracle two ways for each vector:
//   1. Directly, via the exported parse/canonicalize functions (fast, precise
//      failure location).
//   2. Through the actual wire protocol (stdin/stdout NDJSON, base64), since
//      that is the interface the differential harness will actually drive —
//      a unit-level pass that the wire adapter breaks would be a false
//      confidence.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { parseJsonText, canonicalizeBytes } from './oracle.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '..', '..');
const RFC_DIR = path.join(REPO_ROOT, 'conformance', 'rfc8785');
const ORACLE = path.join(HERE, 'oracle.mjs');

const VECTORS = ['arrays', 'french', 'structures', 'unicode', 'values', 'weird'];

let allPass = true;

console.log('=== Direct (in-process) validation against the six RFC 8785 vectors ===\n');

for (const name of VECTORS) {
  const inputPath = path.join(RFC_DIR, `input-${name}.json`);
  const expectedPath = path.join(RFC_DIR, `output-${name}.json`);
  const inputBytes = fs.readFileSync(inputPath);
  const expectedBytes = fs.readFileSync(expectedPath);

  const text = inputBytes.toString('utf8'); // vendored vectors are known-valid UTF-8
  const tree = parseJsonText(text);
  const got = canonicalizeBytes(tree);

  const pass = Buffer.compare(got, expectedBytes) === 0;
  console.log(`${pass ? 'PASS' : 'FAIL'} rfc8785/${name}`);
  if (!pass) {
    allPass = false;
    console.log(`  expected: ${expectedBytes.toString('hex')}`);
    console.log(`  got:      ${got.toString('hex')}`);
  }
}

console.log('\n=== Wire-protocol validation (stdin/stdout NDJSON, base64) ===\n');

const ndjsonLines = VECTORS.map((name) => {
  const inputBytes = fs.readFileSync(path.join(RFC_DIR, `input-${name}.json`));
  return JSON.stringify({ id: name, op: 'canonicalize', input_b64: inputBytes.toString('base64') });
}).join('\n') + '\n';

const result = spawnSync(process.execPath, [ORACLE], { input: ndjsonLines, encoding: 'utf8' });
if (result.status !== 0) {
  console.log(`FAIL wire protocol: oracle.mjs exited with status ${result.status}`);
  console.log(`stderr: ${result.stderr}`);
  allPass = false;
} else {
  const outLines = result.stdout.split('\n').filter((l) => l.trim() !== '');
  if (outLines.length !== VECTORS.length) {
    console.log(`FAIL wire protocol: expected ${VECTORS.length} output lines, got ${outLines.length}`);
    allPass = false;
  }
  for (let idx = 0; idx < outLines.length; idx++) {
    const resp = JSON.parse(outLines[idx]);
    const name = resp.id;
    const expectedBytes = fs.readFileSync(path.join(RFC_DIR, `output-${name}.json`));
    if (!resp.ok) {
      console.log(`FAIL wire rfc8785/${name}: oracle returned ok:false slug=${resp.slug}`);
      allPass = false;
      continue;
    }
    const got = Buffer.from(resp.out_b64, 'base64');
    const pass = Buffer.compare(got, expectedBytes) === 0;
    console.log(`${pass ? 'PASS' : 'FAIL'} wire rfc8785/${name} (order preserved: ${idx === VECTORS.indexOf(name) ? 'yes' : 'NO'})`);
    if (!pass) {
      allPass = false;
      console.log(`  expected: ${expectedBytes.toString('hex')}`);
      console.log(`  got:      ${got.toString('hex')}`);
    }
  }
}

console.log(`\n${allPass ? 'ALL SIX VECTORS PASS, both directly and through the wire protocol.' : 'DISAGREEMENT WITH THE RFC AUTHOR\'S OWN DATA — the oracle is wrong, fix it before doing anything else.'}`);
process.exit(allPass ? 0 : 1);
