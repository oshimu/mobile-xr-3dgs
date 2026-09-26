use std::path::PathBuf;
use std::process::ExitCode;

use build_lod_unity::{
    coord, run_pipeline, InputError, InputSource, LodMethod, PipelineOptions, ValidationError,
};

fn show_usage() {
    eprintln!("Usage: build-lod-unity <input> [options]");
    eprintln!();
    eprintln!("  --out <dir>            output directory (default: input's directory)");
    eprintln!("  --name <base>          output base name (default: input stem)");
    eprintln!("  --quality              use bhatt-lod (default: tiny-lod)");
    eprintln!("  --lod-base <f32>       LoD base (default: 1.5 tiny / 1.75 bhatt)");
    eprintln!("  --max-sh <0..3>        max SH degree; format v1 accepts 0 only (default 0)");
    eprintln!("  --coord <preset>       identity | flip-x | flip-y | flip-z (default: flip-x,");
    eprintln!(
        "                         right-handed 3DGS/PLY -> left-handed Unity, un-mirrors L/R)"
    );
    eprintln!("  --region-splats <n>    region granularity for chunk layout (default 8192;");
    eprintln!("                         smaller = richer always-resident coarse view)");
    eprintln!("  --force                overwrite existing output files");
    eprintln!();
    eprintln!("  input: .ply / .compressed.ply / .spz / .splat / .ksplat / .sog / .zip");
    eprintln!("  exit code: 0=ok / 1=input error / 2=internal error / 3=self-validation failure");
}

fn main() -> ExitCode {
    #[cfg(debug_assertions)]
    eprintln!("warning: debug build — LoD construction is impractically slow; use --release");

    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        show_usage();
        return ExitCode::from(1);
    }

    let mut input: Option<PathBuf> = None;
    let mut opts = PipelineOptions {
        input: InputSource::Path(PathBuf::new()),
        out_dir: None,
        name: None,
        method: LodMethod::Tiny,
        lod_base: None,
        max_sh: 0,
        coord: coord::CoordPreset::FlipX,
        force: false,
        region_splats: None,
    };

    let mut it = args.into_iter();
    while let Some(arg) = it.next() {
        match arg.as_str() {
            "--out" => match it.next() {
                Some(v) => opts.out_dir = Some(PathBuf::from(v)),
                None => return usage_error("--out requires a value"),
            },
            "--name" => match it.next() {
                Some(v) => opts.name = Some(v),
                None => return usage_error("--name requires a value"),
            },
            "--quality" => opts.method = LodMethod::Bhatt,
            "--lod-base" => match it.next().and_then(|v| v.parse::<f32>().ok()) {
                Some(v) => opts.lod_base = Some(v.clamp(1.1, 16.0)),
                None => return usage_error("--lod-base requires a float value"),
            },
            "--max-sh" => match it.next().and_then(|v| v.parse::<usize>().ok()) {
                Some(v) => opts.max_sh = v,
                None => return usage_error("--max-sh requires an integer value"),
            },
            "--coord" => match it.next().as_deref().and_then(coord::parse_preset) {
                Some(p) => opts.coord = p,
                None => {
                    return usage_error("--coord: unknown preset (identity|flip-x|flip-y|flip-z)")
                }
            },
            "--region-splats" => match it.next().and_then(|v| v.parse::<usize>().ok()) {
                Some(v) => opts.region_splats = Some(v),
                None => return usage_error("--region-splats requires an integer value"),
            },
            "--force" => opts.force = true,
            "--help" | "-h" => {
                show_usage();
                return ExitCode::SUCCESS;
            }
            s if s.starts_with("--") => return usage_error(&format!("unknown option: {s}")),
            _ => {
                if input.is_some() {
                    return usage_error("multiple input files are not supported");
                }
                input = Some(PathBuf::from(arg));
            }
        }
    }

    let Some(input) = input else {
        return usage_error("no input file given");
    };
    opts.input = InputSource::Path(input);

    match run_pipeline(&opts, |s| println!("{s}")) {
        Ok(_) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("error: {e}");
            if e.downcast_ref::<InputError>().is_some() {
                ExitCode::from(1)
            } else if e.downcast_ref::<ValidationError>().is_some() {
                ExitCode::from(3)
            } else {
                ExitCode::from(2)
            }
        }
    }
}

fn usage_error(msg: &str) -> ExitCode {
    eprintln!("error: {msg}");
    show_usage();
    ExitCode::from(1)
}
