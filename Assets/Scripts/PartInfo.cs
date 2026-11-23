using System.Collections.Generic;
using UnityEngine;

// Display data for a part's info popup. Assets in a Resources/PartInfo folder act as defaults for their
// slot type; a MountablePart can override that with its own asset.
[CreateAssetMenu(fileName = "PartInfo", menuName = "PC Builder/Part Info")]
public class PartInfo : ScriptableObject
{
    public const string ResourcesFolder = "PartInfo";

    public string displayName = "Part";
    [TextArea(2, 5)] public string description;
    [Tooltip("Parts with this slot type and no Part Info of their own use this asset.")]
    public SlotType defaultForSlotType;
    [Tooltip("Turn off for assets that should only be assigned by hand.")]
    public bool useAsSlotTypeDefault = true;

    private static Dictionary<SlotType, PartInfo> defaults;

    public static PartInfo Resolve(MountablePart part)
    {
        if (part == null) return null;
        if (part.info != null) return part.info;
        if (defaults == null)
        {
            defaults = new Dictionary<SlotType, PartInfo>();
            foreach (PartInfo asset in Resources.LoadAll<PartInfo>(ResourcesFolder))
                if (asset.useAsSlotTypeDefault && !defaults.ContainsKey(asset.defaultForSlotType))
                    defaults.Add(asset.defaultForSlotType, asset);
        }
        return defaults.TryGetValue(part.compatibleSlotType, out PartInfo info) ? info : null;
    }

    public static string NameOf(MountablePart part)
    {
        PartInfo info = Resolve(part);
        return info != null && !string.IsNullOrWhiteSpace(info.displayName) ? info.displayName : part.name;
    }

    // Domain reload may be disabled in Play Mode settings; start each session with a fresh lookup.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetDefaults() => defaults = null;
}
