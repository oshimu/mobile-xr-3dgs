//! Self-validation (spec §4.8): reads back .usst/.usc and checks structural
//! invariants. Failure maps to CLI exit code 3.

use std::collections::VecDeque;
use std::path::Path;

use glam::Vec3A;
use half::f16;

use crate::unity_encoder::{
    chunk_file_name, lod_std_dev, MAX_STD_DEV, NODE_RECORD_SIZE, NONE_U32, SPLAT_RECORD_SIZE,
    USST_HEADER_SIZE, USST_VERSION,
};
use crate::ValidationError;

pub struct UsstHeader {
    pub version: u32,
    pub node_count: u32,
    pub chunk_count: u32,
    pub chunk_splats: u32,
    pub total_splats: u64,
    pub leaf_splats: u64,
    pub root_node_index: u32,
    pub aabb_min: [f32; 3],
    pub aabb_max: [f32; 3],
}

pub struct UsstNode {
    pub center: Vec3A,
    pub radius: f32,
    pub feature_size: f32,
    pub merged_splat_index: u32,
    pub first_interior_child: u32,
    pub interior_child_count: u16,
    pub direct_leaf_first: u32,
    pub direct_leaf_count: u32,
}

pub struct UsstFile {
    pub header: UsstHeader,
    pub nodes: Vec<UsstNode>,
    /// (chunkId, splatCount, byteLength)
    pub chunks: Vec<(u32, u32, u64)>,
}

fn rd_u32(b: &[u8], o: usize) -> u32 {
    u32::from_le_bytes(b[o..o + 4].try_into().unwrap())
}
fn rd_u16(b: &[u8], o: usize) -> u16 {
    u16::from_le_bytes(b[o..o + 2].try_into().unwrap())
}
fn rd_u64(b: &[u8], o: usize) -> u64 {
    u64::from_le_bytes(b[o..o + 8].try_into().unwrap())
}
fn rd_f32(b: &[u8], o: usize) -> f32 {
    f32::from_le_bytes(b[o..o + 4].try_into().unwrap())
}

fn fail(msg: String) -> anyhow::Error {
    ValidationError(msg).into()
}

pub fn read_usst(path: &Path) -> anyhow::Result<UsstFile> {
    let bytes = std::fs::read(path)?;
    if bytes.len() < USST_HEADER_SIZE {
        return Err(fail(format!(".usst too small: {} bytes", bytes.len())));
    }
    if &bytes[0..4] != b"USST" {
        return Err(fail("bad magic".to_string()));
    }
    let header = UsstHeader {
        version: rd_u32(&bytes, 4),
        node_count: rd_u32(&bytes, 8),
        chunk_count: rd_u32(&bytes, 12),
        chunk_splats: rd_u32(&bytes, 16),
        total_splats: rd_u64(&bytes, 20),
        leaf_splats: rd_u64(&bytes, 28),
        root_node_index: rd_u32(&bytes, 36),
        aabb_min: [rd_f32(&bytes, 40), rd_f32(&bytes, 44), rd_f32(&bytes, 48)],
        aabb_max: [rd_f32(&bytes, 52), rd_f32(&bytes, 56), rd_f32(&bytes, 60)],
    };
    if header.version != USST_VERSION {
        return Err(fail(format!(
            "unsupported version {} (expected {USST_VERSION})",
            header.version
        )));
    }

    let expected = USST_HEADER_SIZE
        + header.node_count as usize * NODE_RECORD_SIZE
        + header.chunk_count as usize * 16;
    if bytes.len() != expected {
        return Err(fail(format!(
            ".usst size mismatch: {} bytes, expected {}",
            bytes.len(),
            expected
        )));
    }

    let mut nodes = Vec::with_capacity(header.node_count as usize);
    let mut o = USST_HEADER_SIZE;
    for _ in 0..header.node_count {
        nodes.push(UsstNode {
            center: Vec3A::new(
                rd_f32(&bytes, o),
                rd_f32(&bytes, o + 4),
                rd_f32(&bytes, o + 8),
            ),
            radius: rd_f32(&bytes, o + 12),
            feature_size: rd_f32(&bytes, o + 16),
            merged_splat_index: rd_u32(&bytes, o + 20),
            first_interior_child: rd_u32(&bytes, o + 24),
            interior_child_count: rd_u16(&bytes, o + 28),
            direct_leaf_first: rd_u32(&bytes, o + 32),
            direct_leaf_count: rd_u32(&bytes, o + 36),
        });
        o += NODE_RECORD_SIZE;
    }

    let mut chunks = Vec::with_capacity(header.chunk_count as usize);
    for _ in 0..header.chunk_count {
        chunks.push((
            rd_u32(&bytes, o),
            rd_u32(&bytes, o + 4),
            rd_u64(&bytes, o + 8),
        ));
        o += 16;
    }

    Ok(UsstFile {
        header,
        nodes,
        chunks,
    })
}

