using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Ceiling chute that parts are dropped through. Parts handed to Drop() wait out of sight inside the
// shaft while the warning light blinks, then the hatch opens and they fall out one after another.
public class DropChute : MonoBehaviour
{
    [Header("Drop")]
    [Tooltip("Where parts wait and are released from, just above the hatch. Defaults to this transform.")]
    public Transform spawnPoint;
    [Tooltip("Random sideways offset (m) so parts in one drop don't land exactly on each other.")]
    [Min(0f)] public float spawnJitter = 0.08f;
    [Tooltip("Seconds between parts released in the same drop.")]
    [Min(0f)] public float releaseInterval = 0.5f;
    [Tooltip("Downward speed (m/s) parts leave the chute with.")]
    [Min(0f)] public float exitSpeed = 0.5f;
    [Tooltip("Largest random spin (rad/s) given to a released part.")]
    [Min(0f)] public float maxTumble = 1.5f;
    [Tooltip("Collision detection used while a part falls, so thin parts cannot pass through the table or floor. " +
             "Speculative also works against surfaces that are themselves moving rigidbodies.")]
    public CollisionDetectionMode fallingCollisionMode = CollisionDetectionMode.ContinuousSpeculative;
    [Tooltip("The part's own collision mode is restored once it comes to rest, or after this many seconds.")]
    [Min(0.1f)] public float maxFallSeconds = 8f;

    [Header("Warning")]
    [Min(0f)] public float warningSeconds = 2f;
    [Min(0.1f)] public float blinksPerSecond = 3f;
    public Light warningLight;
    public Renderer warningBeacon;
    [ColorUsage(false, true)] public Color beaconOnColor = new Color(6f, 3f, 0f);
    [Tooltip("Optional. Played when the warning starts.")]
    public AudioSource warningSound;
    [Tooltip("Optional. Played as each part is released.")]
    public AudioSource releaseSound;

    [Header("Hatch")]
    [Tooltip("Rotates around its local Z axis to swing the left door down.")]
    public Transform leftDoorHinge;
    [Tooltip("Rotates around its local Z axis to swing the right door down.")]
    public Transform rightDoorHinge;
    [Range(0f, 180f)] public float doorOpenAngle = 100f;
    [Min(0.01f)] public float doorSwingSeconds = 0.35f;
    [Tooltip("Seconds the hatch stays open after the last part of a drop leaves.")]
    [Min(0f)] public float holdOpenSeconds = 1f;

    public event Action<Rigidbody> PartReleased;

    public bool IsBusy => running != null || batches.Count > 0;
    public bool IsPending(Rigidbody body) => body != null && pending.Contains(body);
    public Transform SpawnPoint => spawnPoint != null ? spawnPoint : transform;

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private readonly Queue<List<Rigidbody>> batches = new Queue<List<Rigidbody>>();
    private readonly HashSet<Rigidbody> pending = new HashSet<Rigidbody>();
    private readonly Dictionary<Rigidbody, CollisionDetectionMode> ownModes = new Dictionary<Rigidbody, CollisionDetectionMode>();
    private Coroutine running;
    private Quaternion leftClosed = Quaternion.identity;
    private Quaternion rightClosed = Quaternion.identity;
    private MaterialPropertyBlock beaconBlock;

    private void Awake()
    {
        if (leftDoorHinge != null) leftClosed = leftDoorHinge.localRotation;
        if (rightDoorHinge != null) rightClosed = rightDoorHinge.localRotation;
        SetWarning(false);
    }

    private void OnDisable()
    {
        // Parts still waiting inside fall straight out rather than staying frozen in the shaft.
        var waiting = new List<Rigidbody>(pending);
        CancelAll();
        foreach (Rigidbody body in waiting) if (body != null) Release(body, false);
    }

    public void Drop(Rigidbody body) => Drop(new[] { body });

    // Queues the bodies as one drop. Bodies already waiting in the chute are ignored.
    public void Drop(IEnumerable<Rigidbody> bodies)
    {
        var batch = new List<Rigidbody>();
        foreach (Rigidbody body in bodies)
        {
            if (body == null || !pending.Add(body)) continue;
            Park(body);
            batch.Add(body);
        }
        if (batch.Count == 0) return;
        batches.Enqueue(batch);
        if (running == null && isActiveAndEnabled) running = StartCoroutine(Run());
    }

    // Forgets every queued drop. Waiting parts are left where they are, collisions back on, for the caller to place.
    public void CancelAll()
    {
        if (running != null) StopCoroutine(running);
        running = null;
        batches.Clear();
        foreach (Rigidbody body in pending)
        {
            if (body == null) continue;
            body.detectCollisions = true;
            RestoreOwnMode(body);
        }
        pending.Clear();
        SetWarning(false);
        if (leftDoorHinge != null) leftDoorHinge.localRotation = leftClosed;
        if (rightDoorHinge != null) rightDoorHinge.localRotation = rightClosed;
    }

