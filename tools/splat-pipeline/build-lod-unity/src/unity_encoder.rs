//! UnityEncoder (spec §4.4–4.5): re-walks the LoD tree with its own DFS to
//! assign global splat indices, then writes .usst / .usc / manifest.json.
//!
//! Invariants constructed here (relied on by the Unity runtime):
//! - a node's direct leaf children occupy consecutive global indices
//! - a node's interior children occupy consecutive NodeTable slots
//! - the coarse backbone (incl. every region root's merged splat) occupies
//!   the first chunk(s), so a whole-scene coarse view and all §5.5 fallback
//!   ancestors are found in low-numbered, always-resident chunks
//! - each region (subtree <= regionSplats) is contiguous, so refining a
//!   region touches only its own chunk(s)

use std::fs::File;
use std::io::{BufWriter, Write};
use std::path::PathBuf;

use glam::Vec3A;
use half::f16;
use spark_lib::tsplat::{Tsplat, TsplatArray};

use crate::InputError;

pub const CHUNK_SPLATS: usize = 65536;
pub const SPLAT_RECORD_SIZE: usize = 32;
pub const NODE_RECORD_SIZE: usize = 40;
pub const USST_HEADER_SIZE: usize = 64;
pub const USST_MAGIC: &[u8; 4] = b"USST";
pub const USST_VERSION: u32 = 2;
pub const NONE_U32: u32 = 0xFFFF_FFFF;

pub struct EncodeOptions {
    pub out_dir: PathBuf,
    pub name: String,
    pub source: String,
    pub lod_method: String,
    pub lod_base: f32,
    pub max_sh: usize,
    pub coord_preset: String,
    pub input_splats: usize,
    /// Subtrees at or below this size become contiguous "regions"; everything
    /// above is coarse backbone emitted first. Smaller = richer always-resident
    /// base view, but refinement pulls proportionally less per chunk.
    pub region_splats: usize,
}

pub const DEFAULT_REGION_SPLATS: usize = 8192;

/// NodeTable record under construction (serialized form in spec §3.4).
#[derive(Debug, Clone)]
pub struct NodeBuild {
    pub orig: usize,
    pub merged_global: u32,
    pub first_interior: u32,
    pub interior_count: u32,
    pub direct_leaf_first: u32,
    pub direct_leaf_count: u32,
    pub center: Vec3A,
    pub radius: f32,
    pub feature_size: f32,
    pub depth: u32,
}

pub struct TreeLayout {
    /// globalIndex -> original array index
    pub order: Vec<u32>,
    pub nodes: Vec<NodeBuild>,
    pub leaf_splats: u64,
    pub tree_depth: u32,
    pub max_interior_children: u32,
    pub max_direct_leaves: u32,
    /// Splats emitted in phase 1 (coarse backbone incl. region-root mergeds);
    /// ceil(backbone_splats / chunkSplats) chunks hold the whole-scene coarse view.
    pub backbone_splats: u64,
}

/// Emits a node's merged splat at the current position and fills the
/// viewpoint-independent record fields. Idempotent per node.
fn emit_merged<SA: TsplatArray>(
    splats: &SA,
    slot: usize,
    depth: u32,
    order: &mut Vec<u32>,
    nodes: &mut [NodeBuild],
    stats: &mut LayoutStats,
) {
    debug_assert_eq!(nodes[slot].merged_global, NONE_U32);
    let orig = nodes[slot].orig;
    stats.tree_depth = stats.tree_depth.max(depth);
    nodes[slot].merged_global = order.len() as u32;
    order.push(orig as u32);
    nodes[slot].center = splats.get(orig).center();
    // Captured before the LoD-opacity re-encode mutates opacity
    // (feature_size depends on it).
    nodes[slot].feature_size = splats.get(orig).feature_size();
    nodes[slot].depth = depth;
}