pub fn validate(out_dir: &Path, name: &str, log: &impl Fn(&str)) -> anyhow::Result<()> {
    let usst = read_usst(&out_dir.join(format!("{}.usst", name)))?;
    let h = &usst.header;
    let node_count = h.node_count as usize;
    let total = h.total_splats;

    if h.root_node_index as usize >= node_count {
        return Err(fail("rootNodeIndex out of range".to_string()));
    }

    // (1)(2): per-node range checks.
    for (i, n) in usst.nodes.iter().enumerate() {
        if n.first_interior_child != NONE_U32 {
            let end = n.first_interior_child as u64 + n.interior_child_count as u64;
            if n.interior_child_count == 0 || end > node_count as u64 {
                return Err(fail(format!(
                    "node {i}: interior child range out of bounds"
                )));
            }
        } else if n.interior_child_count != 0 {
            return Err(fail(format!(
                "node {i}: interiorChildCount set but no firstInteriorChild"
            )));
        }
        if n.direct_leaf_first != NONE_U32 {
            let end = n.direct_leaf_first as u64 + n.direct_leaf_count as u64;
            if n.direct_leaf_count == 0 || end > total {
                return Err(fail(format!("node {i}: direct leaf range out of bounds")));
            }
        } else if n.direct_leaf_count != 0 {
            return Err(fail(format!(
                "node {i}: directLeafCount set but no directLeafFirst"
            )));
        }
        if n.merged_splat_index as u64 >= total {
            return Err(fail(format!("node {i}: mergedSplatIndex out of bounds")));
        }
    }

    // (3)(4): reachability from root.
    let mut visited = vec![false; node_count];
    let mut reachable = 0u64;
    let mut leaf_total = 0u64;
    let mut queue = VecDeque::from([h.root_node_index as usize]);
    visited[h.root_node_index as usize] = true;
    while let Some(i) = queue.pop_front() {
        reachable += 1;
        let n = &usst.nodes[i];
        leaf_total += n.direct_leaf_count as u64;
        if n.first_interior_child != NONE_U32 {
            for c in
                n.first_interior_child..(n.first_interior_child + n.interior_child_count as u32)
            {
                if visited[c as usize] {
                    return Err(fail(format!("node {c} reachable via multiple parents")));
                }
                visited[c as usize] = true;
                queue.push_back(c as usize);
            }
        }
    }
    if reachable != node_count as u64 {
        return Err(fail(format!(
            "reachable nodes {} != nodeCount {} (orphan nodes)",
            reachable, node_count
        )));
    }
    if leaf_total != h.leaf_splats {
        return Err(fail(format!(
            "reachable leaves {} != leafSplats {}",
            leaf_total, h.leaf_splats
        )));
    }
    if node_count as u64 + leaf_total != total {
        return Err(fail(format!(
            "nodeCount {} + leaves {} != totalSplats {}",
            node_count, leaf_total, total
        )));
    }

    // (5): chunk sizes; also load splat position/scale for the bounds check.
    let mut sum_counts = 0u64;
    let mut positions: Vec<Vec3A> = Vec::with_capacity(total as usize);
    let mut extents: Vec<f32> = Vec::with_capacity(total as usize);
    for &(id, count, byte_len) in &usst.chunks {
        if byte_len != count as u64 * SPLAT_RECORD_SIZE as u64 {
            return Err(fail(format!(
                "chunk {id}: byteLength inconsistent with splatCount"
            )));
        }
        let path = out_dir.join(chunk_file_name(name, id));
        let data = std::fs::read(&path)
            .map_err(|e| fail(format!("chunk {id}: cannot read {}: {e}", path.display())))?;
        if data.len() as u64 != byte_len {
            return Err(fail(format!(
                "chunk {id}: file size {} != byteLength {}",
                data.len(),
                byte_len
            )));
        }
        sum_counts += count as u64;
        for s in 0..count as usize {
            let o = s * SPLAT_RECORD_SIZE;
            positions.push(Vec3A::new(
                rd_f32(&data, o),
                rd_f32(&data, o + 4),
                rd_f32(&data, o + 8),
            ));
            let sx = f16::from_le_bytes(data[o + 20..o + 22].try_into().unwrap()).to_f32();
            let sy = f16::from_le_bytes(data[o + 22..o + 24].try_into().unwrap()).to_f32();
            let sz = f16::from_le_bytes(data[o + 24..o + 26].try_into().unwrap()).to_f32();
            // Same widening the renderer applies, so the extent here is what is
            // actually drawn (leaves, code 0, are unaffected).
            let d = decode_lod_opacity(data[o + 26]);
            extents.push(2.0 * sx.max(sy).max(sz) * (lod_std_dev(d) / MAX_STD_DEV));
        }
    }
    if sum_counts != total {
        return Err(fail(format!(
            "sum of chunk splatCounts {} != totalSplats {}",
            sum_counts, total
        )));
    }

    // (6): bounding sphere containment, 1% tolerance.
    for (i, n) in usst.nodes.iter().enumerate() {
        let limit = n.radius * 1.01 + 1.0e-4;
        if n.first_interior_child != NONE_U32 {
            for c in
                n.first_interior_child..(n.first_interior_child + n.interior_child_count as u32)
            {
                let child = &usst.nodes[c as usize];
                let d = n.center.distance(child.center) + child.radius;
                if d > limit {
                    return Err(fail(format!(
                        "node {i}: child node {c} not contained (d={d}, radius={})",
                        n.radius
                    )));
                }
            }
        }
        if n.direct_leaf_first != NONE_U32 {
            for g in n.direct_leaf_first..(n.direct_leaf_first + n.direct_leaf_count) {
                let d = n.center.distance(positions[g as usize]) + extents[g as usize];
                if d > limit {
                    return Err(fail(format!(
                        "node {i}: leaf {g} not contained (d={d}, radius={})",
                        n.radius
                    )));
                }
            }
        }
    }

    report_representative_coverage(&usst, &extents, log);

    log(&format!(
        "validate ok: {} nodes, {} chunks, {} splats",
        node_count, h.chunk_count, total
    ));
    Ok(())
}

