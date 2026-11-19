using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
public class GrabHeft : MonoBehaviour
{
    [Tooltip("Follow response while held with one controller. Lower values create more lag.")]
    [Min(0.1f)] public float oneHandFollow = 5f;
    [Tooltip("Follow response when held with two or more controllers.")]
    [Min(0.1f)] public float twoHandFollow = 12f;

    private XRGrabInteractable grab;
    private bool originalSmoothPosition;
    private bool originalSmoothRotation;
    private float originalPositionAmount;
    private float originalRotationAmount;

    private void Awake()
    {
        grab = GetComponent<XRGrabInteractable>();
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
        if (!grab.isSelected) { Restore(); return; }
        float response = grab.interactorsSelecting.Count > 1 ? twoHandFollow : oneHandFollow;
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
