//! E2E test (spec §4.9): procedurally generate a 100k-splat 3DGS ply, run the
//! full pipeline, and require self-validation to pass.
//! Run with `cargo test --release` — LoD construction is slow in debug.

use std::io::Write;

fn lcg(state: &mut u64) -> f32 {
    *state = state
        .wrapping_mul(6364136223846793005)
        .wrapping_add(1442695040888963407);
    ((*state >> 33) as u32) as f32 / u32::MAX as f32
}

fn write_synthetic_ply(path: &std::path::Path, count: usize) {
    let mut header = String::new();
    header.push_str("ply\nformat binary_little_endian 1.0\n");
    header.push_str(&format!("element vertex {count}\n"));
    for p in [
        "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2",
        "rot_0", "rot_1", "rot_2", "rot_3",
    ] {
        header.push_str(&format!("property float {p}\n"));
    }
    header.push_str("end_header\n");

    let mut w = std::io::BufWriter::new(std::fs::File::create(path).unwrap());
    w.write_all(header.as_bytes()).unwrap();

    let mut rng = 0x1234_5678_9abc_def0u64;
    for _ in 0..count {
        // Gaussian-ish blob cloud in a 20m box.
        let vals: [f32; 14] = [
            lcg(&mut rng) * 20.0 - 10.0,
            lcg(&mut rng) * 20.0 - 10.0,
            lcg(&mut rng) * 20.0 - 10.0,
            lcg(&mut rng) * 2.0 - 1.0, // f_dc (SH DC coefficients)
            lcg(&mut rng) * 2.0 - 1.0,
            lcg(&mut rng) * 2.0 - 1.0,
            lcg(&mut rng) * 6.0 - 2.0,          // opacity logit
            (0.005 + lcg(&mut rng) * 0.1).ln(), // ln scales
            (0.005 + lcg(&mut rng) * 0.1).ln(),
            (0.005 + lcg(&mut rng) * 0.1).ln(),
            lcg(&mut rng) - 0.5, // rot_0 = w
            lcg(&mut rng) - 0.5,
            lcg(&mut rng) - 0.5,
            lcg(&mut rng) - 0.5,
        ];
        for v in vals {
            w.write_all(&v.to_le_bytes()).unwrap();
        }
    }
    w.flush().unwrap();
}

#[test]
fn e2e_synthetic_100k() {
    let dir = std::env::temp_dir().join("build-lod-unity-e2e");
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    let ply = dir.join("synthetic.ply");
    write_synthetic_ply(&ply, 100_000);

    let opts = build_lod_unity::PipelineOptions {
        input: build_lod_unity::InputSource::Path(ply),
        out_dir: Some(dir.clone()),
        name: Some("synthetic".to_string()),
        method: build_lod_unity::LodMethod::Tiny,
        lod_base: None,
        max_sh: 0,
        coord: build_lod_unity::coord::CoordPreset::Identity,
        force: true,
        region_splats: None,
    };
    let manifest = build_lod_unity::run_pipeline(&opts, |_| {}).expect("pipeline should succeed");

    // Manifest stats consistent with the input.
    let stats = &manifest["stats"];
    assert_eq!(stats["inputSplats"].as_u64().unwrap(), 100_000);
    let leaf = stats["leafSplats"].as_u64().unwrap();
    let interior = stats["interiorNodes"].as_u64().unwrap();
    let total = stats["totalSplats"].as_u64().unwrap();
    assert!(leaf <= 100_000 && leaf > 0);
    assert_eq!(leaf + interior, total);
    assert!(stats["chunkCount"].as_u64().unwrap() == total.div_ceil(65536));

    // Output files exist.
    assert!(dir.join("synthetic.usst").is_file());
    assert!(dir.join("synthetic_0000.usc").is_file());
    assert!(dir.join("synthetic.manifest.json").is_file());
}
