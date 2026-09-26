//! C ABI wrapper around `build-lod-unity::run_pipeline`, for Unity P/Invoke.
//!
//! Polling model (no callbacks across the FFI boundary):
//!   start -> poll (loop, draining log lines) -> result_json -> free
//!
//! The conversion runs on a dedicated worker thread with a 16 MiB stack. The
//! deepest real recursion in the pipeline is the quality (bhatt-lod) path's
//! `recurse_to_output` / `recurse_indices` (spark-lib/src/bhatt_lod.rs:151,
//! 197), which is deep enough to overflow the default 2 MiB thread stack
//! (SIGSEGV instead of unwinding). The default (tiny-lod) path's
//! `chunk_tree::chunk_tree` (spark-lib/src/chunk_tree.rs:653-655) is
//! iterative and does not need this, but the stack is sized for the worst
//! case (quality=true) regardless of which method a given job uses.
//!
//! Cancellation is cooperative: `gsplat_convert_cancel` sets an atomic flag
//! that the log callback passed into `run_pipeline` checks on every log line;
//! when set, it panics (after dropping the log mutex guard), which
//! `catch_unwind` on the worker thread turns into an error result.
//!
//! `gsplat_convert_free` detaches rather than joins the worker thread — see
//! its doc comment for why and for the use-after-free argument. The state
//! shared between the worker and the caller lives in an `Arc<ConvertState>`
//! rather than being gated on a `JoinHandle`.

use std::collections::{HashSet, VecDeque};
use std::ffi::{CStr, CString};
use std::os::raw::{c_char, c_int};
use std::panic::{self, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use build_lod_unity::{coord, InputSource, LodMethod, PipelineOptions};
use serde::Deserialize;

const HANDLE_MAGIC: u32 = 0x4753_5043; // "GSPC"
const WORKER_STACK_SIZE: usize = 16 * 1024 * 1024;

/// JSON schema accepted by `gsplat_convert_start`. All fields are required
/// (matches what Unity's `JsonUtility.ToJson` emits: camelCase, no Option),
/// except `inputFd`/`displayName` which are new and defaulted so JSON
/// produced before their addition still parses.
/// Sentinel values map to `PipelineOptions`' `Option` fields:
///   - `lodBase <= 0`      -> `None` (use the method's default)
///   - `regionSplats <= 0` -> `None` (use the encoder's default)
///   - `outDir`/`name` empty -> `None` (derive from the input path)
///   - `inputFd < 0`       -> not fd input; `input` (path) is used instead
///
/// `input` and `inputFd` are mutually exclusive: exactly one of "`input` is
/// non-empty" / "`inputFd` is >= 0" must hold, or `into_pipeline_options`
/// returns `None`. `displayName` is required (non-empty) when `inputFd` is
/// used — it stands in for a pathname for format detection and default
/// naming, since an fd has neither.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RawOptions {
    input: String,
    #[serde(default = "default_input_fd")]
    input_fd: i64,
    #[serde(default)]
    display_name: String,
    out_dir: String,
    name: String,
    quality: bool,
    lod_base: f32,
    max_sh: usize,
    coord: String,
    region_splats: i64,
    force: bool,
}

fn default_input_fd() -> i64 {
    -1
}

impl RawOptions {
    fn into_pipeline_options(self) -> Option<PipelineOptions> {
        let coord = coord::parse_preset(&self.coord)?;

        let has_path = !self.input.is_empty();
        let has_fd = self.input_fd >= 0;
        let input = match (has_path, has_fd) {
            (true, false) => InputSource::Path(PathBuf::from(self.input)),
            (false, true) => {
                if self.display_name.is_empty() {
                    return None; // displayName is required for fd input
                }
                InputSource::Fd {
                    fd: self.input_fd as i32,
                    display_name: self.display_name,
                }
            }
            // Neither or both given: ambiguous/invalid.
            _ => return None,
        };

        Some(PipelineOptions {
            input,
            out_dir: if self.out_dir.is_empty() {
                None
            } else {
                Some(PathBuf::from(self.out_dir))
            },
            name: if self.name.is_empty() {
                None
            } else {
                Some(self.name)
            },
            method: if self.quality {
                LodMethod::Bhatt
            } else {
                LodMethod::Tiny
            },
            lod_base: if self.lod_base > 0.0 {
                Some(self.lod_base)
            } else {
                None
            },
            max_sh: self.max_sh,
            coord,
            force: self.force,
            region_splats: if self.region_splats > 0 {
                Some(self.region_splats as usize)
            } else {
                None
            },
        })
    }
}

