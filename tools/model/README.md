# tools/model/

- `fetch-model.py`: fetch the embedding model by digest. `python3 tools/model/fetch-model.py --out <dir>` downloads what `<dir>` lacks, checks every file against `models/bge-small-en-v1.5/manifest.json` and
  exits non-zero, naming the file and the digest, when one does not match; `--verify-only` checks a directory and downloads nothing. Standard library only. A file is written to a temporary name and renamed only
  when its sha256 equals the manifest's. The cache is `$XDG_CACHE_HOME/curia-model` or `~/.cache/curia-model` (`--no-cache` skips it), and the digest is checked on every use, the cache's included.
- `reference.py`: the Python reference that writes the manifest's canaries and `tokenizer-parity.jsonl`. It uses the Hugging Face fast tokenizer and Python `onnxruntime`, never the C# adapter, and checks
  PyTorch against `onnxruntime` to 1e-4 before it writes anything. Its requirements and how to run it are at the top of the file. Run it by hand; it is not part of CI.
