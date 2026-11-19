using UnityEngine;

public enum SlotType { CPU, RAM, PCIe, CaseStandoff, PowerSupply }

public class SnapPoint : MonoBehaviour
{
    [Header("Slot Configuration")]
    public SlotType acceptedSlotType;

    [Tooltip("Exact target transform where the part's alignment pivot snaps. Defaults to this GameObject's transform.")]
    public Transform snapTransform;

    [Header("Precision Tolerances")]
    [Tooltip("Maximum allowable distance in meters between alignment pivot and snapTransform.")]
    public float maxPositionError = 0.015f;

    [Tooltip("Minimum dot product (-1 to 1) for forward and up alignment. 0.95 is roughly ~18 degrees.")]
    public float minOrientationAlignment = 0.95f;

    public bool isOccupied { get; private set; }
    public MountablePart SeatedPart { get; private set; }

    private void Awake()
    {
        if (snapTransform == null)
        {
            snapTransform = transform;
        }
    }

    public bool CanAccept(MountablePart part)
    {
        return part != null && !isOccupied && part.compatibleSlotType == acceptedSlotType;
    }

    public bool EvaluatePrecisionFit(Transform partAlignmentPivot)
    {
        if (isOccupied || snapTransform == null || partAlignmentPivot == null)
        {
            return false;
        }

        float distance = Vector3.Distance(partAlignmentPivot.position, snapTransform.position);
        if (distance > maxPositionError)
        {
            return false;
        }

        float forwardMatch = Vector3.Dot(partAlignmentPivot.forward, snapTransform.forward);
        float upMatch = Vector3.Dot(partAlignmentPivot.up, snapTransform.up);

        if (forwardMatch < minOrientationAlignment || upMatch < minOrientationAlignment)
        {
            return false;
        }

        return true;
    }

    public void LockPart(MountablePart part, Transform partAlignmentPivot)
    {
        if (part == null || snapTransform == null) return;

        Transform pivot = partAlignmentPivot != null ? partAlignmentPivot : part.transform;
        Rigidbody partRb = part.GetComponent<Rigidbody>();

        isOccupied = true;
        SeatedPart = part;

        if (partRb != null)
        {
            partRb.linearVelocity = Vector3.zero;
            partRb.angularVelocity = Vector3.zero;
            partRb.isKinematic = true;
            partRb.useGravity = false;
        }

        // Compute pivot offset and rotation delta in world space BEFORE parenting/moving,
        // so rotating the part around its pivot places `pivot` cleanly onto `snapTransform`.
        Quaternion pivotToRootRot = Quaternion.Inverse(pivot.rotation) * part.transform.rotation;
        Vector3 pivotToRootOffset = part.transform.position - pivot.position;

        Quaternion targetPivotRot = snapTransform.rotation;
        Quaternion targetPartRot = targetPivotRot * pivotToRootRot;
        Vector3 targetPartPos = snapTransform.position + (targetPivotRot * Quaternion.Inverse(pivot.rotation) * pivotToRootOffset);

        part.transform.SetParent(snapTransform, worldPositionStays: true);
        part.transform.SetPositionAndRotation(targetPartPos, targetPartRot);
    }
}