/// State shared between the worker thread and the polling caller.
struct Shared {
    log_lines: VecDeque<String>,
    /// `None` while running; set exactly once when the worker finishes.
    result: Option<Result<String, String>>,
}

/// State kept alive by whichever of (handle, worker thread) drops last.
///
/// `gsplat_convert_start` creates one `Arc<ConvertState>` and gives one clone
/// to the worker thread and one to the `Handle` it returns. `gsplat_convert_free`
/// drops the handle's clone and returns without waiting on the worker; the
/// worker drops its own clone when `worker()` returns. The underlying memory
/// is only deallocated once both clones are gone, so neither side can ever
/// observe it half-freed.
struct ConvertState {
    shared: Mutex<Shared>,
    cancel: AtomicBool,
}

/// Opaque handle returned to the caller. Guarded by a magic value so that a
/// stray/garbage pointer is rejected rather than blindly dereferenced. All
/// fields stay private, so callers (including Rust callers of this crate's
/// rlib) can only hold and pass around `*mut Handle`, never construct or
/// inspect one directly.
pub struct Handle {
    magic: u32,
    state: Arc<ConvertState>,
    /// Cached C string for `result_json`, so the returned pointer stays
    /// valid for the handle's lifetime rather than only until the next call.
    cached_result: Mutex<Option<CString>>,
}

/// # Safety
/// `h` must either be null or a pointer previously returned by
/// `gsplat_convert_start` and not yet passed to `gsplat_convert_free`.
unsafe fn handle_ref(h: *mut Handle) -> Option<&'static Handle> {
    if h.is_null() {
        return None;
    }
    let r = &*h;
    if r.magic != HANDLE_MAGIC {
        return None;
    }
    Some(r)
}

fn resolve_out_dir_name(opts: &PipelineOptions) -> (PathBuf, String) {
    // Mirrors the default resolution inside `run_pipeline` (build-lod-unity's
    // src/lib.rs), which is not otherwise observable from the outside when
    // the pipeline fails before producing a manifest. `PipelineOptions`
    // doesn't expose its `InputSource` defaulting helpers, so this only
    // needs to cover the two cases actually reachable here.
    let out_dir = opts.out_dir.clone().unwrap_or_else(|| match &opts.input {
        InputSource::Path(p) => p.parent().unwrap_or_else(|| Path::new(".")).to_path_buf(),
        InputSource::Fd { .. } => PathBuf::from("."),
    });
    let name = opts.name.clone().unwrap_or_else(|| {
        let stem_of = |p: &Path| {
            p.file_stem()
                .map(|s| s.to_string_lossy().into_owned())
                .unwrap_or_else(|| "scene".to_string())
        };
        match &opts.input {
            InputSource::Path(p) => stem_of(p),
            InputSource::Fd { display_name, .. } => stem_of(Path::new(display_name)),
        }
    });
    (out_dir, name)
}

fn push_log(shared: &Mutex<Shared>, msg: String) {
    let mut g = shared.lock().unwrap_or_else(|e| e.into_inner());
    g.log_lines.push_back(msg);
}

