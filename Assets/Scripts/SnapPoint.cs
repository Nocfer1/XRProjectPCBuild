using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public enum SlotType { CPU, RAM, PCIe, CaseStandoff, PowerSupply, CPUCooler, Motherboard, SSD }

public class SnapPoint : MonoBehaviour
{
    [Header("Slot Configuration")]
    public SlotType acceptedSlotType;

    [Tooltip("Target pose for the part's alignment pivot. Defaults to this object's transform.")]
    public Transform snapTransform;

    [Header("Assembly Order")]
    [Tooltip("All listed snap points must be occupied before this socket can be used. Their parts cannot be removed while this socket is occupied.")]
    public List<SnapPoint> requiredOccupiedSlots = new List<SnapPoint>();
    [Tooltip("Called once when an actual mount or removal attempt is blocked by assembly order.")]
    public AssemblyOrderViolationEvent onOrderRejected = new AssemblyOrderViolationEvent();

    public static event Action<AssemblyOrderViolation> OrderRejected;
    private static readonly HashSet<SnapPoint> activeSlots = new HashSet<SnapPoint>();
    private bool vacating;

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

    [Header("Pull To Remove")]
    [Tooltip("Keep the part seated while held until the controller pulls along the removal direction.")]
    public bool requirePullToRemove;
    [Tooltip("Require a different controller to hold the host object while pulling.")]
    public bool requireHostHeld = true;
    [Min(0.001f)] public float pullDistance = 0.03f;
    [Min(0f)] public float pullHoldSeconds = 0.15f;

    public MountablePart SeatedPart { get; private set; }
    public bool isOccupied => SeatedPart != null;
    public Transform Target => snapTransform != null ? snapTransform : transform;
    public Transform RemovalDirection => removalApproach != null ? removalApproach : Target;

    public float MeasurePullDistance(Vector3 startInTargetSpace, Vector3 currentPosition)
    {
        Vector3 startWorld = Target.TransformPoint(startInTargetSpace);
        return Vector3.Dot(currentPosition - startWorld, RemovalDirection.up);
    }

    public bool CanAccept(MountablePart part)
    {
        return CanConsider(part) && !TryGetMissingPrerequisite(out _);
    }

    public bool CanConsider(MountablePart part)
    {
        return isActiveAndEnabled && part != null && !part.IsMounted && !isOccupied &&
               part.compatibleSlotType == acceptedSlotType && !Target.IsChildOf(part.transform);
    }

    public bool TryGetMissingPrerequisite(out SnapPoint missing)
    {
        foreach (SnapPoint required in requiredOccupiedSlots)
        {
            // Empty or self references are configuration mistakes (see OnValidate), not blockers.
            if (required != null && required != this && !required.isOccupied)
            {
                missing = required;
                return true;
            }
        }
        missing = null;
        return false;
    }

    public bool TryGetBlockingDependent(out SnapPoint blocker)
    {
        foreach (SnapPoint other in activeSlots)
        {
            if (other != null && other != this && other.isOccupied && other.requiredOccupiedSlots.Contains(this))
            {
                blocker = other;
                return true;
            }
        }
        blocker = null;
        return false;
    }

    public void ReportOrderRejected(AssemblyOrderViolationKind kind, MountablePart part, SnapPoint blockingSlot)
    {
        var violation = new AssemblyOrderViolation { kind = kind, attemptedSlot = this,
            attemptedPart = part, blockingSlot = blockingSlot };
        onOrderRejected?.Invoke(violation);
        OrderRejected?.Invoke(violation);
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

        Transform approach = RemovalDirection;
        if (requireApproachDirection && Vector3.Dot(controllerPosition - approach.position, approach.up) < 0f)
            return false;

        if (removalGripRegion != null)
        {
            if (!removalGripRegion.enabled || !removalGripRegion.gameObject.activeInHierarchy ||
                (removalGripRegion.ClosestPoint(controllerPosition) - controllerPosition).sqrMagnitude > 0.00000001f)
                return false;
        }

        return !limitGrabDistance || GrabUtility.AnyColliderWithin(grab, controllerPosition, mountedGrabDistance);
    }

    public bool TrySeat(MountablePart part) => TrySeat(part, true);

    // requirePrecisionFit is false for parts placed by code (initial slots); seating aligns them anyway.
    internal bool TrySeat(MountablePart part, bool requirePrecisionFit)
    {
        if (!CanConsider(part) || (requirePrecisionFit && !EvaluatePrecisionFit(part.AlignmentPivot))) return false;
        if (TryGetMissingPrerequisite(out var missing))
        {
            ReportOrderRejected(AssemblyOrderViolationKind.MissingPrerequisite, part, missing);
            return false;
        }
        if (!part.SeatIn(this)) return false;
        SeatedPart = part;
        return true;
    }

    internal void ClearSeat(MountablePart part)
    {
        if (SeatedPart == part) SeatedPart = null;
    }

    // Removes the seated part without the order check. Occupied slots that depend on this one
    // are vacated first, so no part is left mounted without its prerequisite.
    internal void Vacate()
    {
        if (SeatedPart == null || vacating) return;
        vacating = true;
        try
        {
            var dependents = new List<SnapPoint>();
            foreach (SnapPoint other in activeSlots)
                if (other != this && other.isOccupied && other.requiredOccupiedSlots.Contains(this))
                    dependents.Add(other);
            foreach (SnapPoint dependent in dependents) dependent.Vacate();
            if (SeatedPart != null) SeatedPart.ForceUnmount();
        }
        finally { vacating = false; }
    }

    private void OnEnable() => activeSlots.Add(this);

    private void OnDisable()
    {
        Vacate();
        activeSlots.Remove(this);
    }

    private void OnValidate()
    {
        foreach (SnapPoint required in requiredOccupiedSlots)
        {
            if (required == null)
                Debug.LogWarning($"{name}: Required Occupied Slots has an empty entry; it is ignored.", this);
            else if (required == this)
                Debug.LogWarning($"{name}: Required Occupied Slots lists this slot itself; it is ignored.", this);
        }
    }
}
