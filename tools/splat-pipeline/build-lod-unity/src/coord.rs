//! Coordinate-system conversion presets (spec §4.6), baked offline so the
//! Unity runtime never applies a basis change.
//!
//! Reflections conjugate the rotation (M·R·M). For an axis flip the resulting
//! quaternion negates the two components orthogonal to the flipped axis.

use glam::Quat;
use spark_lib::tsplat::{Tsplat, TsplatArray, TsplatMut};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoordPreset {
    Identity,
    FlipZ,
    FlipY,
    FlipX,
}

pub fn parse_preset(s: &str) -> Option<CoordPreset> {
    match s {
        "identity" => Some(CoordPreset::Identity),
        "flip-z" => Some(CoordPreset::FlipZ),
        "flip-y" => Some(CoordPreset::FlipY),
        "flip-x" => Some(CoordPreset::FlipX),
        _ => None,
    }
}

pub fn preset_name(p: CoordPreset) -> &'static str {
    match p {
        CoordPreset::Identity => "identity",
        CoordPreset::FlipZ => "flip-z",
        CoordPreset::FlipY => "flip-y",
        CoordPreset::FlipX => "flip-x",
    }
}

pub fn apply<SA: TsplatArray>(splats: &mut SA, preset: CoordPreset) {
    if preset == CoordPreset::Identity {
        return;
    }
    for i in 0..splats.len() {
        let mut splat = splats.get_mut(i);
        let mut c = splat.center();
        let q = splat.quaternion();
        let q = match preset {
            CoordPreset::Identity => q,
            CoordPreset::FlipZ => {
                c.z = -c.z;
                Quat::from_xyzw(-q.x, -q.y, q.z, q.w)
            }
            CoordPreset::FlipY => {
                c.y = -c.y;
                Quat::from_xyzw(-q.x, q.y, -q.z, q.w)
            }
            CoordPreset::FlipX => {
                c.x = -c.x;
                Quat::from_xyzw(q.x, -q.y, -q.z, q.w)
            }
        };
        splat.set_center(c);
        splat.set_quaternion(q);
    }
}
