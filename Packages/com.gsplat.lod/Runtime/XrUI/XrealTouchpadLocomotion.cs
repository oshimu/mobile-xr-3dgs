// Travel through the scene with the XREAL virtual controller's centre trackpad:
// vertical drag = forward/back along the head's heading, horizontal drag = yaw.
//
// The pad reports an ABSOLUTE normalised position inside its rect (XREALButton
// SendTouchPos), not a delta, so using it straight as a stick would jump to full
// speed the moment you touch off-centre. Touch-down captures an origin and the
// input is the displacement from it — a relative stick, which is the only thing
// that feels right on a flat phone screen with no centre detent.
//
// Touch begin/end comes from TriggerButton: the pad and the trigger share one
// GameObject on the SDK prefab, so any touch presses the trigger too. That
// collision is the reliable touch-active signal (no need to guess whether the
// axis returns to zero on release).
//
// Bindings are path strings, so this compiles and runs without referencing the
// XREAL assembly; off XREAL the paths simply do not resolve.

using UnityEngine;
using UnityEngine.InputSystem;
using Unity.XR.CoreUtils;

namespace GsplatLod.XrUI
{
    public sealed class XrealTouchpadLocomotion : MonoBehaviour
    {
        [Tooltip("Travel speed at full pad deflection (m/s)")]
        public float MoveSpeed = 1.5f;

        [Tooltip("Yaw rate at full pad deflection (degrees/s)")]
        public float TurnSpeed = 60f;

        [Tooltip("Drag distance (in pad units, pad half-width = 1) that counts as full deflection")]
        public float FullDeflection = 0.35f;

        [Tooltip("Displacement below this fraction of full deflection is ignored")]
        public float DeadZone = 0.08f;

        [Tooltip("Response exponent; >1 gives finer control near the centre")]
        public float ResponseCurve = 2f;

        [Tooltip("Drag down to move forward instead of up")]
        public bool InvertVertical = false;

        [Tooltip("Panel whose hover state suppresses movement; auto-resolved when empty")]
        public LodSetupPanel Panel;

        InputAction m_touchpad;
        InputAction m_touch;
        XROrigin m_origin;
        Vector2 m_padOrigin;
        bool m_dragging;

        void OnEnable()
        {
            m_touchpad = new InputAction("XrealTouchpad", InputActionType.Value, expectedControlType: "Vector2");
            m_touchpad.AddBinding("<XREALController>/Primary2DAxis");
            m_touchpad.AddBinding("<XRSimulatedController>/primary2DAxis");
            m_touchpad.Enable();

            m_touch = new InputAction("XrealTouchpadTouch", InputActionType.Button);
            m_touch.AddBinding("<XREALController>/TriggerButton");
            m_touch.AddBinding("<XRSimulatedController>/triggerButton");
            m_touch.Enable();
        }

        void OnDisable()
        {
            m_touchpad?.Dispose();
            m_touch?.Dispose();
            m_touchpad = null;
            m_touch = null;
            m_dragging = false;
        }

        void Update()
        {
            var origin = ResolveOrigin();
            if (origin == null) return;

            if (!Panel) Panel = FindAnyObjectByType<LodSetupPanel>();

            // Pointing at the panel means UI work, not travel.
            bool touching = m_touch != null && m_touch.IsPressed()
                            && !(Panel != null && Panel.PointerOverPanel);
            if (!touching)
            {
                m_dragging = false;
                return;
            }

            var pad = m_touchpad.ReadValue<Vector2>();
            if (!m_dragging)
            {
                // First frame of a touch only establishes the origin.
                m_padOrigin = pad;
                m_dragging = true;
                return;
            }

            float span = Mathf.Max(FullDeflection, 1e-3f);
            float h = Response((pad.x - m_padOrigin.x) / span);
            float v = Response((pad.y - m_padOrigin.y) / span);
            if (InvertVertical) v = -v;

            var cam = origin.Camera ? origin.Camera : Camera.main;
            if (!cam) return;
            float dt = Time.deltaTime;

            if (!Mathf.Approximately(v, 0f))
            {
                var fwd = cam.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-6f)
                {
                    fwd.Normalize();
                    origin.MoveCameraToWorldLocation(
                        cam.transform.position + fwd * (v * MoveSpeed * dt));
                }
            }

            if (!Mathf.Approximately(h, 0f))
                origin.RotateAroundCameraUsingOriginUp(h * TurnSpeed * dt);
        }

        // Clamp, drop the dead zone, then curve for fine control near centre.
        float Response(float x)
        {
            x = Mathf.Clamp(x, -1f, 1f);
            float mag = Mathf.Abs(x);
            if (mag < DeadZone) return 0f;
            mag = (mag - DeadZone) / Mathf.Max(1f - DeadZone, 1e-3f);
            return Mathf.Sign(x) * Mathf.Pow(mag, Mathf.Max(ResponseCurve, 1f));
        }

        // The scene's standalone rig is disabled on XREAL; only the rig nested in
        // the XREAL setup prefab is active, so filter on active-and-enabled.
        XROrigin ResolveOrigin()
        {
            if (m_origin && m_origin.isActiveAndEnabled) return m_origin;
            m_origin = null;
            foreach (var o in FindObjectsByType<XROrigin>(FindObjectsSortMode.None))
            {
                if (!o.isActiveAndEnabled) continue;
                m_origin = o;
                break;
            }
            return m_origin;
        }
    }
}
