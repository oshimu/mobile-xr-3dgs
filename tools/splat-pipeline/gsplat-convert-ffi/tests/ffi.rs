//! Integration tests for the C ABI in `gsplat_convert::*`, exercised as plain
//! Rust calls (this crate builds an rlib in addition to the cdylib, so no
//! dlopen is needed to test it).

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::time::{Duration, Instant};

// Synthetic PLY generator copied from build-lod-unity/tests/e2e.rs (spec
// asks to reuse the existing generation pattern rather than inventing a new
// one).
fn lcg(state: &mut u64) -> f32 {
    *state = state
        .wrapping_mul(6364136223846793005)
        .wrapping_add(1442695040888963407);
    ((*state >> 33) as u32) as f32 / u32::MAX as f32
}

fn write_synthetic_ply(path: &std::path::Path, count: usize) {
    use std::io::Write;

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
        let vals: [f32; 14] = [
            lcg(&mut rng) * 20.0 - 10.0,
            lcg(&mut rng) * 20.0 - 10.0,
            lcg(&mut rng) * 20.0 - 10.0,
            lcg(&mut rng) * 2.0 - 1.0,
            lcg(&mut rng) * 2.0 - 1.0,
            lcg(&mut rng) * 2.0 - 1.0,
            lcg(&mut rng) * 6.0 - 2.0,
            (0.005 + lcg(&mut rng) * 0.1).ln(),
            (0.005 + lcg(&mut rng) * 0.1).ln(),
            (0.005 + lcg(&mut rng) * 0.1).ln(),
            lcg(&mut rng) - 0.5,
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

fn options_json(input: &std::path::Path, out_dir: &std::path::Path, name: &str) -> CString {
    let json = serde_json::json!({
        "input": input.to_string_lossy(),
        "outDir": out_dir.to_string_lossy(),
        "name": name,
        "quality": false,
        "lodBase": 0.0,
        "maxSh": 0,
        "coord": "identity",
        "regionSplats": 0,
        "force": true,
    });
    CString::new(json.to_string()).unwrap()
}

#[test]
fn start_poll_success() {
    unsafe {
        let dir = std::env::temp_dir().join("gsplat-convert-ffi-success");
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let ply = dir.join("synthetic.ply");
        write_synthetic_ply(&ply, 4000);

        let opts = options_json(&ply, &dir, "synthetic");
        let handle = gsplat_convert::gsplat_convert_start(opts.as_ptr());
        assert!(!handle.is_null(), "start should succeed with valid options");

        let mut buf = [0 as c_char; 4096];
        let deadline = Instant::now() + Duration::from_secs(120);
        let mut status;
        loop {
            status =
                gsplat_convert::gsplat_convert_poll(handle, buf.as_mut_ptr(), buf.len() as i32);
            if status != 0 {
                break;
            }
            assert!(Instant::now() < deadline, "pipeline did not finish in time");
            std::thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(status, 1, "expected success status");

        // Drain any remaining buffered log lines.
        loop {
            let s = gsplat_convert::gsplat_convert_poll(handle, buf.as_mut_ptr(), buf.len() as i32);
            let text = CStr::from_ptr(buf.as_ptr()).to_str().unwrap();
            if text.is_empty() {
                break;
            }
            assert_eq!(s, 1);
        }

        assert!(dir.join("synthetic.usst").is_file());

        let result_ptr = gsplat_convert::gsplat_convert_result_json(handle);
        assert!(!result_ptr.is_null());
        let result_str = CStr::from_ptr(result_ptr).to_str().unwrap();
        let manifest: serde_json::Value =
            serde_json::from_str(result_str).expect("result_json should be the manifest JSON");
        assert!(manifest["stats"].is_object());

        build_lod_unity::validate::validate(&dir, "synthetic", &|_| {})
            .expect("output should self-validate");

        gsplat_convert::gsplat_convert_free(handle);
    }
}

#[test]
fn repeated_failure_preserves_previous_output() {
    unsafe {
        let dir = std::env::temp_dir().join("gsplat-convert-ffi-preserve");
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let ply = dir.join("synthetic.ply");
        write_synthetic_ply(&ply, 4000);

        // 1. A successful conversion establishes real output at this
        // out_dir/name.
        let opts_ok = options_json(&ply, &dir, "synthetic");
        let handle = gsplat_convert::gsplat_convert_start(opts_ok.as_ptr());
        assert!(!handle.is_null(), "first (successful) start should succeed");

        let mut buf = [0 as c_char; 4096];
        let deadline = Instant::now() + Duration::from_secs(120);
        let mut status;
        loop {
            status =
                gsplat_convert::gsplat_convert_poll(handle, buf.as_mut_ptr(), buf.len() as i32);
            if status != 0 {
                break;
            }
            assert!(
                Instant::now() < deadline,
                "first conversion did not finish in time"
            );
            std::thread::sleep(Duration::from_millis(10));
        }
        assert_eq!(status, 1, "first conversion should succeed");
        gsplat_convert::gsplat_convert_free(handle);

        let usst_path = dir.join("synthetic.usst");
        let manifest_path = dir.join("synthetic.manifest.json");
        assert!(usst_path.is_file());
        assert!(manifest_path.is_file());
        let original_usst = std::fs::read(&usst_path).unwrap();
        assert!(!original_usst.is_empty());

        // 2. A second run at the SAME out_dir/name that is rejected before
        // writing a single byte (max_sh=1 is not supported by format v1 —
        // see build-lod-unity::run_pipeline's very first check) must not
        // delete the first run's output. This is the A-1 regression: the old
        // cleanup unconditionally deleted `{name}.usst`/`.manifest.json`/
        // `{name}_*.usc` in out_dir on any failure, including ones that never
        // wrote anything themselves.
        let bad_json = serde_json::json!({
            "input": ply.to_string_lossy(),
            "outDir": dir.to_string_lossy(),
            "name": "synthetic",
            "quality": false,
            "lodBase": 0.0,
            "maxSh": 1,
            "coord": "identity",
            "regionSplats": 0,
            "force": true,
        });
        let opts_bad = CString::new(bad_json.to_string()).unwrap();
        let handle2 = gsplat_convert::gsplat_convert_start(opts_bad.as_ptr());
        assert!(
            !handle2.is_null(),
            "second (deliberately failing) start should still spawn"
        );

        let deadline2 = Instant::now() + Duration::from_secs(30);
        let mut status2;
        loop {
            status2 =
                gsplat_convert::gsplat_convert_poll(handle2, buf.as_mut_ptr(), buf.len() as i32);
            if status2 != 0 {
                break;
            }
            assert!(
                Instant::now() < deadline2,
                "second conversion did not fail in time"
            );
            std::thread::sleep(Duration::from_millis(5));
        }
        assert_eq!(status2, 2, "max_sh=1 should be rejected as an error");
        gsplat_convert::gsplat_convert_free(handle2);

        // The first run's output must be untouched.
        assert!(
            usst_path.is_file(),
            "A-1 regression: the previous successful .usst was deleted by the failing run's cleanup"
        );
        assert!(
            manifest_path.is_file(),
            "A-1 regression: the previous successful .manifest.json was deleted"
        );
        assert_eq!(
            std::fs::read(&usst_path).unwrap(),
            original_usst,
            "previous .usst content changed"
        );
    }
}

#[test]
fn poll_with_tiny_buffer_still_completes() {
    unsafe {
        let dir = std::env::temp_dir().join("gsplat-convert-ffi-tinybuf");
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let ply = dir.join("synthetic.ply");
        write_synthetic_ply(&ply, 4000);

        let opts = options_json(&ply, &dir, "synthetic");
        let handle = gsplat_convert::gsplat_convert_start(opts.as_ptr());
        assert!(!handle.is_null());

        // Deliberately too small to hold even one typical log line whole
        // (this crate's log lines include full file paths). B-2 regression:
        // the old poll_impl just `break`d when the front line didn't fit,
        // permanently jamming log_lines (and the job would still finish, but
        // a caller draining the queue every frame would never see status
        // flip because... actually status is independent of the queue, but
        // real callers like ConvertJob rely on draining to not build up
        // unbounded memory / to see progress) — the fix truncates the
        // oversized line instead of getting stuck on it.
        let mut buf = [0 as c_char; 16];
        let deadline = Instant::now() + Duration::from_secs(120);
        let mut status;
        loop {
            status =
                gsplat_convert::gsplat_convert_poll(handle, buf.as_mut_ptr(), buf.len() as i32);
            if status != 0 {
                break;
            }
            assert!(
                Instant::now() < deadline,
                "pipeline did not progress to completion with a tiny poll buffer"
            );
            std::thread::sleep(Duration::from_millis(5));
        }
        assert_eq!(
            status, 1,
            "expected success status even with a tiny poll buffer"
        );

        gsplat_convert::gsplat_convert_free(handle);
    }
}

#[test]
fn start_cancel() {
    unsafe {
        let dir = std::env::temp_dir().join("gsplat-convert-ffi-cancel");
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let ply = dir.join("synthetic.ply");
        // Large enough to give the LoD builder several levels (and thus
        // several log calls, each a cancellation checkpoint) before it would
        // finish.
        write_synthetic_ply(&ply, 60_000);

        let opts = options_json(&ply, &dir, "synthetic");
        let handle = gsplat_convert::gsplat_convert_start(opts.as_ptr());
        assert!(!handle.is_null());

        gsplat_convert::gsplat_convert_cancel(handle);

        let mut buf = [0 as c_char; 256];
        let deadline = Instant::now() + Duration::from_secs(120);
        let mut status;
        loop {
            status =
                gsplat_convert::gsplat_convert_poll(handle, buf.as_mut_ptr(), buf.len() as i32);
            if status != 0 {
                break;
            }
            assert!(
                Instant::now() < deadline,
                "cancel did not take effect in time"
            );
            std::thread::sleep(Duration::from_millis(5));
        }
        assert_eq!(status, 2, "cancellation should surface as an error status");

        // No completed output should remain (cleanup removed anything partial).
        assert!(!dir.join("synthetic.usst").is_file());
        assert!(!dir.join("synthetic.manifest.json").is_file());
        let has_usc = std::fs::read_dir(&dir)
            .unwrap()
            .flatten()
            .any(|e| e.file_name().to_string_lossy().ends_with(".usc"));
        assert!(!has_usc, "no chunk files should remain after cancellation");

        gsplat_convert::gsplat_convert_free(handle);
    }
}