/// LoD opacity D stored in a record's flags low byte; None for a leaf.
/// Inverse of the encoder's mapping and of the shader's DecodeLodOpacity.
fn decode_lod_opacity(code: u8) -> Option<f32> {
    (code != 0).then(|| 1.0 + 4.0 * ((code - 1) as f32 / 254.0))
}

/// How much of its node a representative splat actually covers when drawn.
/// A ratio well below 1 means coarse nodes leave holes that only fill in as
/// the viewer approaches and the node is expanded — the failure the LoD
/// opacity carried in the flags is there to prevent. Reported per node-size
/// bucket because the shortfall grows with node size.
fn report_representative_coverage(usst: &UsstFile, extents: &[f32], log: &impl Fn(&str)) {
    const BUCKETS: [(f32, &str); 4] = [
        (1.0, "  <1m  "),
        (10.0, "  1-10m"),
        (100.0, " 10-100m"),
        (f32::INFINITY, "  >100m"),
    ];
    let mut ratios: [Vec<f32>; 4] = Default::default();

    for n in &usst.nodes {
        if n.radius <= 0.0 {
            continue;
        }
        let b = BUCKETS
            .iter()
            .position(|&(hi, _)| n.radius < hi)
            .unwrap_or(3);
        ratios[b].push(extents[n.merged_splat_index as usize] / n.radius);
    }

    log("representative coverage (drawn extent / node radius, 1.0 = fully covers):");
    for (b, (_, label)) in BUCKETS.iter().enumerate() {
        let r = &mut ratios[b];
        if r.is_empty() {
            continue;
        }
        r.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let pick = |q: f32| r[((r.len() - 1) as f32 * q).round() as usize];
        log(&format!(
            "{label}: n={:6}  p10={:.3}  median={:.3}  p90={:.3}",
            r.len(),
            pick(0.10),
            pick(0.50),
            pick(0.90)
        ));
    }
}