/// Snapshots which of this run's candidate output paths already exist
/// *before* the pipeline starts, so a failure/cancel cleanup can tell this
/// run's own partial outputs apart from a previous successful conversion's
/// outputs sitting at the same `out_dir`/`name` (see `cleanup_partial_outputs`).
///
/// Returns `None` if the snapshot itself could not be taken reliably (e.g.
/// `out_dir` exists but is not readable) — callers must treat that as "do not
/// delete anything", not as "nothing pre-existed".
fn snapshot_pre_existing_outputs(out_dir: &Path, name: &str) -> Option<HashSet<PathBuf>> {
    let mut set = HashSet::new();

    let usst = out_dir.join(format!("{name}.usst"));
    if usst.exists() {
        set.insert(usst);
    }
    let manifest = out_dir.join(format!("{name}.manifest.json"));
    if manifest.exists() {
        set.insert(manifest);
    }

    // Chunk files are named `<name>_<4-digit-id>.usc` (see
    // build-lod-unity::unity_encoder::chunk_file_name); match by prefix/suffix
    // rather than depending on the exact digit count.
    match std::fs::read_dir(out_dir) {
        Ok(entries) => {
            let prefix = format!("{name}_");
            for entry in entries.flatten() {
                let fname = entry.file_name();
                let fname_str = fname.to_string_lossy();
                if fname_str.starts_with(&prefix) && fname_str.ends_with(".usc") {
                    set.insert(entry.path());
                }
            }
            Some(set)
        }
        // `out_dir` doesn't exist yet (common case: this is the first
        // conversion for this out_dir/name): nothing pre-existing, empty set
        // is the correct answer, not a failure.
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Some(set),
        // Any other failure (e.g. permissions): we cannot be sure what was
        // already there, so refuse to guess.
        Err(_) => None,
    }
}

/// Removes any output files this run may have already written before
/// failing or being cancelled — but only files that were *not* present in
/// `pre_existing` (the snapshot taken before this run started). This is what
/// stops a failing/cancelled run from deleting a previous successful
/// conversion's output at the same `out_dir`/`name` (see
/// `snapshot_pre_existing_outputs`). Best-effort: a removal failure is
/// logged, not propagated (the overall result is already an error).
///
/// `pre_existing = None` means the snapshot could not be taken reliably;
/// in that case nothing is deleted, ever, and a log line explains why.
fn cleanup_partial_outputs(
    out_dir: &Path,
    name: &str,
    pre_existing: Option<&HashSet<PathBuf>>,
    shared: &Mutex<Shared>,
) {
    let Some(pre_existing) = pre_existing else {
        push_log(
            shared,
            "cleanup: skipped — could not snapshot pre-existing outputs before this run \
             started, so nothing was deleted (to avoid removing a previous successful \
             conversion's output)"
                .to_string(),
        );
        return;
    };

    let remove_if_new = |path: &Path| {
        if path.exists() && !pre_existing.contains(path) {
            if let Err(e) = std::fs::remove_file(path) {
                push_log(
                    shared,
                    format!("cleanup: failed to remove {}: {e}", path.display()),
                );
            }
        }
    };

    remove_if_new(&out_dir.join(format!("{name}.usst")));
    remove_if_new(&out_dir.join(format!("{name}.manifest.json")));

    match std::fs::read_dir(out_dir) {
        Ok(entries) => {
            let prefix = format!("{name}_");
            for entry in entries.flatten() {
                let fname = entry.file_name();
                let fname_str = fname.to_string_lossy();
                if fname_str.starts_with(&prefix) && fname_str.ends_with(".usc") {
                    remove_if_new(&entry.path());
                }
            }
        }
        Err(e) => {
            push_log(
                shared,
                format!("cleanup: failed to read dir {}: {e}", out_dir.display()),
            );
        }
    }
}

fn format_anyhow_chain(e: &anyhow::Error) -> String {
    let mut msg = e.to_string();
    for cause in e.chain().skip(1) {
        msg.push_str(": ");
        msg.push_str(&cause.to_string());
    }
    msg
}

fn panic_message(payload: &(dyn std::any::Any + Send)) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        s.to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "unknown panic".to_string()
    }
}

