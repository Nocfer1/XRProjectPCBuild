using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// Shared hand-position and reach checks used by the grab filters and mounting logic.
public static class GrabUtility
{
    // The point a hand grabs from: the near caster origin for Near-Far, the attach point for Direct.
    public static Vector3 GetInteractorPosition(IXRInteractor interactor, IXRInteractable interactable)
    {
        if (interactor is NearFarInteractor nearFar && nearFar.nearInteractionCaster != null)
        {
            var caster = nearFar.nearInteractionCaster;
            Transform origin = caster.effectiveCastOrigin != null ? caster.effectiveCastOrigin : caster.castOrigin;
            if (origin != null) return origin.position;
        }
        if (interactor is XRDirectInteractor direct) return direct.GetAttachTransform(interactable).position;
        return interactor.transform.position;
    }

    // True when any active interaction collider of the grab is within radius of position.
    public static bool AnyColliderWithin(XRGrabInteractable grab, Vector3 position, float radius)
    {
        float radiusSquared = radius * radius;
        foreach (Collider collider in grab.colliders)
        {
            if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                (collider.ClosestPoint(position) - position).sqrMagnitude <= radiusSquared)
                return true;
        }
        return false;
    }
}
