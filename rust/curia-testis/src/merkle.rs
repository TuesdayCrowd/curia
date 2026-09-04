//! RFC 9162 §2.1: the Merkle Tree Hash, audit paths, consistency proofs, and the
//! verification procedures for both. The Acta's tree (§6.6, R6.23).
//!
//! A leaf is `SHA-256(0x00 ‖ input)`, a node is `SHA-256(0x01 ‖ left ‖ right)`,
//! the empty tree is `SHA-256()`, and every split is at the largest power of two
//! strictly below the size. What a leaf's *input* is belongs to the log and is
//! frozen by R15.1; this module only hashes what it is handed.
//!
//! Written from the RFC's definitions, not from `Curia.Canon.Acta.MerkleTree`;
//! the two agree on the Certificate Transparency reference vectors, which is the
//! whole point of there being two.

use sha2::{Digest, Sha256};

/// A 32-byte SHA-256 output.
pub type Hash = [u8; 32];

/// `SHA-256(0x00 ‖ input)` (§2.1.1).
pub fn leaf_hash(input: &[u8]) -> Hash {
    let mut h = Sha256::new();
    h.update([0x00u8]);
    h.update(input);
    h.finalize().into()
}

/// `SHA-256(0x01 ‖ left ‖ right)` (§2.1.1).
pub fn node_hash(left: &Hash, right: &Hash) -> Hash {
    let mut h = Sha256::new();
    h.update([0x01u8]);
    h.update(left);
    h.update(right);
    h.finalize().into()
}

/// The empty tree's hash, `SHA-256()`.
pub fn empty_root() -> Hash {
    Sha256::digest([]).into()
}

/// The largest power of two strictly less than `n`, for `n > 1` (§2.1.1's `k`).
pub fn split_point(n: usize) -> usize {
    debug_assert!(n > 1);
    let mut k = 1;
    while k * 2 < n {
        k *= 2;
    }
    k
}

/// `MTH(D[n])` over already-hashed leaves.
pub fn root(leaves: &[Hash]) -> Hash {
    if leaves.is_empty() {
        return empty_root();
    }
    subtree(leaves)
}

fn subtree(leaves: &[Hash]) -> Hash {
    if leaves.len() == 1 {
        return leaves[0];
    }
    let k = split_point(leaves.len());
    node_hash(&subtree(&leaves[..k]), &subtree(&leaves[k..]))
}

/// `PATH(m, D[n])` (§2.1.3.1): the audit path for leaf `index`.
pub fn inclusion_path(leaves: &[Hash], index: usize) -> Vec<Hash> {
    assert!(index < leaves.len(), "not a leaf of this tree");
    let mut path = Vec::new();
    path_into(leaves, index, &mut path);
    path
}

fn path_into(leaves: &[Hash], m: usize, path: &mut Vec<Hash>) {
    if leaves.len() == 1 {
        return;
    }
    let k = split_point(leaves.len());
    if m < k {
        path_into(&leaves[..k], m, path);
        path.push(subtree(&leaves[k..]));
    } else {
        path_into(&leaves[k..], m - k, path);
        path.push(subtree(&leaves[..k]));
    }
}

/// `PROOF(m, D[n])` (§2.1.4.1): the consistency proof from the first `from_size`
/// leaves to all of `leaves`. Empty when the sizes are equal.
pub fn consistency_path(leaves: &[Hash], from_size: usize) -> Vec<Hash> {
    assert!(
        from_size >= 1 && from_size <= leaves.len(),
        "a consistency proof runs from a size in 1..=n"
    );
    if from_size == leaves.len() {
        return Vec::new();
    }
    let mut path = Vec::new();
    subproof(leaves, from_size, true, &mut path);
    path
}

fn subproof(leaves: &[Hash], m: usize, start_from_complete: bool, path: &mut Vec<Hash>) {
    let n = leaves.len();
    if m == n {
        if !start_from_complete {
            path.push(subtree(leaves));
        }
        return;
    }
    let k = split_point(n);
    if m <= k {
        subproof(&leaves[..k], m, start_from_complete, path);
        path.push(subtree(&leaves[k..]));
    } else {
        subproof(&leaves[k..], m - k, false, path);
        path.push(subtree(&leaves[..k]));
    }
}