fn worker(opts: PipelineOptions, state: Arc<ConvertState>) {
    let (out_dir, name) = resolve_out_dir_name(&opts);

    // Snapshot before doing anything else (A-1): this is what lets the
    // failure/cancel cleanup below tell this run's own partial outputs apart
    // from a previous successful conversion's outputs at the same
    // out_dir/name. `run_pipeline` can fail before writing a single byte
    // (bad max_sh, missing input, "output already exists" without --force),
    // and cancellation can land on the very first log line — both of those
    // must not delete anything that was already there.
    let pre_existing = snapshot_pre_existing_outputs(&out_dir, &name);
    if pre_existing.is_none() {
        push_log(
            &state.shared,
            format!(
                "cleanup: could not snapshot pre-existing outputs in {} before starting; \
                 any failure/cancel cleanup for this run will be skipped for safety",
                out_dir.display()
            ),
        );
    }

    let log_state = state.clone();
    let log = move |s: &str| {
        {
            let mut g = log_state.shared.lock().unwrap_or_else(|e| e.into_inner());
            g.log_lines.push_back(s.to_string());
        } // guard dropped here, before the potential panic below.
        if log_state.cancel.load(Ordering::Relaxed) {
            panic!("gsplat_convert: cancelled");
        }
    };

    let outcome = panic::catch_unwind(AssertUnwindSafe(|| {
        build_lod_unity::run_pipeline(&opts, log)
    }));

    let final_result = match outcome {
        Ok(Ok(manifest)) => Ok(manifest.to_string()),
        Ok(Err(e)) => {
            let msg = format_anyhow_chain(&e);
            cleanup_partial_outputs(&out_dir, &name, pre_existing.as_ref(), &state.shared);
            Err(msg)
        }
        Err(payload) => {
            let msg = panic_message(&*payload);
            cleanup_partial_outputs(&out_dir, &name, pre_existing.as_ref(), &state.shared);
            Err(msg)
        }
    };

    let mut g = state.shared.lock().unwrap_or_else(|e| e.into_inner());
    g.result = Some(final_result);
    drop(g);
    // `state` (this worker's `Arc<ConvertState>` clone) drops when this
    // function returns. See `gsplat_convert_free`'s doc comment for what
    // that means for the handle side.
}

fn start_impl(options_json: *const c_char) -> Option<*mut Handle> {
    if options_json.is_null() {
        return None;
    }
    let s = unsafe { CStr::from_ptr(options_json) }.to_str().ok()?;
    let raw: RawOptions = serde_json::from_str(s).ok()?;
    let opts = raw.into_pipeline_options()?;

    let state = Arc::new(ConvertState {
        shared: Mutex::new(Shared {
            log_lines: VecDeque::new(),
            result: None,
        }),
        cancel: AtomicBool::new(false),
    });

    let worker_state = state.clone();
    // Spawn detached: we deliberately drop the returned `JoinHandle` without
    // joining it. That does not stop or block on the thread — it just means
    // nobody is waiting for it, which is exactly what `gsplat_convert_free`
    // needs (see its doc comment).
    std::thread::Builder::new()
        .name("gsplat-convert".to_string())
        .stack_size(WORKER_STACK_SIZE)
        .spawn(move || worker(opts, worker_state))
        .ok()?;

    let handle = Box::new(Handle {
        magic: HANDLE_MAGIC,
        state,
        cached_result: Mutex::new(None),
    });
    Some(Box::into_raw(handle))
}

/// Returns the C ABI version implemented by this build (currently 1).
#[no_mangle]
pub extern "C" fn gsplat_convert_abi_version() -> c_int {
    panic::catch_unwind(|| 1).unwrap_or(1)
}

/// Starts a conversion job on a worker thread. `options_json_utf8` must be a
/// NUL-terminated UTF-8 string matching the `RawOptions` schema. Returns NULL
/// on any failure to parse options or spawn the worker (nothing is running in
/// that case, so there is nothing to free).
///
/// # Safety
/// `options_json_utf8` must be a valid pointer to a NUL-terminated string, or
/// NULL.
#[no_mangle]
pub unsafe extern "C" fn gsplat_convert_start(options_json_utf8: *const c_char) -> *mut Handle {
    panic::catch_unwind(AssertUnwindSafe(|| start_impl(options_json_utf8)))
        .ok()
        .flatten()
        .unwrap_or(std::ptr::null_mut())
}

