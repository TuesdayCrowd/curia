//! Task 6, Step 3: part of the evidence for the offline claim (R6.19 —
//! "confirm authorship without executing Forum-supplied code and without
//! trusting Forum-supplied results"), not the whole of it. See
//! `task/task-6-report.md`'s "Step 3" section for the full method — a
//! `sandbox-exec` transcript with network denied, plus a manual reading of
//! `#![forbid(unsafe_code)]`'s scope — of which this file is the automated,
//! re-runnable piece: a regression guard against the *locked dependency
//! graph* ever gaining a crate capable of network I/O.
//!
//! ## Method, and its limits — read before trusting this test
//!
//! This walks `Cargo.lock` (the actual, resolved, locked graph `cargo build
//! --locked` builds from — not `Cargo.toml`'s six direct dependencies, which
//! say nothing about what *those* pull in transitively) and asserts that no
//! package name in it appears on a hand-maintained list of crates whose
//! primary purpose is network I/O (HTTP clients/servers, async runtimes
//! built around non-blocking socket I/O, TLS stacks, DNS resolvers,
//! WebSocket libraries, and the like).
//!
//! **What a pass here establishes:** none of the ~60 crates this build
//! currently, actually resolves to is a *known* network-I/O crate. Combined
//! with `Cargo.lock` being committed and `--locked` builds being enforced
//! (CHARTER §2), this means the offline claim does not rest on trusting
//! `cargo`'s dependency *resolution* at build time — the graph is fixed and
//! auditable, and this test re-audits the fixed graph on every `cargo test`
//! run, so a future dependency bump that silently pulls in `tokio` or
//! `reqwest` (directly or transitively) fails a test rather than passing
//! unnoticed.
//!
//! **What a pass here does NOT establish**, stated plainly rather than
//! implied:
//!
//! - The blocklist is a curated list of well-known crate names, not an
//!   exhaustive taxonomy of "every crate that could possibly perform network
//!   I/O." A crate with an unfamiliar name that happens to open sockets
//!   would not be caught by name-matching alone. This is why the report
//!   pairs this test with a full manual read of every one of the ~60
//!   resolved packages' *purpose* (crypto primitives, JSON parsing, Unicode
//!   tables, proc-macro plumbing — nothing that reads as "network" by
//!   function either), not just this automated name check.
//! - This test says nothing about what the code in those crates *actually
//!   does* at runtime — it is a graph-shape check, not a behavioral one.
//!   The `sandbox-exec` transcript in the report is what tests actual
//!   runtime behavior (a real `verify` invocation, run to completion with
//!   the network denied at the OS level) rather than only what the
//!   dependency graph could theoretically reach.
//! - It says nothing about a *build-time* network fetch (`cargo` resolving
//!   and downloading crates from crates.io) — CHARTER §2 is explicit that
//!   this is a build-time act, not a runtime one, and out of scope for an
//!   *offline verification* claim; `--offline`/vendoring would be the tool
//!   for that separate concern, not this test.

use std::fs;
use std::path::Path;

/// Crate names whose primary published purpose is network I/O: HTTP
/// clients/servers, async runtimes built around non-blocking network
/// sockets, TLS/DNS stacks, WebSocket implementations. Not exhaustive —
/// see the module doc comment's "what this does NOT establish."
const NETWORK_CAPABLE_CRATE_NAMES: &[&str] = &[
    // HTTP clients / servers
    "reqwest",
    "hyper",
    "hyper-util",
    "hyper-tls",
    "hyper-rustls",
    "isahc",
    "surf",
    "ureq",
    "attohttpc",
    "actix-web",
    "actix-http",
    "actix-rt",
    "axum",
    "warp",
    "tide",
    "rocket",
    // Async runtimes built around non-blocking network I/O
    "tokio",
    "tokio-util",
    "tokio-native-tls",
    "tokio-rustls",
    "async-std",
    "async-net",
    "smol",
    "mio",
    "polling",
    // Low-level socket / networking
    "socket2",
    "net2",
    // TLS
    "native-tls",
    "openssl",
    "openssl-sys",
    "rustls",
    "rustls-pemfile",
    "webpki",
    "webpki-roots",
    // DNS
    "trust-dns-resolver",
    "trust-dns-proto",
    "hickory-resolver",
    "hickory-proto",
    "dns-lookup",
    // WebSockets
    "tungstenite",
    "tokio-tungstenite",
    "async-tungstenite",
    "ws",
    // gRPC / QUIC / HTTP2-3
    "tonic",
    "quinn",
    "quinn-proto",
    "h2",
    "h3",
    // Generic "fetch a URL" / lower-level transport
    "curl",
    "curl-sys",
    "ssh2",
    "libssh2-sys",
];

/// Parses `Cargo.lock`'s package names by hand — no `toml` dependency exists
/// in this crate (CHARTER §2: "no new dependencies"), and `Cargo.lock`'s
/// format is simple enough not to need one: every package is a `[[package]]`
/// table whose very next `name = "..."` line is that package's name.
fn locked_package_names(lockfile: &str) -> Vec<String> {
    let mut names = Vec::new();
    let mut lines = lockfile.lines().peekable();
    while let Some(line) = lines.next() {
        if line.trim() == "[[package]]" {
            if let Some(name_line) = lines.peek() {
                if let Some(rest) = name_line.trim().strip_prefix("name = \"") {
                    if let Some(name) = rest.strip_suffix('"') {
                        names.push(name.to_string());
                    }
                }
            }
        }
    }
    names
}

#[test]
fn no_locked_dependency_is_a_known_network_capable_crate() {
    let lockfile_path = Path::new(env!("CARGO_MANIFEST_DIR")).join("Cargo.lock");
    let lockfile = fs::read_to_string(&lockfile_path)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", lockfile_path.display()));

    let names = locked_package_names(&lockfile);
    assert!(
        names.len() > 10,
        "sanity check: the lockfile parser found only {} package names, which is \
         suspiciously few for this crate's dependency graph — the parser itself may be \
         broken rather than the graph being small",
        names.len()
    );

    let offenders: Vec<&String> = names
        .iter()
        .filter(|name| NETWORK_CAPABLE_CRATE_NAMES.contains(&name.as_str()))
        .collect();

    assert!(
        offenders.is_empty(),
        "Cargo.lock resolves to at least one crate name on the network-capable \
         blocklist: {offenders:?}. This crate is offline-only (CHARTER §2); if this is a \
         genuine new dependency, it must not be added at all, per \"no new dependencies\" — \
         if it is a false positive (a same-named crate that does not actually do network \
         I/O), update the blocklist's comment to say so explicitly rather than silently \
         deleting the entry."
    );
}

/// A second, independent pass: every resolved package name, printed once,
/// so a human auditing the report (see `task/task-6-report.md`) can read
/// the *complete* list this test checked against — not just trust that the
/// blocklist was applied to *something*. Run with `--nocapture` to see it.
#[test]
fn print_full_locked_dependency_list_for_manual_audit() {
    let lockfile_path = Path::new(env!("CARGO_MANIFEST_DIR")).join("Cargo.lock");
    let lockfile = fs::read_to_string(&lockfile_path)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", lockfile_path.display()));
    let mut names = locked_package_names(&lockfile);
    names.sort();
    names.dedup();
    println!("{} distinct locked package names:", names.len());
    for name in &names {
        println!("  {name}");
    }
}