/// §2.1.3.2: does `path` prove that `leaf` sits at `index` in a tree of
/// `tree_size` leaves whose root is `root`?
pub fn verify_inclusion(
    leaf: &Hash,
    index: u64,
    tree_size: u64,
    path: &[Hash],
    root: &Hash,
) -> bool {
    if tree_size == 0 || index >= tree_size {
        return false;
    }
    let mut fn_ = index;
    let mut sn = tree_size - 1;
    let mut r = *leaf;

    for p in path {
        if sn == 0 {
            return false;
        }
        if fn_ & 1 == 1 || fn_ == sn {
            r = node_hash(p, &r);
            if fn_ & 1 == 0 {
                while fn_ & 1 == 0 && fn_ != 0 {
                    fn_ >>= 1;
                    sn >>= 1;
                }
            }
        } else {
            r = node_hash(&r, p);
        }
        fn_ >>= 1;
        sn >>= 1;
    }

    sn == 0 && r == *root
}

/// §2.1.4.2: does `path` prove that the tree of `second_size` leaves with root
/// `second_root` is an append-only extension of the tree of `first_size` leaves
/// with root `first_root`? Equal sizes with an empty path verify exactly when the
/// roots agree.
pub fn verify_consistency(
    first_size: u64,
    second_size: u64,
    first_root: &Hash,
    second_root: &Hash,
    path: &[Hash],
) -> bool {
    if first_size == 0 || second_size < first_size {
        return false;
    }
    if first_size == second_size {
        return path.is_empty() && first_root == second_root;
    }
    if path.is_empty() {
        return false;
    }

    // Step 3: when the first size is a power of two, the first root is the first
    // proof node; the prover omits it because the verifier already holds it.
    let mut nodes: Vec<Hash> = Vec::with_capacity(path.len() + 1);
    if first_size & (first_size - 1) == 0 {
        nodes.push(*first_root);
    }
    nodes.extend_from_slice(path);

    let mut fn_ = first_size - 1;
    let mut sn = second_size - 1;
    while fn_ & 1 == 1 {
        fn_ >>= 1;
        sn >>= 1;
    }

    let mut fr = nodes[0];
    let mut sr = nodes[0];

    for p in &nodes[1..] {
        if sn == 0 {
            return false;
        }
        if fn_ & 1 == 1 || fn_ == sn {
            fr = node_hash(p, &fr);
            sr = node_hash(p, &sr);
            if fn_ & 1 == 0 {
                while fn_ & 1 == 0 && fn_ != 0 {
                    fn_ >>= 1;
                    sn >>= 1;
                }
            }
        } else {
            sr = node_hash(&sr, p);
        }
        fn_ >>= 1;
        sn >>= 1;
    }

    sn == 0 && fr == *first_root && sr == *second_root
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The Certificate Transparency reference leaves, as hex.
    const LEAVES: [&str; 8] = [
        "",
        "00",
        "10",
        "2021",
        "3031",
        "40414243",
        "5051525354555657",
        "606162636465666768696a6b6c6d6e6f",
    ];

    /// Known roots for the first n leaves, n = 0..=8, from an oracle written from
    /// the RFC's definitions and agreeing with the CT reference implementation.
    const ROOTS: [&str; 9] = [
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d",
        "fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125",
        "aeb6bcfe274b70a14fb067a5e5578264db0fa9b51af5e0ba159158f329e06e77",
        "d37ee418976dd95753c1c73862b9398fa2a2cf9b4ff0fdfe8b30cd95209614b7",
        "4e3bbb1f7b478dcfe71fb631631519a3bca12c9aefca1612bfce4c13a86264d4",
        "76e67dadbcdf1e10e1b74ddc608abd2f98dfb16fbce75277b5232a127f2087ef",
        "ddb89be403809e325750d3d263cd78929c2942b7942a34b77e122c9594a74c8c",
        "5dc9da79a70659a9ad559cb701ded9a2ab9d823aad2f4960cfe370eff4604328",
    ];

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect()
    }

    fn hash(s: &str) -> Hash {
        hex(s).try_into().unwrap()
    }

    fn leaves(n: usize) -> Vec<Hash> {
        LEAVES[..n].iter().map(|l| leaf_hash(&hex(l))).collect()
    }

    fn to_hex(h: &Hash) -> String {
        h.iter().map(|b| format!("{b:02x}")).collect()
    }

    #[test]
    fn roots_match_the_reference_for_sizes_zero_through_eight() {
        for (n, expected) in ROOTS.iter().enumerate() {
            assert_eq!(to_hex(&root(&leaves(n))), *expected, "size {n}");
        }
    }

    #[test]
    fn the_audit_path_for_leaf_one_of_eight_matches_the_reference() {
        let path = inclusion_path(&leaves(8), 1);
        assert_eq!(
            path.iter().map(to_hex).collect::<Vec<_>>(),
            [
                "6e340b9cffb37a989ca544e6bb780a2c78901d3fb33738768511a30617afa01d",
                "5f083f0a1a33ca076a95279832580db3e0ef4584bdff1f54c8a360f50de3031e",
                "6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4",
            ]
        );
    }

    #[test]
    fn every_audit_path_verifies_for_every_size_up_to_eight() {
        for n in 1..=8 {
            let l = leaves(n);
            let r = root(&l);
            for i in 0..n {
                let path = inclusion_path(&l, i);
                assert!(
                    verify_inclusion(&l[i], i as u64, n as u64, &path, &r),
                    "size {n} index {i}"
                );
                let mut tampered = l[i];
                tampered[0] ^= 1;
                assert!(
                    !verify_inclusion(&tampered, i as u64, n as u64, &path, &r),
                    "tampered {n}/{i}"
                );
            }
        }
    }

    #[test]
    fn the_consistency_proofs_three_to_seven_and_four_to_eight_match_the_reference() {
        assert_eq!(
            consistency_path(&leaves(7), 3)
                .iter()
                .map(to_hex)
                .collect::<Vec<_>>(),
            [
                "0298d122906dcfc10892cb53a73992fc5b9f493ea4c9badb27b791b4127a7fe7",
                "07506a85fd9dd2f120eb694f86011e5bb4662e5c415a62917033d4a9624487e7",
                "fac54203e7cc696cf0dfcb42c92a1d9dbaf70ad9e621f4bd8d98662f00e3c125",
                "837dbb152e9b079010717e84e865da4ebc0fa198a806d59d31bf15accef22d0e",
            ]
        );
        assert_eq!(
            consistency_path(&leaves(8), 4)
                .iter()
                .map(to_hex)
                .collect::<Vec<_>>(),
            ["6b47aaf29ee3c2af9af889bc1fb9254dabd31177f16232dd6aab035ca39bf6e4"]
        );
        assert!(consistency_path(&leaves(8), 8).is_empty());
    }

    #[test]
    fn every_consistency_path_verifies_including_k_zero() {
        for n in 1..=8 {
            for m in 1..=n {
                let path = consistency_path(&leaves(n), m);
                assert!(
                    verify_consistency(
                        m as u64,
                        n as u64,
                        &root(&leaves(m)),
                        &root(&leaves(n)),
                        &path
                    ),
                    "{m} -> {n}"
                );
            }
        }
    }

    #[test]
    fn a_rewritten_or_truncated_log_fails_consistency() {
        let earlier = root(&leaves(3));

        let mut rewritten = leaves(7);
        rewritten[1] = leaf_hash(b"not what was logged");
        let path = consistency_path(&rewritten, 3);
        assert!(!verify_consistency(
            3,
            7,
            &earlier,
            &root(&rewritten),
            &path
        ));

        let truncated: Vec<Hash> = leaves(7)[2..].to_vec();
        let path = consistency_path(&truncated, 3);
        assert!(!verify_consistency(
            3,
            5,
            &earlier,
            &root(&truncated),
            &path
        ));

        let honest = leaves(7);
        assert!(verify_consistency(
            3,
            7,
            &earlier,
            &root(&honest),
            &consistency_path(&honest, 3)
        ));
    }

    #[test]
    fn unrelated_heads_do_not_verify() {
        let a = leaves(8);
        let b: Vec<Hash> = (0..8u8).map(|i| leaf_hash(&[0x80 + i])).collect();
        assert!(!verify_consistency(
            4,
            8,
            &root(&leaves(4)),
            &root(&b),
            &consistency_path(&a, 4)
        ));
        assert!(!verify_consistency(8, 8, &root(&a), &root(&b), &[]));
        assert_eq!(to_hex(&hash(ROOTS[8])), ROOTS[8]);
    }
}