/// Emits a node's contiguous direct leaves at the current position and
/// allocates a contiguous NodeTable block for its interior children.
/// Returns (firstInterior, count).
fn emit_body<SA: TsplatArray>(
    splats: &SA,
    slot: usize,
    order: &mut Vec<u32>,
    nodes: &mut Vec<NodeBuild>,
    stats: &mut LayoutStats,
) -> anyhow::Result<(u32, u32)> {
    let orig = nodes[slot].orig;
    let children = splats.get_children(orig);
    let mut interior: Vec<usize> = Vec::new();
    let mut leaf_first = NONE_U32;
    let mut leaf_count = 0u32;
    for &child in children.iter() {
        if splats.get_child_count_start(child).0 == 0 {
            if leaf_count == 0 {
                leaf_first = order.len() as u32;
            }
            order.push(child as u32);
            leaf_count += 1;
            stats.leaf_splats += 1;
        } else {
            interior.push(child);
        }
    }
    if interior.len() > u16::MAX as usize {
        return Err(InputError(format!(
            "node has {} interior children (> u16::MAX); lower --lod-base",
            interior.len()
        ))
        .into());
    }

    let first_interior = if interior.is_empty() {
        NONE_U32
    } else {
        nodes.len() as u32
    };
    for &c in &interior {
        nodes.push(NodeBuild {
            orig: c,
            merged_global: NONE_U32,
            first_interior: NONE_U32,
            interior_count: 0,
            direct_leaf_first: NONE_U32,
            direct_leaf_count: 0,
            center: Vec3A::ZERO,
            radius: 0.0,
            feature_size: 0.0,
            depth: 0,
        });
    }

    stats.max_interior_children = stats.max_interior_children.max(interior.len() as u32);
    stats.max_direct_leaves = stats.max_direct_leaves.max(leaf_count);

    let node = &mut nodes[slot];
    node.first_interior = first_interior;
    node.interior_count = interior.len() as u32;
    node.direct_leaf_first = leaf_first;
    node.direct_leaf_count = leaf_count;

    Ok((first_interior, interior.len() as u32))
}

#[derive(Default)]
struct LayoutStats {
    leaf_splats: u64,
    tree_depth: u32,
    max_interior_children: u32,
    max_direct_leaves: u32,
}

