using System.Collections.Generic;
using UnityEngine;

// The case's simplified walls overlap the PSU bay. Ignore only this PSU/case
// pair, including during insertion, so the socket remains physically reachable.
public class PsuCaseCollision : MonoBehaviour
{
    public Collider psuCollider;
    public Rigidbody caseBody;

    private readonly List<(Collider collider, bool wasIgnored)> pairs = new List<(Collider, bool)>();

    public void Configure(Collider source, Rigidbody host)
    {
        if (isActiveAndEnabled) OnDisable();
        psuCollider = source;
        caseBody = host;
        if (isActiveAndEnabled) OnEnable();
    }

    private void OnEnable()
    {
        pairs.Clear();
        if (psuCollider == null || caseBody == null) return;
        foreach (var collider in caseBody.GetComponentsInChildren<Collider>(true))
        {
            if (collider == psuCollider || collider.isTrigger || collider.attachedRigidbody != caseBody) continue;
            bool active = psuCollider.enabled && psuCollider.gameObject.activeInHierarchy &&
                          collider.enabled && collider.gameObject.activeInHierarchy;
            pairs.Add((collider, active && Physics.GetIgnoreCollision(psuCollider, collider)));
        }
        RefreshPairs();
    }

    private void FixedUpdate() => RefreshPairs();

    private void RefreshPairs()
    {
        if (psuCollider == null || !psuCollider.enabled || !psuCollider.gameObject.activeInHierarchy) return;
        foreach (var pair in pairs)
            if (pair.collider != null && pair.collider.enabled && pair.collider.gameObject.activeInHierarchy &&
                !Physics.GetIgnoreCollision(psuCollider, pair.collider))
                Physics.IgnoreCollision(psuCollider, pair.collider, true);
    }

    private void OnDisable()
    {
        if (psuCollider != null && psuCollider.enabled && psuCollider.gameObject.activeInHierarchy)
            foreach (var pair in pairs)
                if (!pair.wasIgnored && pair.collider != null && pair.collider.enabled && pair.collider.gameObject.activeInHierarchy)
                    Physics.IgnoreCollision(psuCollider, pair.collider, false);
        pairs.Clear();
    }
}
