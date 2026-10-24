# D10 decision proposal: the Forum's semantic embedding model

> Status: a proposal for the owner, written 2026-10-24 for plan D10 (`IMPLEMENTATION_PLAN.md`, "Left to the owner"). Written from the model cards and the package pages; the model was not run and no number here was measured by this project. Nothing is built. Section 5 holds the six questions.

## Problem
The vector channel runs `hashed-ngram@1` (256-d, lexical). `conformance/retrieval/RESULTS.md` shows 5/5 literal duplicates refused but 0/4 paraphrases, with paraphrase cosines 0.287-0.390, below even the 0.85 annotate floor. The port (`ITextEmbedder` in `src/Curia.Application/Ports/Retrieval.cs`) and the identity record (`EmbeddingModel`, id = `name@version`, R9.5) already exist. `db/0003` leaves the `embedding` column untyped in dimension. Plan D10 sets the constraints: permissive licence (Appendix I), ONNX, dimension recorded per vector, identifier from configuration, CS-17 `ValidateOnStart` refusing to boot with no model, and no fallback to the hashed adapter.

## 1. Model (recommend `bge-small-en-v1.5`)
| | bge-small-en-v1.5 | all-MiniLM-L6-v2 | e5-small-v2 |
|---|---|---|---|
| Licence (model card) | MIT | Apache-2.0 | MIT |
| Dim / params | 384 / 33.4M | 384 / 22.7M | 384 / 33.4M |
| ONNX | official `onnx/model.onnx`, 133 MB (fp32) | Xenova export: 90 MB fp32, 23 MB int8 | listed as ONNX-capable |
| Quality | MTEB avg 62.17 (card) | lower, roughly 56 (recalled, not confirmed on the card) | card gives no score |
| Seq length | 512 | 256 | 512 |
| Catch | query-side instruction for retrieval | smallest; weakest | needs `query: ` / `passage: ` prefixes on every input, easy to get wrong |

All three are compatible with a CC0 project: the weights are linked or loaded, not copied into source, and Appendix I lists MIT and Apache-2.0 as acceptable. Appendix I has no ML-model row, so add one (R I.1 SBOM entry) in the same stage. Training-data provenance is not covered by the card's licence line; ask the owner whether that matters (question 4).

Why bge-small: highest published MTEB of the three, MIT, an official ONNX file and no mandatory prefix for the symmetric dedupe use. Why not MiniLM: Apache-2.0 is fine and it is smaller, but its quality is lower and its 256-token cap truncates long posts. Fallback if size matters more than quality: MiniLM int8 (23 MB).

Unmeasured caveat: small embedders have a high cosine floor for unrelated text, so the 0.94 refuse, 0.85 annotate and 0.50 overlap thresholds (R8.18, G10) must be recalibrated against the corpus. I have not run the model.

## 2. Runtime packages (checked on nuget.org, 2026-10-24)
- `Microsoft.ML.OnnxRuntime` **1.31.0** (listed updated 2026-10-09; the licence line was not on the page, so confirm MIT on the licence link before merging). The scoping document's package manifest (section 9) already names it for Infrastructure.
- `Microsoft.ML.Tokenizers` **2.0.1**, MIT (updated 2026-10-15), for the BERT WordPiece tokenizer reading `vocab.txt`. It avoids a hand-written tokenizer.
- Policy fit: CS-3 puts versions in `Directory.Packages.props` (currently 9 `PackageVersion` entries, none ONNX). CS-7 confines ONNX types to Infrastructure, so the adapter goes in `Curia.Infrastructure`. The package is native, so the lock file (`restore --locked-mode`) and SBOM need regenerating.
- Version risk: both packages are under 3 weeks old. Pin the previous minor if the owner wants the usual two-week age margin (question 3).
- Skip `.Gpu`: CPU-only for CI.

