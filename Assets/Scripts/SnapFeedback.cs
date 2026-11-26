using UnityEngine;

// Plays a sound whenever a player snaps any MountablePart into a slot. One of these in the scene
// covers every part, so the parts need no AudioSource of their own. Parts placed by code
// (Reset Stage rebuilding progress) stay silent.
[RequireComponent(typeof(AudioSource))]
public class SnapFeedback : MonoBehaviour
{
    [Tooltip("Played on the AudioSource. When empty, the AudioSource's own clip is used.")]
    public AudioClip snapClip;
    [Range(0f, 1f)] public float volume = 1f;

    private AudioSource source;

    private void Awake() => source = GetComponent<AudioSource>();
    private void OnEnable() => MountablePart.Snapped += OnSnapped;
    private void OnDisable() => MountablePart.Snapped -= OnSnapped;

    private void OnSnapped(MountablePart part, SnapPoint slot)
    {
        AudioClip clip = snapClip != null ? snapClip : source.clip;
        if (clip == null) return;
        // Only audible when the AudioSource's Spatial Blend is above 0 (3D).
        transform.position = slot.Target.position;
        source.PlayOneShot(clip, volume);
    }
}