/// First pass of spec §4.4, revised after P3 field testing: hybrid
/// coarse-first ordering (the same idea as spark's chunk_tree).
///
/// Pure preorder DFS scatters subtree heads — and with them every coarse
/// merged splat — across the whole file, so any tree cut touches most chunks
/// and a pool-bounded frontier starves. Instead:
///
/// - Phase 1 (backbone): nodes whose subtree exceeds `region_splats` are
///   emitted in BFS order, and each region root's MERGED splat is hoisted into
///   this stream. The coarse backbone lands in the first chunk(s), so a
///   low-detail whole-scene view — and every §5.5 fallback ancestor — lives in
///   the pinned chunk 0 region.
/// - Phase 2 (regions): each region body (leaves + descendants) is emitted
///   contiguously via preorder DFS, in backbone discovery order (spatially
///   coherent). Refining one region touches only its own chunk or two.
///
/// NodeTable invariants are unchanged: interior children occupy a contiguous
/// slot block allocated when the parent is visited, and parent slots precede
/// child slots. Direct leaves still immediately follow their node's merged
/// splat.
pub fn assign_indices<SA: TsplatArray>(
    splats: &SA,
    root: usize,
    region_splats: usize,
) -> anyhow::Result<TreeLayout> {
    let region_splats = region_splats.clamp(64, CHUNK_SPLATS);
    let n = splats.len();
    if n >= NONE_U32 as usize {
        return Err(InputError(format!("too many splats for u32 indexing: {n}")).into());
    }
    if splats.get_child_count_start(root).0 == 0 {
        return Err(
            InputError("root node has no children (no LoD tree present)".to_string()).into(),
        );
    }

    // Subtree splat counts (merged + leaves), iterative post-order.
    let mut subtree_size = vec![0u32; n];
    {
        let mut stack: Vec<(usize, bool)> = vec![(root, false)];
        while let Some((o, done)) = stack.pop() {
            if done {
                let mut s = 1u64;
                for &c in splats.get_children(o).iter() {
                    s += subtree_size[c] as u64;
                }
                subtree_size[o] = s.min(u32::MAX as u64) as u32;
            } else {
                stack.push((o, true));
                for &c in splats.get_children(o).iter() {
                    if splats.get_child_count_start(c).0 == 0 {
                        subtree_size[c] = 1;
                    } else {
                        stack.push((c, false));
                    }
                }
            }
        }
    }

    let mut order: Vec<u32> = Vec::with_capacity(n);
    let mut nodes: Vec<NodeBuild> = Vec::new();
    let mut stats = LayoutStats::default();

    nodes.push(NodeBuild {
        orig: root,
        merged_global: NONE_U32,
        first_interior: NONE_U32,
        interior_count: 0,
        direct_leaf_first: NONE_U32,
        direct_leaf_count: 0,
        center: Vec3A::ZERO,
        radius: 0.0,
        feature_size: 0.0,
        depth: 0,
    });

    // Phase 1: BFS over the backbone (subtree > one chunk). Region roots
    // (subtree <= one chunk) get their MERGED splat emitted into the backbone
    // stream right when discovered — a whole-scene cut at region granularity
    // then touches only the backbone chunk(s) — while their bodies (leaves +
    // descendants) are deferred to phase 2.
    let mut bfs: std::collections::VecDeque<(usize, u32)> = std::collections::VecDeque::new();
    let mut deferred: Vec<(usize, u32)> = Vec::new();
    if subtree_size[root] as usize <= region_splats {
        emit_merged(splats, 0, 0, &mut order, &mut nodes, &mut stats);
        deferred.push((0, 0));
    } else {
        bfs.push_back((0, 0));
    }
    while let Some((slot, depth)) = bfs.pop_front() {
        emit_merged(splats, slot, depth, &mut order, &mut nodes, &mut stats);
        let (first, count) = emit_body(splats, slot, &mut order, &mut nodes, &mut stats)?;
        for i in 0..count {
            let child_slot = (first + i) as usize;
            let child_orig = nodes[child_slot].orig;
            if subtree_size[child_orig] as usize <= region_splats {
                emit_merged(
                    splats,
                    child_slot,
                    depth + 1,
                    &mut order,
                    &mut nodes,
                    &mut stats,
                );
                deferred.push((child_slot, depth + 1));
            } else {
                bfs.push_back((child_slot, depth + 1));
            }
        }
    }
    let backbone_splats = order.len() as u64;

    // Phase 2: each region body is contiguous (preorder DFS); interior
    // descendants' merged splats stay inside the region — refining within a
    // region only ever touches that region's chunk(s).
    let mut stack: Vec<(usize, u32)> = Vec::new();
    for &(region_slot, region_depth) in &deferred {
        let (first, count) = emit_body(splats, region_slot, &mut order, &mut nodes, &mut stats)?;
        for i in (0..count).rev() {
            stack.push(((first + i) as usize, region_depth + 1));
        }
        while let Some((slot, depth)) = stack.pop() {
            emit_merged(splats, slot, depth, &mut order, &mut nodes, &mut stats);
            let (f, c) = emit_body(splats, slot, &mut order, &mut nodes, &mut stats)?;
            for i in (0..c).rev() {
                stack.push(((f + i) as usize, depth + 1));
            }
        }
    }

    if order.len() != n {
        return Err(InputError(format!(
            "tree does not cover the splat array: reached {} of {} (multiple roots or orphans)",
            order.len(),
            n
        ))
        .into());
    }

    Ok(TreeLayout {
        order,
        nodes,
        leaf_splats: stats.leaf_splats,
        tree_depth: stats.tree_depth,
        max_interior_children: stats.max_interior_children,
        max_direct_leaves: stats.max_direct_leaves,
        backbone_splats,
    })
}

/// Base render extent in standard deviations; mirrors spark's default
/// `SparkRenderer.maxStdDev = sqrt(8)` and the renderer's leaf quad radius.
pub const MAX_STD_DEV: f32 = std::f32::consts::SQRT_2 * 2.0;

/// LoD opacity D of a splat whose opacity has been through
/// `encode_lod_opacity()` (which maps D 1..5 onto opacity 1..2).
/// Returns None for an ordinary leaf.
pub fn lod_opacity<S: Tsplat>(s: &S) -> Option<f32> {
    let a = s.opacity();
    (a > 1.0).then(|| (a * 4.0 - 3.0).min(5.0))
}

