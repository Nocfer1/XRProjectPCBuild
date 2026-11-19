using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public enum SlotType { CPU, RAM, PCIe, CaseStandoff, PowerSupply }

public class SnapPoint : MonoBehaviour
{
    [Header("Slot Configuration")]
    public SlotType acceptedSlotType;

    [Tooltip("Target pose for the part's alignment pivot. Defaults to this object's transform.")]
    public Transform snapTransform;

    [Header("Precision Tolerances")]
    [Min(0f)] public float maxPositionError = 0.015f;
    [Range(-1f, 1f)] public float minOrientationAlignment = 0.95f;

    [Header("Mounted Part Removal")]
    [Tooltip("Allow a seated part to be picked up and removed.")]
    public bool allowRemoval = true;
    [Tooltip("Restrict the initial grab distance from the seated part's interaction colliders.")]
    public bool limitGrabDistance;
    [Min(0f)] public float mountedGrabDistance = 0.015f;
    [Tooltip("Require the controller to be on the positive up side of the approach transform.")]
    public bool requireApproachDirection;
    [Tooltip("Defines the front plane and its up direction; defaults to the snap target.")]
    public Transform removalApproach;
    [Tooltip("Optional active collider defining a region the controller must be inside. A trigger is recommended.")]
    public Collider removalGripRegion;

    public MountablePart SeatedPart { get; private set; }
    public bool isOccupied => SeatedPart != null;
    public Transform Target => snapTransform != null ? snapTransform : transform;

    public bool CanAccept(MountablePart part)
    {
        return isActiveAndEnabled && part != null && !part.IsMounted && !isOccupied &&
               part.compatibleSlotType == acceptedSlotType && !Target.IsChildOf(part.transform);
    }

    public bool EvaluatePrecisionFit(Transform pivot)
    {
        if (isOccupied || pivot == null) return false;
        Transform target = Target;
        return Vector3.Distance(pivot.position, target.position) <= maxPositionError &&
               Vector3.Dot(pivot.forward, target.forward) >= minOrientationAlignment &&
               Vector3.Dot(pivot.up, target.up) >= minOrientationAlignment;
    }

    public bool CanRemoveFrom(Vector3 controllerPosition, XRGrabInteractable grab)
    {
        if (!allowRemoval || grab == null) return false;

        Transform approach = removalApproach != null ? removalApproach : Target;
        if (requireApproachDirection && Vector3.Dot(controllerPosition - approach.position, approach.up) < 0f)
            return false;

        if (removalGripRegion != null)
        {
            if (!removalGripRegion.enabled || !removalGripRegion.gameObject.activeInHierarchy ||
                (removalGripRegion.ClosestPoint(controllerPosition) - controllerPosition).sqrMagnitude > 0.00000001f)
                return false;
        }

        if (!limitGrabDistance) return true;
        float maxDistanceSquared = mountedGrabDistance * mountedGrabDistance;
        foreach (Collider collider in grab.colliders)
        {
            if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                (collider.ClosestPoint(controllerPosition) - controllerPosition).sqrMagnitude <= maxDistanceSquared)
                return true;
        }
        return false;
    }

    public bool TrySeat(MountablePart part)
    {
        if (!CanAccept(part) || !EvaluatePrecisionFit(part.AlignmentPivot)) return false;
        if (!part.SeatIn(this)) return false;
        SeatedPart = part;
        return true;
    }

    internal void ClearSeat(MountablePart part)
    {
        if (SeatedPart == part) SeatedPart = null;
    }

    private void OnDisable()
    {
        if (SeatedPart != null) SeatedPart.Unmount();
    }
}
