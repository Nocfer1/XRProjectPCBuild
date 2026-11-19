using UnityEngine;

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