/// Render extent in standard deviations, widened for LoD representatives so
/// they cover the cell they stand in for. Matches spark's splatVertex.glsl
/// `adjustedStdDev = maxStdDev + 0.7 * (D - 1)` and the runtime `LodStdDev`.
pub fn lod_std_dev(d: Option<f32>) -> f32 {
    MAX_STD_DEV + 0.7 * (d.unwrap_or(1.0) - 1.0)
}

/// `encode_lod_opacity()` maps LoD opacity 1..5 onto opacity 1..2. The
/// remainder is *not* baked into the scales (spark's non-default `lodInflate`
/// path): a representative fitted only to where its children's mass sits
/// covers roughly half its node radius, which is what showed up as holes in
/// distant geometry. It is carried in the record flags instead and applied at
/// render time as a wider extent plus the higher opacity.
pub fn encode_lod_opacity<SA: TsplatArray>(splats: &mut SA) {
    splats.encode_lod_opacity();
}

/// Conservative world-space radius of a single splat, in the same units the
/// renderer draws it: 2 sigma for a leaf, widened for a representative by the
/// same factor `lod_std_dev` applies.
fn splat_extent<S: Tsplat>(s: &S) -> f32 {
    2.0 * s.max_scale() * (lod_std_dev(lod_opacity(s)) / MAX_STD_DEV)
}

/// Bottom-up bounding sphere computation. Parent slots always precede child
/// slots, so a reverse scan visits children first.
fn compute_radii<SA: TsplatArray>(splats: &SA, layout: &mut TreeLayout) {
    for i in (0..layout.nodes.len()).rev() {
        let center = layout.nodes[i].center;
        let orig = layout.nodes[i].orig;
        let mut radius = splat_extent(&splats.get(orig));

        let (lf, lc) = (
            layout.nodes[i].direct_leaf_first,
            layout.nodes[i].direct_leaf_count,
        );
        if lf != NONE_U32 {
            for g in lf..(lf + lc) {
                let leaf = splats.get(layout.order[g as usize] as usize);
                radius = radius.max(center.distance(leaf.center()) + splat_extent(&leaf));
            }
        }
        let (cf, cc) = (
            layout.nodes[i].first_interior,
            layout.nodes[i].interior_count,
        );
        if cf != NONE_U32 {
            for c in cf..(cf + cc) {
                let child = &layout.nodes[c as usize];
                radius = radius.max(center.distance(child.center) + child.radius);
            }
        }
        layout.nodes[i].radius = radius;
    }
}

pub fn encode_unorm16(v: f32) -> u16 {
    ((v.clamp(-1.0, 1.0) * 0.5 + 0.5) * 65535.0).round() as u16
}

pub fn decode_unorm16(u: u16) -> f32 {
    (u as f32 / 65535.0) * 2.0 - 1.0
}

pub fn encode_unorm8(v: f32) -> u8 {
    (v.clamp(0.0, 1.0) * 255.0).round() as u8
}

