using System.Collections.Generic;
using UnityEngine;

// Sends loose objects that leave the play area (fell through the floor, flung out of the room)
// back through the drop chute. Mounted parts ride along with their host and are not checked.
public class LostPartCatcher : MonoBehaviour
{
    public DropChute chute;
    [Tooltip("Rigidbodies under these roots are watched. Hidden (inactive) ones are skipped.")]
    public List<Transform> watchedRoots = new List<Transform>();
    [Tooltip("Rigidbodies watched in addition to the roots.")]
    public List<Rigidbody> extraBodies = new List<Rigidbody>();
    [Tooltip("World-space area a loose object must stay inside.")]
    public Bounds playArea = new Bounds(Vector3.zero, new Vector3(20f, 20f, 20f));
    [Min(0.02f)] public float checkInterval = 0.25f;

    private readonly List<Rigidbody> buffer = new List<Rigidbody>();
    private float nextCheck;

    private void Update()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + checkInterval;
        CheckNow();
    }

    // Returns how many lost objects were sent back.
    public int CheckNow()
    {
        if (chute == null || !chute.isActiveAndEnabled) return 0;
        int sent = 0;
        foreach (Transform root in watchedRoots)
        {
            if (root == null) continue;
            root.GetComponentsInChildren(false, buffer);
            foreach (Rigidbody body in buffer)
                if (TrySendBack(body)) sent++;
        }
        buffer.Clear();
        foreach (Rigidbody body in extraBodies)
            if (TrySendBack(body)) sent++;
        return sent;
    }

    private bool TrySendBack(Rigidbody body)
    {
        if (body == null || !body.gameObject.activeInHierarchy || chute.IsPending(body)) return false;
        if (playArea.Contains(body.position) || GrabUtility.IsHeld(body)) return false;
        var part = body.GetComponent<MountablePart>();
        if (part != null && part.IsMounted) return false;
        chute.Drop(body);
        return true;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.6f);
        Gizmos.DrawWireCube(playArea.center, playArea.size);
    }
}