fn poll_impl(h: *mut Handle, buf: *mut c_char, buf_len: c_int) -> c_int {
    let Some(handle) = (unsafe { handle_ref(h) }) else {
        return 2;
    };

    let mut g = handle
        .state
        .shared
        .lock()
        .unwrap_or_else(|e| e.into_inner());

    if !buf.is_null() && buf_len > 0 {
        let cap = (buf_len as usize).saturating_sub(1);
        let mut out = String::new();
        if cap > 0 {
            while let Some(line) = g.log_lines.front() {
                let add_len = line.len() + 1; // +1 for the '\n' separator.
                if out.is_empty() && add_len > cap {
                    // This line alone can never fit in a buffer this size —
                    // without this, it would sit at the front of the queue
                    // forever and block every line behind it (B-2). Truncate
                    // it at a UTF-8 char boundary and move on.
                    let line = g.log_lines.pop_front().unwrap();
                    const SUFFIX: &str = "...";
                    let total_budget = cap - 1; // reserve 1 byte for the '\n' below (cap > 0)
                    let suffix_len = if total_budget >= SUFFIX.len() {
                        SUFFIX.len()
                    } else {
                        0
                    };
                    let content_budget = total_budget - suffix_len;
                    let mut end = content_budget.min(line.len());
                    while end > 0 && !line.is_char_boundary(end) {
                        end -= 1;
                    }
                    out.push_str(&line[..end]);
                    if suffix_len > 0 {
                        out.push_str(SUFFIX);
                    }
                    out.push('\n');
                    break;
                }
                if out.len() + add_len > cap {
                    break;
                }
                let line = g.log_lines.pop_front().unwrap();
                out.push_str(&line);
                out.push('\n');
            }
        }
        // SAFETY: `out.len() <= cap == buf_len - 1`, so `out` plus the NUL
        // terminator fits within `buf_len` bytes of `buf`.
        unsafe {
            std::ptr::copy_nonoverlapping(out.as_ptr() as *const c_char, buf, out.len());
            *buf.add(out.len()) = 0;
        }
    }

    match &g.result {
        None => 0,
        Some(Ok(_)) => 1,
        Some(Err(_)) => 2,
    }
}

/// Drains any pending log lines (newline-separated UTF-8, NUL-terminated)
/// into `buf` and returns the job status: 0 = running, 1 = ok, 2 = error.
/// If `buf` is too small to hold even one queued line, that line is
/// truncated at a UTF-8 character boundary with a trailing "..." rather than
/// being left stuck at the front of the queue forever. Safe to call again
/// after status 1/2 to drain any log lines that did not fit yet.
///
/// # Safety
/// `h` must be a live handle from `gsplat_convert_start` (or NULL). `buf`
/// must point to at least `buf_len` writable bytes, or `buf_len` must be 0.
#[no_mangle]
pub unsafe extern "C" fn gsplat_convert_poll(
    h: *mut Handle,
    buf: *mut c_char,
    buf_len: c_int,
) -> c_int {
    panic::catch_unwind(AssertUnwindSafe(|| poll_impl(h, buf, buf_len))).unwrap_or(2)
}

/// Requests cancellation. The job stops at its next log line, not
/// immediately. Safe to call multiple times or on a finished handle.
///
/// # Safety
/// `h` must be a live handle from `gsplat_convert_start` (or NULL).
#[no_mangle]
pub unsafe extern "C" fn gsplat_convert_cancel(h: *mut Handle) {
    let _ = panic::catch_unwind(AssertUnwindSafe(|| {
        if let Some(handle) = unsafe { handle_ref(h) } {
            handle.state.cancel.store(true, Ordering::Relaxed);
        }
    }));
}

fn result_json_impl(h: *mut Handle) -> *const c_char {
    let Some(handle) = (unsafe { handle_ref(h) }) else {
        return std::ptr::null();
    };
    let mut cached = handle
        .cached_result
        .lock()
        .unwrap_or_else(|e| e.into_inner());
    if cached.is_none() {
        let g = handle
            .state
            .shared
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        let text = match &g.result {
            None => return std::ptr::null(), // still running: nothing to report yet.
            Some(Ok(json)) => json.clone(),
            Some(Err(msg)) => msg.clone(),
        };
        drop(g);
        *cached = CString::new(text).ok();
    }
    cached.as_ref().map_or(std::ptr::null(), |c| c.as_ptr())
}