/// Serializes one SplatRecord (spec §3.2) and returns (record, quantization
/// errors): rotation error in degrees, max relative scale error.
pub fn quantize_record<S: Tsplat>(s: &S) -> ([u8; SPLAT_RECORD_SIZE], f32, f32) {
    let mut rec = [0u8; SPLAT_RECORD_SIZE];

    let c = s.center();
    rec[0..4].copy_from_slice(&c.x.to_le_bytes());
    rec[4..8].copy_from_slice(&c.y.to_le_bytes());
    rec[8..12].copy_from_slice(&c.z.to_le_bytes());

    let q = s.quaternion().normalize();
    let qa = [q.x, q.y, q.z, q.w];
    let qu: [u16; 4] = [
        encode_unorm16(qa[0]),
        encode_unorm16(qa[1]),
        encode_unorm16(qa[2]),
        encode_unorm16(qa[3]),
    ];
    rec[12..14].copy_from_slice(&qu[0].to_le_bytes());
    rec[14..16].copy_from_slice(&qu[1].to_le_bytes());
    rec[16..18].copy_from_slice(&qu[2].to_le_bytes());
    rec[18..20].copy_from_slice(&qu[3].to_le_bytes());

    // Rotation quantization error: angle between original and decoded quat.
    let dq = glam::Quat::from_xyzw(
        decode_unorm16(qu[0]),
        decode_unorm16(qu[1]),
        decode_unorm16(qu[2]),
        decode_unorm16(qu[3]),
    )
    .normalize();
    let dot = (dq.x * qa[0] + dq.y * qa[1] + dq.z * qa[2] + dq.w * qa[3])
        .abs()
        .min(1.0);
    let rot_err_deg = 2.0 * dot.acos().to_degrees();

    let scales = s.scales();
    let sh: [f16; 3] = [
        f16::from_f32(scales.x),
        f16::from_f32(scales.y),
        f16::from_f32(scales.z),
    ];
    rec[20..22].copy_from_slice(&sh[0].to_le_bytes());
    rec[22..24].copy_from_slice(&sh[1].to_le_bytes());
    rec[24..26].copy_from_slice(&sh[2].to_le_bytes());

    // flags (u16) at 26..28: low byte carries the LoD opacity D as unorm8 of
    // (D-1)/4, i.e. D in 1..5, with 0 marking an ordinary leaf. The renderer
    // widens the quad and raises the alpha for D > 1. High byte reserved.
    let d = lod_opacity(s);
    rec[26] = match d {
        // 1..5 maps onto 1..=255 so a representative never reads back as a leaf.
        Some(d) => (1.0 + 254.0 * ((d - 1.0) / 4.0).clamp(0.0, 1.0)).round() as u8,
        None => 0,
    };

    let mut scale_err = 0.0f32;
    for d in 0..3 {
        let orig = [scales.x, scales.y, scales.z][d];
        if orig > 0.0 {
            scale_err = scale_err.max((sh[d].to_f32() - orig).abs() / orig);
        }
    }

    // rgb() already holds display-space color (0.5 + SH_C0 * dc applied at decode).
    let rgb = s.rgb();
    rec[28] = encode_unorm8(rgb.x);
    rec[29] = encode_unorm8(rgb.y);
    rec[30] = encode_unorm8(rgb.z);
    // Leaves store their true alpha here; for a representative (rec[26] != 0)
    // the renderer takes the alpha from the LoD opacity instead, so the
    // clamped 1.0 stored here is unused.
    rec[31] = encode_unorm8(s.opacity());

    (rec, rot_err_deg, scale_err)
}

fn write_u32(w: &mut impl Write, v: u32) -> std::io::Result<()> {
    w.write_all(&v.to_le_bytes())
}
fn write_u64(w: &mut impl Write, v: u64) -> std::io::Result<()> {
    w.write_all(&v.to_le_bytes())
}
fn write_f32(w: &mut impl Write, v: f32) -> std::io::Result<()> {
    w.write_all(&v.to_le_bytes())
}

pub fn chunk_file_name(name: &str, chunk_id: u32) -> String {
    format!("{}_{:04}.usc", name, chunk_id)
}

