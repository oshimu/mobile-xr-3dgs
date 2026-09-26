//! build-lod-unity: converts 3DGS input files (.ply/.spz/...) into the Unity
//! streaming format (.usst + .usc chunks) defined in docs/spec.md.
//!
//! Pipeline: decode (spark-lib) -> LoD tree (tiny_lod/bhatt_lod) -> chunk_tree
//! -> coordinate preset bake -> UnityEncoder (own DFS re-indexing) -> validate.

use std::fs::File;
use std::io::{BufReader, Read};
use std::path::{Path, PathBuf};

use spark_lib::decoder::{ChunkReceiver, MultiDecoder};
use spark_lib::gsplat::GsplatArray;
use spark_lib::tsplat::{Tsplat, TsplatArray};
use spark_lib::{bhatt_lod, chunk_tree, tiny_lod};

pub mod coord;
pub mod unity_encoder;
pub mod validate;

pub const VENDORED_COMMIT: &str = include_str!("../../VENDORED_COMMIT");

/// Error wrapper used to map failures to CLI exit codes (1 = input error).
#[derive(Debug)]
pub struct InputError(pub String);
impl std::fmt::Display for InputError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "input error: {}", self.0)
    }
}
impl std::error::Error for InputError {}

/// Error wrapper used to map failures to CLI exit codes (3 = validation failure).
#[derive(Debug)]
pub struct ValidationError(pub String);
impl std::fmt::Display for ValidationError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "validation failed: {}", self.0)
    }
}
impl std::error::Error for ValidationError {}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LodMethod {
    Tiny,
    Bhatt,
}

impl LodMethod {
    pub fn name(&self) -> &'static str {
        match self {
            LodMethod::Tiny => "tiny",
            LodMethod::Bhatt => "bhatt",
        }
    }

    /// Defaults match the vendored build-lod CLI (--quick / --quality).
    pub fn default_lod_base(&self) -> f32 {
        match self {
            LodMethod::Tiny => 1.5,
            LodMethod::Bhatt => 1.75,
        }
    }
}

/// Where the pipeline reads its input from. `Path` is the original,
/// still-default mode (CLI and, on Unity's desktop path, a plain filesystem
/// path). `Fd` is used from Unity/Android via the SAF file picker, which only
/// hands back a POSIX file descriptor (no filesystem path) plus the picked
/// document's display name — the display name stands in for a pathname
/// everywhere format detection or a stem/name default would otherwise use
/// the input path's extension/file name.
#[derive(Debug, Clone)]
pub enum InputSource {
    Path(PathBuf),
    /// `fd` is owned by the pipeline once this is constructed: it is always
    /// closed (via `File`'s `Drop`) by the time `run_pipeline` returns,
    /// success or failure. Unix only — see `open_reader` below.
    Fd { fd: i32, display_name: String },
}

impl InputSource {
    /// Human-readable form for logging and the manifest's `source` field.
    /// Not necessarily a real filesystem path for `Fd`.
    fn display_string(&self) -> String {
        match self {
            InputSource::Path(p) => p.display().to_string(),
            InputSource::Fd { display_name, .. } => display_name.clone(),
        }
    }

    /// Stand-in pathname used for format detection (`MultiDecoder`'s
    /// extension sniffing) — for `Fd`, the fd itself has no extension, so
    /// the picked document's display name is used instead.
    fn decoder_pathname(&self) -> String {
        match self {
            InputSource::Path(p) => p.to_string_lossy().into_owned(),
            InputSource::Fd { display_name, .. } => display_name.clone(),
        }
    }

    /// Default output base name (`--name`/`name` unset): the input's file
    /// stem, taken from the real path or (for `Fd`) from the display name.
    fn name_stem(&self) -> String {
        let stem_of = |p: &Path| {
            p.file_stem()
                .map(|s| s.to_string_lossy().into_owned())
                .unwrap_or_else(|| "scene".to_string())
        };
        match self {
            InputSource::Path(p) => stem_of(p),
            InputSource::Fd { display_name, .. } => stem_of(Path::new(display_name)),
        }
    }

    /// Default output directory (`--out`/`outDir` unset): the input's parent
    /// directory. `Fd` has no filesystem path to derive one from, so callers
    /// that pick fd input are expected to always supply an explicit out_dir
    /// (Unity's ConvertPanel does); "." is a last-resort fallback only.
    fn default_out_dir(&self) -> PathBuf {
        match self {
            InputSource::Path(p) => p
                .parent()
                .unwrap_or_else(|| Path::new("."))
                .to_path_buf(),
            InputSource::Fd { .. } => PathBuf::from("."),
        }
    }

    /// Opens the input for reading. For `Fd`, this takes ownership of the
    /// descriptor: whatever the caller does with the returned `File`, it is
    /// closed exactly once when that `File` is dropped (including during a
    /// panic unwind), so the caller must not close `fd` itself.
    fn open_reader(&self) -> anyhow::Result<File> {
        match self {
            InputSource::Path(p) => Ok(File::open(p)?),
            #[cfg(unix)]
            InputSource::Fd { fd, .. } => {
                use std::os::fd::FromRawFd;
                // SAFETY: caller contract (see the `Fd` variant's doc
                // comment) is that `fd` is a valid, open, otherwise-unowned
                // POSIX file descriptor handed off to us exactly once.
                Ok(unsafe { File::from_raw_fd(*fd) })
            }
            #[cfg(not(unix))]
            InputSource::Fd { .. } => {
                Err(anyhow::anyhow!(
                    "fd input is not supported on this platform (Windows/other non-Unix build)"
                ))
            }
        }
    }
}

