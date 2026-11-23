using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

// Apply to a group of PC parts, not to the XR rig or UI interactables.
public class NearOnlyGrabFilter : MonoBehaviour, IXRHoverFilter, IXRSelectFilter
{
    private readonly List<XRGrabInteractable> registered = new List<XRGrabInteractable>();
    public bool canProcess => isActiveAndEnabled;

    private void OnEnable()
    {
        foreach (XRGrabInteractable grab in GetComponentsInChildren<XRGrabInteractable>(true))
        {
            grab.hoverFilters.Add(this);
            grab.selectFilters.Add(this);
            registered.Add(grab);
        }
    }

    private void OnDisable()
    {
        foreach (XRGrabInteractable grab in registered)
        {
            if (grab == null) continue;
            grab.hoverFilters.Remove(this);
            grab.selectFilters.Remove(this);
        }
        registered.Clear();
    }

    public bool Process(IXRHoverInteractor interactor, IXRHoverInteractable interactable)
    {
        if (interactor is IXRSelectInteractor selecting && interactable is IXRSelectInteractable selected &&
            selecting.IsSelecting(selected)) return true;
        return IsNear(interactor, interactable as XRGrabInteractable);
    }

    public bool Process(IXRSelectInteractor interactor, IXRSelectInteractable interactable)
    {
        if (interactor.IsSelecting(interactable)) return true;
        return IsNear(interactor, interactable as XRGrabInteractable);
    }

    // Distance only. Where a seated part may be taken from is MountablePart's own hover/select filter.
    private static bool IsNear(IXRInteractor interactor, XRGrabInteractable grab)
    {
        if (grab == null) return true;

        if (interactor is NearFarInteractor nearFar)
        {
            if (!nearFar.enableNearCasting || !(nearFar.nearInteractionCaster is SphereInteractionCaster sphere))
                return false;
            float radius = sphere.castRadius * Mathf.Abs(sphere.transform.lossyScale.x);
            return GrabUtility.AnyColliderWithin(grab, GrabUtility.GetInteractorPosition(nearFar, grab), radius);
        }

        // Direct and socket interactors can only reach what they touch.
        return interactor is XRDirectInteractor || interactor is XRSocketInteractor;
    }
}
