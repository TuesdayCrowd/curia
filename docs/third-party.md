# Third-party software and data

This is the record of what the repository depends on and under which licence (errata G65; white paper Appendix I, R I.1). It is compared with `Directory.Packages.props`, with the closure of
`Microsoft.ML.OnnxRuntime` in `src/Curia.Infrastructure/packages.lock.json` and with itself by `ThirdPartyRecordTests` (SEM18), so a package added without a row fails the build.

Licences are the ones each package declares in its own metadata (a `license` expression, or the licence file inside the package). Nothing here is legal advice, and nothing here is a statement that
a file was reviewed beyond what its row says.

## Packages

| Package | Version | Licence | Use |
|---|---|---|---|
| `NSec.Cryptography` | 26.4.0 | MIT | linked: Ed25519, the signature primitive behind a port (`Curia.Canon.Sodium`) |
| `Npgsql` | 10.0.3 | PostgreSQL (permissive) | linked: the Postgres driver (`Curia.Infrastructure`) |
| `ModelContextProtocol.Core` | 2.2.0 | Apache-2.0 | linked: the MCP adapter (`Curia.Mcp`) |
| `Microsoft.ML.OnnxRuntime` | 1.31.0 | MIT | linked and loaded: runs the embedding model (`Curia.Infrastructure`); the native library it carries is loaded at start (see "The native library" below) |
| `Microsoft.ML.OnnxRuntime.Managed` | 1.31.0 | MIT | linked: ONNX Runtime's managed half, brought by the package above |
| `System.Numerics.Tensors` | 9.0.0 | MIT | linked: brought by `Microsoft.ML.OnnxRuntime.Managed` |
| `xunit.v3` | 3.2.2 | Apache-2.0 | tests only |
| `xunit.runner.visualstudio` | 3.1.5 | Apache-2.0 | tests only |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | tests only |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.3 | MIT | tests only |
| `CsCheck` | 4.8.0 | Apache-2.0 | tests only |
| `NetArchTest.Rules` | 1.3.2 | MIT (the project's LICENSE file; the package declares none) | tests only |

## The embedding model

| Package | Version | Licence | Use |
|---|---|---|---|
| `bge-small-en-v1.5` (BAAI) | revision `5c38ec7c405ec4b44b94cc5a9bb96e735b38267a` | MIT by the model card; training-data provenance was not reviewed | loaded: the weights (`model.onnx`) and `vocab.txt` are fetched by digest (`tools/model/fetch-model.py`), never committed |

The model is loaded and run, not copied into this repository. Its licence field on the Hugging Face card is `mit`; what data it was trained on was not reviewed here, and this record makes no claim
about it.

## Written in this repository

| Package | Licence | Use |
|---|---|---|
| tokenizer (`Curia.Infrastructure.Embedder.Tokenization`) | CC0 1.0, like the rest of the repository | BERT-uncased WordPiece, written in this repository to the behaviour of Hugging Face's tokenizer. No code is copied from Hugging Face, Google or Microsoft. It is held to the reference by `models/bge-small-en-v1.5/tokenizer-parity.jsonl` and the manifest's canaries, both written by `tools/model/reference.py`. |

## The native library

`Microsoft.ML.OnnxRuntime` carries `libonnxruntime` for each platform it supports. Its `ThirdPartyNotices.txt` lists 70 components. Read for this record:

- **Eigen** is under the Mozilla Public License 2.0 (MPL-2.0), a file-level weak copyleft whose obligations attach to whoever distributes the binary.
- **Mbed TLS** is dual-licensed; the notice says in its own words that this distribution uses it under the Apache License 2.0.
- The first component listed is **Intel** MKL, under the Intel Simplified Software License; its terms were not read for this record.
- The remaining components were not read.

This project does not distribute `libonnxruntime`: the repository holds no copy, CI restores it from nuget.org, and the only deployment known is the owner's host. **Any artifact that bundles it (a container
image, a release archive, a published build) must carry ONNX Runtime's `ThirdPartyNotices.txt`, and is the owner's question first.**

ONNX Runtime 1.31.0 ships no `osx-x64` native library. On Intel macOS everything builds and every test runs except `Curia.Embedding.Tests`, and a Forum with the real embedder cannot be run there;
Apple Silicon, Linux (x64 and arm64) and Windows work. `Curia.Operator` and `Curia.Architecture.Tests` reference the package with its native assets excluded: neither runs a model.