pub fn encode<SA: TsplatArray>(
    splats: &mut SA,
    opts: &EncodeOptions,
    log: &impl Fn(&str),
) -> anyhow::Result<serde_json::Value> {
    // Pass 1: DFS layout (feature_size captured on the un-encoded opacities).
    let mut layout = assign_indices(splats, 0, opts.region_splats)?;
    log(&format!(
        "layout: {} nodes, {} leaves, depth {}",
        layout.nodes.len(),
        layout.leaf_splats,
        layout.tree_depth
    ));

    // Opacity post-processing, then bounds on the render extent it implies.
    encode_lod_opacity(splats);
    compute_radii(splats, &mut layout);

    let total_splats = layout.order.len() as u64;
    let chunk_count = layout.order.len().div_ceil(CHUNK_SPLATS) as u32;

    // Pass 2: stream out .usc chunks in global order.
    let mut chunk_entries: Vec<(u32, u32, u64)> = Vec::with_capacity(chunk_count as usize);
    let mut aabb_min = Vec3A::splat(f32::INFINITY);
    let mut aabb_max = Vec3A::splat(f32::NEG_INFINITY);
    let mut rot_err_max = 0.0f32;
    let mut scale_err_max = 0.0f32;
    let mut scale_err_sum = 0.0f64;
    let mut usc_total_bytes = 0u64;

    for chunk_id in 0..chunk_count {
        let start = chunk_id as usize * CHUNK_SPLATS;
        let count = (layout.order.len() - start).min(CHUNK_SPLATS);
        let path = opts.out_dir.join(chunk_file_name(&opts.name, chunk_id));
        let mut writer = BufWriter::new(File::create(&path)?);
        for g in start..start + count {
            let splat = splats.get(layout.order[g] as usize);
            let (rec, rot_err, scale_err) = quantize_record(&splat);
            writer.write_all(&rec)?;

            let ext = Vec3A::splat(splat_extent(&splat));
            aabb_min = aabb_min.min(splat.center() - ext);
            aabb_max = aabb_max.max(splat.center() + ext);
            rot_err_max = rot_err_max.max(rot_err);
            scale_err_max = scale_err_max.max(scale_err);
            scale_err_sum += scale_err as f64;
        }
        writer.flush()?;
        let byte_len = (count * SPLAT_RECORD_SIZE) as u64;
        usc_total_bytes += byte_len;
        chunk_entries.push((chunk_id, count as u32, byte_len));
    }
    log(&format!(
        "wrote {} chunks ({} bytes)",
        chunk_count, usc_total_bytes
    ));

    // .usst: header + node table + chunk index.
    let usst_path = opts.out_dir.join(format!("{}.usst", opts.name));
    let mut w = BufWriter::new(File::create(&usst_path)?);
    w.write_all(USST_MAGIC)?;
    write_u32(&mut w, USST_VERSION)?;
    write_u32(&mut w, layout.nodes.len() as u32)?;
    write_u32(&mut w, chunk_count)?;
    write_u32(&mut w, CHUNK_SPLATS as u32)?;
    write_u64(&mut w, total_splats)?;
    write_u64(&mut w, layout.leaf_splats)?;
    write_u32(&mut w, 0)?; // rootNodeIndex
    for v in [
        aabb_min.x, aabb_min.y, aabb_min.z, aabb_max.x, aabb_max.y, aabb_max.z,
    ] {
        write_f32(&mut w, v)?;
    }

    for node in &layout.nodes {
        write_f32(&mut w, node.center.x)?;
        write_f32(&mut w, node.center.y)?;
        write_f32(&mut w, node.center.z)?;
        write_f32(&mut w, node.radius)?;
        write_f32(&mut w, node.feature_size)?;
        write_u32(&mut w, node.merged_global)?;
        write_u32(&mut w, node.first_interior)?;
        w.write_all(&(node.interior_count as u16).to_le_bytes())?;
        w.write_all(&0u16.to_le_bytes())?; // flags
        write_u32(&mut w, node.direct_leaf_first)?;
        write_u32(&mut w, node.direct_leaf_count)?;
    }

    for &(id, count, bytes) in &chunk_entries {
        write_u32(&mut w, id)?;
        write_u32(&mut w, count)?;
        write_u64(&mut w, bytes)?;
    }
    w.flush()?;
    let usst_bytes = USST_HEADER_SIZE as u64
        + (layout.nodes.len() * NODE_RECORD_SIZE) as u64
        + (chunk_entries.len() * 16) as u64;
    log(&format!(
        "wrote {} ({} bytes)",
        usst_path.display(),
        usst_bytes
    ));

    // manifest.json (debug only, spec §3.6).
    let scale_err_mean = if total_splats > 0 {
        scale_err_sum / total_splats as f64
    } else {
        0.0
    };
    let manifest = serde_json::json!({
        "source": opts.source,
        "generatedAt": iso8601_now(),
        "generator": {
            "tool": "build-lod-unity",
            "version": env!("CARGO_PKG_VERSION"),
            "sparkLibCommit": crate::VENDORED_COMMIT.trim(),
        },
        "params": {
            "lodMethod": opts.lod_method,
            "lodBase": opts.lod_base,
            "maxSh": opts.max_sh,
            "chunkSplats": CHUNK_SPLATS,
            "regionSplats": opts.region_splats,
            "coordPreset": opts.coord_preset,
        },
        "stats": {
            "inputSplats": opts.input_splats,
            "leafSplats": layout.leaf_splats,
            "interiorNodes": layout.nodes.len(),
            "totalSplats": total_splats,
            "treeDepth": layout.tree_depth,
            "backboneSplats": layout.backbone_splats,
            "chunkCount": chunk_count,
            "maxInteriorChildren": layout.max_interior_children,
            "maxDirectLeaves": layout.max_direct_leaves,
            "usstBytes": usst_bytes,
            "uscTotalBytes": usc_total_bytes,
            "quantization": {
                "rotationMaxErrorDeg": rot_err_max,
                "scaleMaxRelError": scale_err_max,
                "scaleMeanRelError": scale_err_mean,
            },
        },
    });
    let manifest_path = opts.out_dir.join(format!("{}.manifest.json", opts.name));
    std::fs::write(&manifest_path, serde_json::to_string_pretty(&manifest)?)?;

    Ok(manifest)
}