#[derive(Debug, Clone)]
pub struct PipelineOptions {
    pub input: InputSource,
    pub out_dir: Option<PathBuf>,
    pub name: Option<String>,
    pub method: LodMethod,
    pub lod_base: Option<f32>,
    pub max_sh: usize,
    pub coord: coord::CoordPreset,
    pub force: bool,
    pub region_splats: Option<usize>,
}

fn read_file_chunks(
    reader: impl Read,
    decoder: &mut impl ChunkReceiver,
) -> anyhow::Result<()> {
    const CHUNK_SIZE: usize = 1024 * 1024;
    let mut reader = BufReader::new(reader);
    let mut buffer = vec![0u8; CHUNK_SIZE];
    loop {
        let bytes_read = reader.read(&mut buffer)?;
        if bytes_read == 0 {
            break;
        }
        decoder.push(&buffer[..bytes_read])?;
    }
    decoder.finish()
}

pub fn run_pipeline(
    opts: &PipelineOptions,
    log: impl Fn(&str),
) -> anyhow::Result<serde_json::Value> {
    if opts.max_sh != 0 {
        return Err(InputError(format!(
            "--max-sh {} is not supported by format v1 (SH0 only)",
            opts.max_sh
        ))
        .into());
    }

    let input = &opts.input;
    if let InputSource::Path(p) = input {
        if !p.is_file() {
            return Err(InputError(format!("input file not found: {}", p.display())).into());
        }
    }

    let out_dir = opts
        .out_dir
        .clone()
        .unwrap_or_else(|| input.default_out_dir());
    let name = opts.name.clone().unwrap_or_else(|| input.name_stem());

    std::fs::create_dir_all(&out_dir)?;
    let usst_path = out_dir.join(format!("{}.usst", name));
    if usst_path.exists() && !opts.force {
        return Err(InputError(format!(
            "output already exists (use --force): {}",
            usst_path.display()
        ))
        .into());
    }

    // 1. Decode input.
    log(&format!("decoding {}", input.display_string()));
    let splats = GsplatArray::new();
    let decoder_pathname = input.decoder_pathname();
    let mut decoder = MultiDecoder::new(splats, None, Some(&decoder_pathname));
    let decode_result = input
        .open_reader()
        .and_then(|reader| read_file_chunks(reader, &mut decoder));
    if let Err(e) = decode_result {
        return Err(InputError(format!("decoding failed: {e:?}")).into());
    }
    let mut splats = decoder.into_splats();
    let input_splats = splats.len();
    log(&format!(
        "decoded {} splats, sh_degree={}",
        input_splats,
        TsplatArray::max_sh_degree(&splats)
    ));
    if input_splats == 0 {
        return Err(InputError("input contains no splats".to_string()).into());
    }

    // 2. SH clamp (v1 format is SH0 only).
    splats.clamp_sh_degree(opts.max_sh);

    // 3. Drop invalid splats (same criteria as build-lod).
    splats.remove_invalid();
    let mut non_finite = 0usize;
    splats.retain(|s| {
        let ok = s.center().is_finite()
            && s.scales().is_finite()
            && s.quaternion().is_finite()
            && s.opacity().is_finite()
            && s.rgb().is_finite();
        if !ok {
            non_finite += 1;
        }
        ok
    });
    if splats.len() != input_splats {
        log(&format!(
            "removed {} invalid splats ({} non-finite), remaining {}",
            input_splats - splats.len(),
            non_finite,
            splats.len()
        ));
    }
    if splats.len() < 2 {
        return Err(
            InputError("need at least 2 valid splats to build a LoD tree".to_string()).into(),
        );
    }

    // 4. LoD tree.
    let lod_base = opts
        .lod_base
        .unwrap_or_else(|| opts.method.default_lod_base());
    let start = std::time::Instant::now();
    match opts.method {
        LodMethod::Tiny => {
            // merge_filter=false matches vendored build-lod's default invocation.
            tiny_lod::compute_lod_tree(&mut splats, lod_base, false, |s| log(s));
        }
        LodMethod::Bhatt => {
            bhatt_lod::compute_lod_tree(&mut splats, lod_base, |s| log(s));
        }
    }
    log(&format!(
        "lod tree built in {:.1}s, total splats {}",
        start.elapsed().as_secs_f64(),
        splats.len()
    ));

    // 5. Spatial reordering (root ends up at index 0).
    let start = std::time::Instant::now();
    chunk_tree::chunk_tree(&mut splats, 0, |s| log(s));
    log(&format!(
        "chunk_tree done in {:.1}s",
        start.elapsed().as_secs_f64()
    ));

    // 6. Coordinate preset bake.
    coord::apply(&mut splats, opts.coord);

    // 7. Encode.
    let enc_opts = unity_encoder::EncodeOptions {
        out_dir: out_dir.clone(),
        name: name.clone(),
        source: input.display_string(),
        lod_method: opts.method.name().to_string(),
        lod_base,
        max_sh: opts.max_sh,
        coord_preset: coord::preset_name(opts.coord).to_string(),
        input_splats,
        region_splats: opts
            .region_splats
            .unwrap_or(unity_encoder::DEFAULT_REGION_SPLATS),
    };
    let manifest = unity_encoder::encode(&mut splats, &enc_opts, &log)?;
    drop(splats);

    // 8. Self-validation.
    log("validating output");
    validate::validate(&out_dir, &name, &log)?;
    log("validation passed");

    Ok(manifest)
}
