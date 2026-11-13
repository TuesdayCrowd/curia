# models/

The identity of the embedding model the Forum runs (errata G65, R9.63). **The model's weights are not in this repository**; what is here is what the Forum and its tools are held to.

- `bge-small-en-v1.5/manifest.json`: the identity `bge-small-en-v1.5@1` (384 dimensions, the query-role instruction, the 512-token head, `[CLS]` pooling, L2 normalisation), the sha256 and the size of
  `model.onnx` and `vocab.txt`, where each is fetched from (the repository's release asset first, then the pinned Hugging Face revision; both checked by the same digest), the reference tools'
  versions, and the **canaries**: texts whose input ids and vectors the Python reference produced. The manifest is embedded in `Curia.Infrastructure` and is the input of `tools/model/fetch-model.py`, so the
  digest the fetch checks and the digest the Forum checks are one string. A Forum whose files or session do not reproduce the canaries does not start.
- `bge-small-en-v1.5/tokenizer-parity.jsonl`: 200 corpus strings and 2,400 generated strings with the content ids of the Hugging Face fast tokenizer, written by `tools/model/reference.py`. The tokenizer in
  `Curia.Infrastructure` is written in this repository and `Curia.Embedding.Tests` holds it to this file.

**Licence.** The model is MIT by its model card. Training-data provenance was not reviewed. See `docs/third-party.md` for the packages and the native library's notices.

**To run the real model's tests or a Forum:** `python3 tools/model/fetch-model.py --out <dir>` and `export CURIA_EMBEDDING_MODEL_DIR=<dir>`. `Curia.Embedding.Tests` fails, naming the variable and this
command, when the directory is absent; it never skips. ONNX Runtime 1.31.0 ships no native library for Intel macOS (`osx-x64`): on that platform everything builds and every test runs except `Curia.Embedding.Tests`,
and a Forum with the real embedder cannot be run.

**To regenerate** the canaries and the parity file (by hand, never in CI; review the result as a diff): see the top of `tools/model/reference.py`. A change to the model's files, its prefix, its head, its
pooling or the tokenization rules is a new identity (`@2`) and a reindex.
