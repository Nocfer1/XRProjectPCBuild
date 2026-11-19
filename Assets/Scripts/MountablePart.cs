using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable), typeof(Rigidbody))]
public class MountablePart : MonoBehaviour
{
    [Header("Component Data")]
    public SlotType compatibleSlotType;

    [Header("Alignment & Detection")]
    [Tooltip("Mating surface pose. Its position and axes must match the socket target when seated. Defaults to this object's transform.")]
    public Transform alignmentPivot;
    [Min(0f)] public float snapSearchRadius = 0.1f;
    [Tooltip("Log why a snap attempt failed when this part is released.")]
    public bool logSnapDiagnostics;

    public SnapPoint MountedSlot { get; private set; }
    public bool IsMounted => MountedSlot != null;
    public Transform AlignmentPivot => alignmentPivot != null ? alignmentPivot : transform;

    private XRGrabInteractable grabInteractable;
    private Rigidbody partRigidbody;
    private Coroutine pendingRelease;
    private Transform parentBeforeMount;
    private bool wasKinematic;
    private bool usedGravity;
    private bool retainParentBeforeMount;
    private bool restoreAfterGrab;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();
        partRigidbody = GetComponent<Rigidbody>();
    }

    private void OnEnable()
    {
        grabInteractable.selectEntered.AddListener(OnGrabbed);
        grabInteractable.selectExited.AddListener(OnReleased);
    }

    private void OnDisable()
    {
        grabInteractable.selectEntered.RemoveListener(OnGrabbed);
        grabInteractable.selectExited.RemoveListener(OnReleased);
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = null;
        Unmount();
        RestoreAfterGrab();
    }

    private void OnGrabbed(SelectEnterEventArgs args)
    {
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = null;
        if (IsMounted) Unmount();
    }

    private void OnReleased(SelectExitEventArgs args)
    {
        if (grabInteractable.isSelected || !isActiveAndEnabled) return;
        if (pendingRelease != null) StopCoroutine(pendingRelease);
        pendingRelease = StartCoroutine(FinishRelease());
    }

    private IEnumerator FinishRelease()
    {
        // XR Grab completes its detach in Late update, after selectExited.
        yield return new WaitForEndOfFrame();
        pendingRelease = null;
        if (grabInteractable.isSelected) yield break;

        RestoreAfterGrab();
        Vector3 angularBefore = partRigidbody.angularVelocity;
        if (!grabInteractable.throwOnDetach && !partRigidbody.isKinematic)
            partRigidbody.angularVelocity = Vector3.zero;
        if (logSnapDiagnostics)
            Debug.Log($"{name}: Release rotation: before={angularBefore}, after={partRigidbody.angularVelocity}, " +
                $"kinematic={partRigidbody.isKinematic}, throwOnDetach={grabInteractable.throwOnDetach}.", this);
        TryMountToNearbySlot();

        if (logSnapDiagnostics && !IsMounted)
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (!grabInteractable.isSelected && !IsMounted)
                Debug.Log($"{name}: Rotation after physics: angularVelocity={partRigidbody.angularVelocity}, " +
                    $"kinematic={partRigidbody.isKinematic}.", this);
        }
    }

    public bool TryMountToNearbySlot()
    {
        if (IsMounted || grabInteractable.isSelected) return false;
        Physics.SyncTransforms();
        Transform pivot = AlignmentPivot;
        Collider[] hits = Physics.OverlapSphere(pivot.position, snapSearchRadius,
            Physics.AllLayers, QueryTriggerInteraction.Collide);
        SnapPoint bestFit = null;
        SnapPoint nearest = null;
        float bestDistance = float.MaxValue;
        float nearestDistance = float.MaxValue;

        foreach (Collider hit in hits)
        {
            SnapPoint slot = hit.GetComponentInParent<SnapPoint>();
            if (slot == null || !slot.CanAccept(this)) continue;
            float distance = Vector3.Distance(pivot.position, slot.Target.position);
            if (distance < nearestDistance) { nearest = slot; nearestDistance = distance; }
            if (distance < bestDistance && slot.EvaluatePrecisionFit(pivot))
            {
                bestFit = slot;
                bestDistance = distance;
            }
        }

        if (bestFit != null && bestFit.TrySeat(this)) return true;
        if (logSnapDiagnostics)
        {
            if (nearest == null)
                Debug.Log($"{name}: No available compatible snap point found within {snapSearchRadius:F3} m of the alignment pivot.", this);
            else
                Debug.Log($"{name}: Snap rejected by {nearest.name}. Distance: {nearestDistance:F3} m (maximum {nearest.maxPositionError:F3}); " +
                    $"forward alignment: {Vector3.Dot(pivot.forward, nearest.Target.forward):F3}, " +
                    $"up alignment: {Vector3.Dot(pivot.up, nearest.Target.up):F3} (minimum {nearest.minOrientationAlignment:F3}).", this);
        }
        return false;
    }

    internal bool SeatIn(SnapPoint slot)
    {
        if (IsMounted || slot == null || grabInteractable.isSelected) return false;
        Transform pivot = AlignmentPivot;
        Transform target = slot.Target;
        Quaternion rotationDelta = target.rotation * Quaternion.Inverse(pivot.rotation);
        Vector3 rootOffset = transform.position - pivot.position;
        Vector3 targetPosition = target.position + rotationDelta * rootOffset;
        Quaternion targetRotation = rotationDelta * transform.rotation;

        parentBeforeMount = transform.parent;
        wasKinematic = partRigidbody.isKinematic;
        usedGravity = partRigidbody.useGravity;
        retainParentBeforeMount = grabInteractable.retainTransformParent;
        if (!partRigidbody.isKinematic)
        {
            partRigidbody.linearVelocity = Vector3.zero;
            partRigidbody.angularVelocity = Vector3.zero;
        }
        partRigidbody.isKinematic = true;
        partRigidbody.useGravity = false;
        transform.SetParent(target, true);
        transform.SetPositionAndRotation(targetPosition, targetRotation);
        MountedSlot = slot;
        return true;
    }

    public void Unmount()
    {
        if (!IsMounted) return;
        SnapPoint slot = MountedSlot;
        MountedSlot = null;
        slot.ClearSeat(this);
        transform.SetParent(parentBeforeMount, true);
        if (grabInteractable.isSelected)
        {
            // Grab() already captured the seated Rigidbody and parent. Restore the
            // pre-seat values after XR Grab finishes dropping the part.
            grabInteractable.retainTransformParent = false;
            restoreAfterGrab = true;
        }
        else
        {
            partRigidbody.isKinematic = wasKinematic;
            partRigidbody.useGravity = usedGravity;
            grabInteractable.retainTransformParent = retainParentBeforeMount;
        }
    }

    private void RestoreAfterGrab()
    {
        if (!restoreAfterGrab) return;
        partRigidbody.isKinematic = wasKinematic;
        partRigidbody.useGravity = usedGravity;
        grabInteractable.retainTransformParent = retainParentBeforeMount;
        restoreAfterGrab = false;
    }
}