/// UTC timestamp without external chrono dependency.
fn iso8601_now() -> String {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let days = secs / 86400;
    let (h, m, s) = ((secs % 86400) / 3600, (secs % 3600) / 60, secs % 60);
    // Civil-from-days algorithm (Howard Hinnant).
    let z = days as i64 + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let mo = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if mo <= 2 { y + 1 } else { y };
    format!("{:04}-{:02}-{:02}T{:02}:{:02}:{:02}Z", y, mo, d, h, m, s)
}

#[cfg(test)]
mod tests {
    use super::*;
    use glam::{Quat, Vec3A};
    use spark_lib::gsplat::{Gsplat, GsplatArray};
    use spark_lib::tsplat::TsplatArray;

    fn make_splat(seed: u64) -> Gsplat {
        // Cheap deterministic pseudo-random values.
        let f = |k: u64| {
            ((seed
                .wrapping_mul(6364136223846793005)
                .wrapping_add(k.wrapping_mul(1442695040888963407))
                >> 33) as f32
                / (u32::MAX >> 1) as f32)
                .fract()
        };
        let q = Quat::from_xyzw(f(1) - 0.5, f(2) - 0.5, f(3) - 0.5, f(4) - 0.5 + 1.0).normalize();
        Gsplat::new(
            Vec3A::new(f(5) * 10.0, f(6) * 10.0, f(7) * 10.0),
            0.25 + 0.75 * f(8),
            Vec3A::new(f(9), f(10), f(11)),
            Vec3A::new(0.01 + f(12) * 0.2, 0.01 + f(13) * 0.2, 0.01 + f(14) * 0.2),
            q,
        )
    }

    #[test]
    fn quantization_roundtrip() {
        for seed in 0..500u64 {
            let s = make_splat(seed);
            let sr = &s;
            let (rec, rot_err, scale_err) = quantize_record(&sr);

            // Position roundtrips exactly (f32).
            let px = f32::from_le_bytes(rec[0..4].try_into().unwrap());
            assert_eq!(px, sr.center().x);

            // Rotation error stays small for unorm16. The acos-based
            // measurement bottoms out around 0.04 deg due to f32 precision,
            // so this bound is dominated by measurement noise.
            assert!(rot_err < 0.2, "rot_err {} too large", rot_err);
            // Scale error small for f16 (source scales are f16-derived already).
            assert!(scale_err < 1.0e-3, "scale_err {}", scale_err);

            // Color/alpha roundtrip within 1/255.
            let a = rec[31] as f32 / 255.0;
            assert!((a - sr.opacity().clamp(0.0, 1.0)).abs() <= 0.5 / 255.0 + 1e-6);

            // A leaf (opacity <= 1) carries no LoD opacity.
            assert_eq!(u16::from_le_bytes(rec[26..28].try_into().unwrap()), 0);
        }
    }