    private void Park(Rigidbody body)
    {
        GrabUtility.ForceRelease(body);
        var part = body.GetComponent<MountablePart>();
        if (part != null && part.IsMounted) part.MountedSlot.Vacate();
        if (!body.gameObject.activeSelf) body.gameObject.SetActive(true);
        if (!ownModes.ContainsKey(body)) ownModes[body] = body.collisionDetectionMode;
        if (!body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        // Hidden inside the shaft, frozen and not colliding with other waiting parts.
        body.isKinematic = true;
        body.detectCollisions = false;
        Place(body, SpawnPoint.position, body.rotation);
    }

    private IEnumerator Run()
    {
        // Never finish within StartCoroutine itself, or `running` would be assigned after Run had already ended.
        yield return null;
        while (batches.Count > 0)
        {
            List<Rigidbody> batch = batches.Dequeue();
            yield return Warn();
            yield return SwingDoors(true);
            for (int i = 0; i < batch.Count; i++)
            {
                if (batch[i] != null && pending.Contains(batch[i])) Release(batch[i], true);
                if (releaseInterval > 0f && i < batch.Count - 1) yield return new WaitForSeconds(releaseInterval);
            }
            if (holdOpenSeconds > 0f) yield return new WaitForSeconds(holdOpenSeconds);
            SetWarning(false);
            yield return SwingDoors(false);
        }
        running = null;
    }

    private void Release(Rigidbody body, bool notify)
    {
        pending.Remove(body);
        Vector2 jitter = UnityEngine.Random.insideUnitCircle * spawnJitter;
        Vector3 position = SpawnPoint.position + new Vector3(jitter.x, 0f, jitter.y);
        Quaternion rotation = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), Vector3.up) * body.rotation;
        Place(body, position, rotation);
        body.detectCollisions = true;
        body.isKinematic = false;
        body.useGravity = true;
        body.collisionDetectionMode = fallingCollisionMode;
        body.linearVelocity = Vector3.down * exitSpeed;
        body.angularVelocity = UnityEngine.Random.insideUnitSphere * maxTumble;
        body.WakeUp();
        if (!notify) { RestoreOwnMode(body); return; }
        if (releaseSound != null) releaseSound.Play();
        PartReleased?.Invoke(body);
        StartCoroutine(RestoreModeWhenSettled(body));
    }

    private IEnumerator RestoreModeWhenSettled(Rigidbody body)
    {
        float started = Time.time;
        float still = 0f;
        yield return new WaitForFixedUpdate();
        while (body != null && !pending.Contains(body) && !body.isKinematic && Time.time - started < maxFallSeconds)
        {
            still = body.linearVelocity.sqrMagnitude < 0.0025f ? still + Time.deltaTime : 0f;
            if (still >= 0.3f || body.IsSleeping()) break;
            yield return null;
        }
        // Parked again meanwhile: that drop restores it instead.
        if (body != null && !pending.Contains(body)) RestoreOwnMode(body);
    }

    private void RestoreOwnMode(Rigidbody body)
    {
        if (!ownModes.TryGetValue(body, out CollisionDetectionMode mode)) return;
        ownModes.Remove(body);
        // Kinematic bodies only support discrete or speculative detection.
        if (body.isKinematic && (mode == CollisionDetectionMode.Continuous || mode == CollisionDetectionMode.ContinuousDynamic))
            mode = CollisionDetectionMode.ContinuousSpeculative;
        body.collisionDetectionMode = mode;
    }

    private static void Place(Rigidbody body, Vector3 position, Quaternion rotation)
    {
        body.transform.SetPositionAndRotation(position, rotation);
        body.position = position;
        body.rotation = rotation;
    }

    private IEnumerator Warn()
    {
        if (warningSound != null) warningSound.Play();
        for (float t = 0f; t < warningSeconds; t += Time.deltaTime)
        {
            SetWarning(Mathf.Repeat(t * blinksPerSecond, 1f) < 0.5f);
            yield return null;
        }
        SetWarning(true);
    }

    private IEnumerator SwingDoors(bool open)
    {
        if (leftDoorHinge == null && rightDoorHinge == null) yield break;
        Quaternion leftTarget = open ? leftClosed * Quaternion.Euler(0f, 0f, -doorOpenAngle) : leftClosed;
        Quaternion rightTarget = open ? rightClosed * Quaternion.Euler(0f, 0f, doorOpenAngle) : rightClosed;
        Quaternion leftFrom = leftDoorHinge != null ? leftDoorHinge.localRotation : Quaternion.identity;
        Quaternion rightFrom = rightDoorHinge != null ? rightDoorHinge.localRotation : Quaternion.identity;
        for (float t = 0f; t < 1f; t += Time.deltaTime / doorSwingSeconds)
        {
            float eased = Mathf.SmoothStep(0f, 1f, t);
            if (leftDoorHinge != null) leftDoorHinge.localRotation = Quaternion.Slerp(leftFrom, leftTarget, eased);
            if (rightDoorHinge != null) rightDoorHinge.localRotation = Quaternion.Slerp(rightFrom, rightTarget, eased);
            yield return null;
        }
        if (leftDoorHinge != null) leftDoorHinge.localRotation = leftTarget;
        if (rightDoorHinge != null) rightDoorHinge.localRotation = rightTarget;
    }

    private void SetWarning(bool on)
    {
        if (warningLight != null) warningLight.enabled = on;
        if (warningBeacon == null) return;
        beaconBlock ??= new MaterialPropertyBlock();
        warningBeacon.GetPropertyBlock(beaconBlock);
        beaconBlock.SetColor(EmissionColorId, on ? beaconOnColor : Color.black);
        warningBeacon.SetPropertyBlock(beaconBlock);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.8f, 0f, 0.8f);
        Vector3 from = SpawnPoint.position;
        Gizmos.DrawWireSphere(from, 0.1f);
        if (Physics.Raycast(from + Vector3.down * 0.5f, Vector3.down, out RaycastHit hit, 20f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            Gizmos.DrawLine(from, hit.point);
    }
}
