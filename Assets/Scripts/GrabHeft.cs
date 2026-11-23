using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// Makes heavy objects lag behind the hand, using the Rigidbody's mass as the only per-object value.
// Velocity tracking ignores mass while held, so the weight is expressed through grab smoothing:
// follow response = strength / (mass / hands holding), so a second hand halves the felt weight.
[RequireComponent(typeof(XRGrabInteractable), typeof(Rigidbody))]
public class GrabHeft : MonoBehaviour
{
    [Tooltip("Follow response for 1 kg in one hand. Higher values feel lighter. 30 gives a 6 kg case a response of 5.")]
    [Min(0.1f)] public float strength = 30f;
    [Tooltip("Below this mass per hand (kg) the object follows the hand without added lag.")]
    [Min(0f)] public float lightMassPerHand = 1f;
    [Tooltip("Slowest follow response, reached by very heavy objects.")]
    [Min(0.1f)] public float minFollow = 4f;
    [Tooltip("Fastest follow response while heft still applies.")]
    [Min(0.1f)] public float maxFollow = 20f;

    private XRGrabInteractable grab;
    private Rigidbody body;
    private bool originalSmoothPosition;
    private bool originalSmoothRotation;
    private float originalPositionAmount;
    private float originalRotationAmount;

    // Smoothing response this object would get when held by the given number of hands.
    public float GetFollowResponse(int hands)
    {
        float massPerHand = body.mass / Mathf.Max(1, hands);
        return Mathf.Clamp(strength / massPerHand, minFollow, Mathf.Max(minFollow, maxFollow));
    }

    public bool AppliesHeft(int hands) => body.mass / Mathf.Max(1, hands) >= lightMassPerHand;

    private void Awake()
    {
        grab = GetComponent<XRGrabInteractable>();
        body = GetComponent<Rigidbody>();
        originalSmoothPosition = grab.smoothPosition;
        originalSmoothRotation = grab.smoothRotation;
        originalPositionAmount = grab.smoothPositionAmount;
        originalRotationAmount = grab.smoothRotationAmount;
    }

    private void OnEnable()
    {
        grab.selectEntered.AddListener(OnSelectionChanged);
        grab.selectExited.AddListener(OnSelectionChanged);
    }

    private void OnDisable()
    {
        grab.selectEntered.RemoveListener(OnSelectionChanged);
        grab.selectExited.RemoveListener(OnSelectionChanged);
        Restore();
    }

    private void OnSelectionChanged(SelectEnterEventArgs args) => UpdateResponse();
    private void OnSelectionChanged(SelectExitEventArgs args) => UpdateResponse();

    private void UpdateResponse()
    {
        int hands = grab.interactorsSelecting.Count;
        if (hands == 0 || !AppliesHeft(hands)) { Restore(); return; }
        float response = GetFollowResponse(hands);
        grab.smoothPosition = true;
        grab.smoothRotation = true;
        grab.smoothPositionAmount = response;
        grab.smoothRotationAmount = response;
    }

    private void Restore()
    {
        grab.smoothPosition = originalSmoothPosition;
        grab.smoothRotation = originalSmoothRotation;
        grab.smoothPositionAmount = originalPositionAmount;
        grab.smoothRotationAmount = originalRotationAmount;
    }
}