    /// The LoD opacity D survives the flags byte and decodes to the same value
    /// the shader's DecodeLodOpacity computes, so the drawn extent matches what
    /// compute_radii bounded.
    #[test]
    fn lod_opacity_roundtrips_through_flags() {
        // Shader-side decode: code 0 = leaf, else D = 1 + 4*(code-1)/254.
        let decode = |code: u8| (code != 0).then(|| 1.0 + 4.0 * ((code - 1) as f32 / 254.0));

        use spark_lib::tsplat::TsplatMut;

        let mut s = make_splat(7);
        // encode_lod_opacity maps D 1..5 onto opacity 1..2. Opacity is stored
        // as f16, so allow for that on the way in as well as the unorm8 out.
        for d in [1.1f32, 1.5, 2.75, 4.0, 5.0] {
            (&mut s).set_opacity((d + 3.0) / 4.0);
            assert!((lod_opacity(&&s).unwrap() - d).abs() < 1e-2, "D={d}");

            let (rec, _, _) = quantize_record(&&s);
            let code = rec[26];
            assert_ne!(code, 0, "D={d} must not read back as a leaf");
            let back = decode(code).unwrap();
            assert!(
                (back - d).abs() <= 4.0 / 254.0 + 1e-2,
                "D={d} decoded {back}"
            );
            assert!((lod_std_dev(Some(back)) - lod_std_dev(Some(d))).abs() < 0.02);
        }

        // A leaf stays a leaf and keeps the base extent.
        (&mut s).set_opacity(0.6);
        assert_eq!(lod_opacity(&&s), None);
        assert_eq!(quantize_record(&&s).0[26], 0);
        assert_eq!(lod_std_dev(None), MAX_STD_DEV);
    }

    #[test]
    fn unorm16_extremes() {
        assert_eq!(encode_unorm16(-1.0), 0);
        assert_eq!(encode_unorm16(1.0), 65535);
        assert!((decode_unorm16(encode_unorm16(0.0))).abs() < 1.0e-4);
    }

    /// Builds a random tree via the real tiny_lod pipeline and checks the
    /// §4.4 structural invariants of assign_indices.
    #[test]
    fn dfs_layout_invariants() {
        let mut splats = GsplatArray::new_capacity(0, 0);
        for i in 0..5000u64 {
            splats.splats.push(make_splat(i));
        }
        spark_lib::tiny_lod::compute_lod_tree(&mut splats, 1.5, false, |_| {});
        spark_lib::chunk_tree::chunk_tree(&mut splats, 0, |_| {});

        let layout = assign_indices(&splats, 0, 8192).unwrap();
        let n = splats.len();

        // order is a permutation of 0..n
        let mut seen = vec![false; n];
        for &o in &layout.order {
            assert!(!seen[o as usize], "duplicate index in order");
            seen[o as usize] = true;
        }

        // node count + leaves == total
        assert_eq!(layout.nodes.len() as u64 + layout.leaf_splats, n as u64);

        // Per-node invariants
        for (slot, node) in layout.nodes.iter().enumerate() {
            // merged splat maps back to the node's original index
            assert_eq!(
                layout.order[node.merged_global as usize] as usize,
                node.orig
            );

            let children = splats.get_children(node.orig);
            let mut leaves: Vec<usize> = Vec::new();
            let mut interiors: Vec<usize> = Vec::new();
            for &c in children.iter() {
                if splats.get_child_count_start(c).0 == 0 {
                    leaves.push(c);
                } else {
                    interiors.push(c);
                }
            }
            assert_eq!(node.direct_leaf_count as usize, leaves.len());
            if !leaves.is_empty() {
                for (k, &l) in leaves.iter().enumerate() {
                    assert_eq!(
                        layout.order[(node.direct_leaf_first + k as u32) as usize] as usize,
                        l,
                        "direct leaves not contiguous for node {slot}"
                    );
                }
                // Note: leaves need not follow the merged splat — region-root
                // mergeds are hoisted into the backbone (hybrid ordering).
            }
            assert_eq!(node.interior_count as usize, interiors.len());
            for (k, &c) in interiors.iter().enumerate() {
                let child = &layout.nodes[(node.first_interior + k as u32) as usize];
                assert_eq!(
                    child.orig, c,
                    "interior children not contiguous for node {slot}"
                );
                // Parent slot precedes child slots (bottom-up radius pass relies on it).
                assert!((node.first_interior as usize) > slot);
                // Child subtree head comes after parent's merged splat.
                assert!(child.merged_global > node.merged_global);
            }
        }
    }
}