/// Returns the manifest JSON (on success) or the error message (on failure)
/// once the job has finished (status 1 or 2); NULL while still running or on
/// an invalid handle. The returned pointer is owned by the handle and stays
/// valid until `gsplat_convert_free` is called.
///
/// # Safety
/// `h` must be a live handle from `gsplat_convert_start` (or NULL).
#[no_mangle]
pub unsafe extern "C" fn gsplat_convert_result_json(h: *mut Handle) -> *const c_char {
    panic::catch_unwind(AssertUnwindSafe(|| result_json_impl(h))).unwrap_or(std::ptr::null())
}

fn free_impl(h: *mut Handle) {
    if h.is_null() {
        return;
    }
    // SAFETY: `h` is non-null and, per the function's safety contract, either
    // points at a live `Handle` or was already zeroed by a previous call to
    // this function — reading `magic` here is exactly the double-free guard,
    // not a dereference of memory the `Box` has released (that only happens
    // below, after this check passes and only once).
    let valid = unsafe { (*h).magic == HANDLE_MAGIC };
    if !valid {
        return;
    }
    // SAFETY: `valid` above confirmed `h` currently points at a live
    // `Handle`; forming this shared reference explicitly (rather than
    // through field-access autoref on the raw pointer) is what the
    // `dangerous_implicit_autorefs` lint wants here.
    let r = unsafe { &*h };
    // Request cancellation as a courtesy (the caller is also expected to
    // call `gsplat_convert_cancel` first — see `ConvertJob.Dispose` on the
    // C# side — but this covers a caller that free'd without it).
    r.state.cancel.store(true, Ordering::Relaxed);
    // C-1: zero `magic` *before* `Box::from_raw` below, so a concurrent or
    // repeated `free`/`handle_ref` call sees an invalid handle instead of
    // racing this one. `magic` is a plain `u32` (not atomic) because only
    // the single caller thread ever touches it — the worker thread never
    // reads or writes `Handle` fields directly, only `Arc<ConvertState>`.
    unsafe {
        (*h).magic = 0;
    }
    let _boxed = unsafe { Box::from_raw(h) };
    // `_boxed` — and with it, this side's `Arc<ConvertState>` clone — drops
    // here. No `join()`. See the `gsplat_convert_free` doc comment for the
    // full argument for why this is safe and what it does and does not do.
}

/// Requests cancellation, invalidates the handle, and releases this caller's
/// reference to it — **without** waiting for the worker thread to stop.
///
/// This used to `join()` the worker thread, which could block the caller —
/// typically Unity's main thread, via `ConvertJob.Dispose` — for however long
/// the worker took to notice cancellation. Cancellation is only checked
/// between log lines (see the file header), and several stretches of the
/// pipeline (decode, the `.usc` write loop, validate) emit no log lines at
/// all, so that block could run to minutes on a large input. On a mobile
/// headset that is an ANR during app teardown or a scene change, not
/// something a user can wait out — hence the detach.
///
/// Why this cannot use-after-free: the worker thread only ever touches the
/// `Arc<ConvertState>` clone it was spawned with — never the `Handle`/`Box`
/// itself. This function zeroes `magic` before releasing the `Box` (C-1), so
/// no other call can be mid-dereference of the `Handle` when it goes away.
/// Releasing the `Box` drops this side's `ConvertState` clone; if the worker
/// is still running, its own clone keeps the shared state alive, and the
/// memory is only actually freed once the *last* clone drops — either right
/// now (worker already finished) or later, when `worker()` returns. Neither
/// side can ever observe the other's half of `ConvertState` freed out from
/// under it.
///
/// Side effects of detaching — read this before assuming "freed" means
/// "stopped": the worker thread keeps running after this call returns, up to
/// its next log line (where it observes the cancel flag this function set)
/// or natural completion. In the Unity Editor, native plugins are not
/// unloaded on domain reload, so a worker thread started before a reload can
/// still be running (holding its own `ConvertState` clone) after the
/// reload — orphaned, with nothing left polling or able to cancel it via
/// this handle, until it finishes on its own or the process exits.
///
/// Safe to call on NULL. Do not use `h` after this call.
///
/// # Safety
/// `h` must either be NULL or a pointer previously returned by
/// `gsplat_convert_start`, not already freed.
#[no_mangle]
pub unsafe extern "C" fn gsplat_convert_free(h: *mut Handle) {
    let _ = panic::catch_unwind(AssertUnwindSafe(|| free_impl(h)));
}