## 3. Model-file delivery
| Option | Reproducible | Digest-pinned | Public-repo fit |
|---|---|---|---|
| Commit `.onnx` (133 MB) | yes | by git | bloats history permanently, and history cannot be rewritten |
| Git LFS | yes | pointer holds sha256 | LFS bandwidth quotas, and a fork cannot clone it reliably |
| Build-time download, sha256 verified | yes if the revision is pinned | yes | repo stays small; needs network at build |
| GitHub release asset | yes | yes | owner-controlled, immutable once published |

**Recommend: a release asset in this repository** (attached to a tagged release), fetched by a build step that checks a sha256 committed in the repository, with the upstream commit hash of the Hugging Face model recorded beside it. A mismatch fails the build. This avoids upstream link rot and keeps 133 MB out of history. Cache the file in CI. Fallback: download directly from the pinned HF revision with the same digest check.

## 4. What the building stage must do
1. `OnnxTextEmbedder : ITextEmbedder` in Infrastructure, model id `bge-small-en-v1.5@1` (name and version from configuration; tokenizer vocabulary and any instruction prefix are part of the version). Embeddings are L2-normalised and deterministic on CPU.
2. CS-17 `ValidateOnStart`: configured for ONNX with a missing file or digest mismatch means the host refuses to boot. No hashed fallback.
3. Re-embed the corpus. Vectors from different models never compare (R9.5): reindex through `EmbeddingIndexer` under the new id, keep the old rows (append-only), and serve only the configured model's rows.
4. Re-measure `conformance/retrieval/` (paraphrase 0/4 baseline, false refusals 0/7, canaries top-3). Flip `R9_5_TheParaphraseBlockRecordsWhatTheDeployedModelCannotDo`, recalibrate thresholds, update `RESULTS.md`, and regenerate the vectors. Alpha policy allows this without a version bump.
5. D11 (novel-query embedding cost): the ONNX path is now ms-scale CPU work per novel query, so D11 stops being theoretical. The stage should at least measure it, and the owner decides whether the miss-rate bound (a cache keyed on canonical query text plus model, 429 with `Retry-After`) lands in this stage or next.
6. `ci.yml`: model-fetch step, cache and the native runtime restore. **Owner-merge only.** Also needs the Debug and Release architecture-test runs to still hold.
7. Docs current in the same PR (plan "Current work", README, CLAUDE.md's "does not" list).

## 5. Owner questions
1. Model: bge-small-en-v1.5 (recommended) / all-MiniLM-L6-v2 (int8) / e5-small-v2?
2. Delivery: release asset with committed sha256 (recommended) / Git LFS / direct HF download with digest / commit the file?
3. Accept OnnxRuntime 1.31.0 and Tokenizers 2.0.1 despite being under 3 weeks old (yes/no)? If no, pin the previous minor.
4. Is the model card's licence line enough for Appendix I, or do you require training-data provenance review (enough / require review)?
5. Is the D11 query-cost bound in the same stage (yes/no)?
6. Will you merge the `ci.yml` change, with CI downloading about 133 MB per cold run (yes/no)?

## Sources
- [bge-small-en-v1.5](https://huggingface.co/BAAI/bge-small-en-v1.5), [onnx dir](https://huggingface.co/BAAI/bge-small-en-v1.5/tree/main/onnx)
- [all-MiniLM-L6-v2](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2), [Xenova ONNX](https://huggingface.co/Xenova/all-MiniLM-L6-v2/tree/main/onnx)
- [e5-small-v2](https://huggingface.co/intfloat/e5-small-v2)
- [Microsoft.ML.OnnxRuntime](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime), [Microsoft.ML.Tokenizers](https://www.nuget.org/packages/Microsoft.ML.Tokenizers)
- Repository: `IMPLEMENTATION_PLAN.md` D10-D12, whitepaper Appendix I, `curia-csharp-scoping.md` CS-3 and section 9, `conformance/retrieval/RESULTS.md`
