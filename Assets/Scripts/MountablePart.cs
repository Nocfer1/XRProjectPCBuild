using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
[RequireComponent(typeof(Rigidbody))]
public class MountablePart : MonoBehaviour
{
    [Header("Component Data")]
    public SlotType compatibleSlotType;

    [Header("Alignment & Detection")]
    [Tooltip("Specific transform on this part (e.g., CPU pins or connector edge) used for snapping and distance checks.")]
    public Transform alignmentPivot;

    [Tooltip("Search radius in meters around alignmentPivot when released.")]
    public float snapSearchRadius = 0.1f;

    public bool IsMounted { get; private set; }
    public SnapPoint MountedSlot { get; private set; }

    private XRGrabInteractable grabInteractable;

    private void Awake()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();

        if (alignmentPivot == null)
        {
            alignmentPivot = transform;
        }
    }

    private void OnEnable()
    {
        if (grabInteractable != null)
        {
            grabInteractable.selectExited.AddListener(OnReleased);
        }
    }

    private void OnDisable()
    {
        if (grabInteractable != null)
        {
            grabInteractable.selectExited.RemoveListener(OnReleased);
        }
    }

    private void OnReleased(SelectExitEventArgs args)
    {
        if (IsMounted) return;

        TryMountToNearbySlot();
    }

    public bool TryMountToNearbySlot()
    {
        if (IsMounted || alignmentPivot == null) return false;

        Collider[] hits = Physics.OverlapSphere(alignmentPivot.position, snapSearchRadius);

        SnapPoint bestSlot = null;
        float closestDistance = float.MaxValue;

        foreach (var hit in hits)
        {
            if (hit == null) continue;

            if (hit.TryGetComponent<SnapPoint>(out var slot))
            {
                if (!slot.CanAccept(this)) continue;

                float dist = Vector3.Distance(alignmentPivot.position, slot.snapTransform.position);
                if (dist < closestDistance)
                {
                    closestDistance = dist;
                    bestSlot = slot;
                }
            }
        }

        if (bestSlot != null && bestSlot.EvaluatePrecisionFit(alignmentPivot))
        {
            IsMounted = true;
            MountedSlot = bestSlot;
            bestSlot.LockPart(this, alignmentPivot);
            return true;
        }

        return false;
    }
}