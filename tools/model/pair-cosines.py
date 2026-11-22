#!/usr/bin/env python3
"""The reference measurement for D10b's pairs and queries (plan: docs/superpowers/plans/2026-11-22-the-numbers-are-re-derived.md, NDT4).

Measurements only. It writes no Derivation/Check label, no threshold and no verdict: those are the rule's
(conformance/retrieval/THRESHOLD-RULE.md) and the runner's (tests/Curia.Embedding.Tests/RetrievalRule.cs).

It reads conformance/retrieval/dedupe-pairs.jsonl (16 legacy pairs), blind-pairs.jsonl (90 pairs and 24 queries) and corpus.jsonl
(10 posts), and writes one JSON line per measurement to --out (default: stdout):

  {"kind": "pair",  "id": ..., "source": "legacy"|"blind", "passage": <cosine of a against b, both in the passage role>,
                                                            "query_passage": <cosine of b in the query role against a in the passage role>}
  {"kind": "noise", "id": ..., "score": <the query's greatest cosine, query role, to any post of the post set>}

A text is joined as title, newline, body (a corpus post with a null title is its body alone, as BehaviourTests reads it); an off-topic
query is its title, newline, body and a made-up query is its `text`. The post set is every `a` and `b` of every blind pair and the ten
corpus posts, in the passage role. Floats are written with repr (full precision). With --out it prints counts only.

Independence: this file shares no code with the C# adapter and imports nothing from reference.py. The tokenizer is the Hugging Face
FAST tokenizer, the model is the deployed model.onnx under Python onnxruntime; the query-role prefix, the first 512 tokens, the [CLS]
row and L2 normalisation are restated here from reference.py (a restatement is a copy, and a shared import would collapse the two).

Run by hand, never in CI. Requirements: Python 3.12 (Homebrew's default python3 has no onnxruntime wheel) in a uv venv, and a snapshot
of BAAI/bge-small-en-v1.5 at the manifest's revision (tokenizer.json, tokenizer_config.json, vocab.txt, special_tokens_map.json,
config.json; vocab.txt's sha256 equals the manifest's). Neither torch nor model.safetensors is needed.

    uv venv --python 3.12 venv
    uv pip install --python venv/bin/python transformers==5.22.2 tokenizers==0.23.3 onnxruntime==1.31.1 numpy==2.5.4
    venv/bin/python -I tools/model/pair-cosines.py --hf-dir <snapshot> --model-dir "$CURIA_EMBEDDING_MODEL_DIR" --out <file>

The packages match models/bge-small-en-v1.5/manifest.json's record of the reference run except Python's patch: that record names 3.12.16,
and a run on this machine may be 3.12.9. The version in use is written to stderr.
"""
import argparse
import hashlib
import json
import platform
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RETRIEVAL = ROOT / "conformance" / "retrieval"
MANIFEST = ROOT / "models" / "bge-small-en-v1.5" / "manifest.json"
PREFIX = "Represent this sentence for searching relevant passages: "


def sanitise(text):
    """A lone surrogate is U+FFFD before it is tokenized, as the Forum does."""
    return "".join(chr(0xFFFD) if 0xD800 <= ord(ch) <= 0xDFFF else ch for ch in text)


def lines(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def joined(side):
    return side["title"] + "\n" + side["body"]


def corpus_text(post):
    title = post.get("title")
    return (title + "\n" if isinstance(title, str) else "") + post["body"]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--hf-dir", required=True, help="a snapshot of BAAI/bge-small-en-v1.5's tokenizer files at the manifest's revision")
    parser.add_argument("--model-dir", required=True, help="the directory holding the deployed model.onnx and vocab.txt")
    parser.add_argument("--out", help="write the measurements here (default: stdout)")
    args = parser.parse_args()

    import numpy as np
    import onnxruntime as ort
    from transformers import AutoTokenizer

    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    want = manifest["files"]["vocab.txt"]["sha256"]
    for where in (Path(args.hf_dir) / "vocab.txt", Path(args.model_dir) / "vocab.txt"):
        got = hashlib.sha256(where.read_bytes()).hexdigest()
        if got != want:
            sys.exit(f"{where.name} under {where.parent.name} has sha256 {got}, the manifest's is {want}")
    print(f"python {platform.python_version()} (the record names 3.12.16)", file=sys.stderr)

    fast = AutoTokenizer.from_pretrained(args.hf_dir)
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(Path(args.model_dir) / "model.onnx"), options, providers=["CPUExecutionProvider"])

    cache = {}

    def vector(text, query):
        key = (query, text)
        if key not in cache:
            ids = fast((PREFIX + text) if query else text, truncation=True, max_length=512)["input_ids"]
            n = len(ids)
            out = session.run(None, {"input_ids": np.array([ids], dtype=np.int64), "attention_mask": np.ones((1, n), dtype=np.int64), "token_type_ids": np.zeros((1, n), dtype=np.int64)})[0]
            v = out[0, 0].astype(np.float64)
            cache[key] = v / np.linalg.norm(v)
        return cache[key]

    def cosine(a, b):
        return float(np.dot(a, b))

    legacy = lines(RETRIEVAL / "dedupe-pairs.jsonl")
    blind = lines(RETRIEVAL / "blind-pairs.jsonl")
    corpus = lines(RETRIEVAL / "corpus.jsonl")
    blind_pairs = [r for r in blind if "a" in r]
    queries = [r for r in blind if "a" not in r]

    rows = []
    for source, pairs in (("legacy", legacy), ("blind", blind_pairs)):
        for p in pairs:
            a, b = sanitise(joined(p["a"])), sanitise(joined(p["b"]))
            rows.append({"kind": "pair", "id": p["id"], "source": source,
                         "passage": cosine(vector(a, False), vector(b, False)),
                         "query_passage": cosine(vector(b, True), vector(a, False))})

    posts = [sanitise(joined(side)) for p in blind_pairs for side in (p["a"], p["b"])] + [sanitise(corpus_text(c)) for c in corpus]
    post_vectors = [vector(t, False) for t in posts]
    for q in queries:
        text = sanitise(joined(q) if "body" in q else q["text"])
        qv = vector(text, True)
        rows.append({"kind": "noise", "id": q["id"], "score": max(cosine(qv, pv) for pv in post_vectors)})

    # json.dumps writes a float as its repr: full precision
    text = "".join(json.dumps(r, ensure_ascii=True) + "\n" for r in rows)
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8")
        print(f"wrote {sum(r['kind'] == 'pair' for r in rows)} pairs ({len(legacy)} legacy, {len(blind_pairs)} blind) and {sum(r['kind'] == 'noise' for r in rows)} queries, over {len(posts)} posts")
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main()
