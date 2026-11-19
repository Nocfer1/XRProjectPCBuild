using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

// Assembly-CSharp is a predefined Unity assembly, so tests in an asmdef use reflection.
public class SnapLogicTests
{
    private readonly List<GameObject> objects = new List<GameObject>();
    private Type PartType => Type.GetType("MountablePart, Assembly-CSharp", true);
    private Type SlotType => Type.GetType("SnapPoint, Assembly-CSharp", true);

    private GameObject CreateObject(string name)
    {
        var obj = new GameObject(name);
        objects.Add(obj);
        return obj;
    }

    private static object Call(Component obj, string name, params object[] args) =>
        obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public).Invoke(obj, args);
    private static object Get(Component obj, string name) => obj.GetType().GetProperty(name).GetValue(obj);
    private static void Set(Component obj, string name, object value) => obj.GetType().GetField(name).SetValue(obj, value);

    private Component CreateSlot(string name, Vector3 position, Quaternion rotation)
    {
        GameObject obj = CreateObject(name);
        obj.transform.SetPositionAndRotation(position, rotation);
        obj.AddComponent<SphereCollider>().isTrigger = true;
        return obj.AddComponent(SlotType);
    }

    [UnityTest]
    public IEnumerator SelectsNearestValidSocketAndAlignsOffsetPivot()
    {
        GameObject partObject = CreateObject("part");
        Component part = partObject.AddComponent(PartType);
        Transform pivot = CreateObject("pivot").transform;
        pivot.SetParent(partObject.transform, false);
        pivot.localPosition = new Vector3(0.005f, 0f, 0f);
        Set(part, "alignmentPivot", pivot);
        Component invalid = CreateSlot("near wrong rotation", pivot.position + Vector3.right * 0.003f, Quaternion.Euler(0, 90, 0));
        Component valid = CreateSlot("valid", pivot.position + Vector3.right * 0.01f, Quaternion.identity);

        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Assert.That(Get(part, "MountedSlot"), Is.SameAs(valid));
        Assert.That((bool)Get(invalid, "isOccupied"), Is.False);
        Assert.That(Vector3.Distance(pivot.position, valid.transform.position), Is.LessThan(0.0001f));
        Assert.That(Quaternion.Angle(pivot.rotation, valid.transform.rotation), Is.LessThan(0.01f));
        yield return null;
    }

    [UnityTest]
    public IEnumerator OccupancyAndPhysicsAreRestoredOnUnmount()
    {
        GameObject partObject = CreateObject("part");
        Component part = partObject.AddComponent(PartType);
        Rigidbody body = partObject.GetComponent<Rigidbody>();
        body.isKinematic = false;
        body.useGravity = true;
        Component slot = CreateSlot("socket", partObject.transform.position, Quaternion.identity);

        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Assert.That((bool)Get(slot, "isOccupied"), Is.True);
        Assert.That(body.isKinematic, Is.True);
        Component other = CreateObject("other").AddComponent(PartType);
        Assert.That((bool)Call(slot, "TrySeat", other), Is.False);

        Call(part, "Unmount");
        Assert.That((bool)Get(slot, "isOccupied"), Is.False);
        Assert.That((bool)Get(part, "IsMounted"), Is.False);
        Assert.That(body.isKinematic, Is.False);
        Assert.That(body.useGravity, Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator SeatedPartIgnoresHostCollisionsAndRestoresThemOnRemoval()
    {
        GameObject host = CreateObject("motherboard");
        host.AddComponent<Rigidbody>().isKinematic = true;
        Collider hostCollider = host.AddComponent<BoxCollider>();
        Component slot = CreateSlot("socket", Vector3.zero, Quaternion.identity);
        slot.transform.SetParent(host.transform, true);

        GameObject partObject = CreateObject("cpu");
        Collider partCollider = partObject.AddComponent<BoxCollider>();
        Component part = partObject.AddComponent(PartType);
        Collider otherCollider = CreateObject("other object").AddComponent<BoxCollider>();

        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Assert.That(Physics.GetIgnoreCollision(partCollider, hostCollider), Is.True);
        Assert.That(Physics.GetIgnoreCollision(partCollider, otherCollider), Is.False);

        Call(part, "Unmount");
        Assert.That(Physics.GetIgnoreCollision(partCollider, hostCollider), Is.False);
        Physics.IgnoreCollision(partCollider, hostCollider, true);
        Assert.That((bool)Call(part, "TryMountToNearbySlot"), Is.True);
        Call(part, "Unmount");
        Assert.That(Physics.GetIgnoreCollision(partCollider, hostCollider), Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator PcPartsRequireTheNearCasterAndKeepTheFilterAfterReparenting()
    {
        GameObject root = CreateObject("PC Components");
        GameObject part = CreateObject("part");
        part.transform.SetParent(root.transform, false);
        BoxCollider collider = part.AddComponent<BoxCollider>();
        collider.size = Vector3.one * 0.02f;
        XRGrabInteractable grab = part.AddComponent<XRGrabInteractable>();
        Type filterType = Type.GetType("NearOnlyGrabFilter, Assembly-CSharp", true);
        IXRSelectFilter filter = (IXRSelectFilter)root.AddComponent(filterType);

        GameObject hand = CreateObject("hand");
        NearFarInteractor nearFar = hand.AddComponent<NearFarInteractor>();
        SphereInteractionCaster sphere = nearFar.nearInteractionCaster as SphereInteractionCaster;
        Assert.That(sphere, Is.Not.Null);
        sphere.castRadius = 0.1f;
        sphere.castOrigin = hand.transform;
        hand.transform.position = new Vector3(0.5f, 0f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.False);

        hand.transform.position = new Vector3(0.05f, 0f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.True);
        part.transform.SetParent(null, true);
        var filters = new List<IXRSelectFilter>();
        grab.selectFilters.GetAll(filters);
        Assert.That(filters, Does.Contain(filter));
        hand.transform.position = new Vector3(0.5f, 0f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.False);
        XRRayInteractor ray = CreateObject("ray").AddComponent<XRRayInteractor>();
        Assert.That(filter.Process(ray, grab), Is.False);
        XRDirectInteractor direct = CreateObject("direct hand").AddComponent<XRDirectInteractor>();
        Assert.That(filter.Process(direct, grab), Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator MountedPartRequiresCloseFrontApproachButLoosePartKeepsNormalRange()
    {
        GameObject root = CreateObject("PC Components");
        GameObject cpu = CreateObject("cpu");
        cpu.transform.SetParent(root.transform, false);
        cpu.AddComponent<BoxCollider>().size = Vector3.one * 0.02f;
        Component part = cpu.AddComponent(PartType);
        XRGrabInteractable grab = cpu.GetComponent<XRGrabInteractable>();
        Type filterType = Type.GetType("NearOnlyGrabFilter, Assembly-CSharp", true);
        IXRSelectFilter filter = (IXRSelectFilter)root.AddComponent(filterType);
        Component slot = CreateSlot("socket", Vector3.zero, Quaternion.identity);
        Set(slot, "limitGrabDistance", true);
        Set(slot, "mountedGrabDistance", 0.015f);
        Set(slot, "requireApproachDirection", true);
        Assert.That((bool)Call(slot, "TrySeat", part), Is.True);

        GameObject hand = CreateObject("hand");
        NearFarInteractor nearFar = hand.AddComponent<NearFarInteractor>();
        SphereInteractionCaster sphere = nearFar.nearInteractionCaster as SphereInteractionCaster;
        Assert.That(sphere, Is.Not.Null);
        sphere.castRadius = 0.1f;
        sphere.castOrigin = hand.transform;
        Physics.SyncTransforms();

        hand.transform.position = new Vector3(0f, 0.02f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.True, "Close approach from the front");
        hand.transform.position = new Vector3(0f, -0.02f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.False, "Behind the socket");
        hand.transform.position = new Vector3(0.04f, 0.005f, 0f);
        Assert.That(filter.Process(nearFar, grab), Is.False, "Too far to the side while seated");
        Call(part, "Unmount");
        Physics.SyncTransforms();
        Assert.That(filter.Process(nearFar, grab), Is.True, "Normal near range after removal");
        yield return null;
    }

    [UnityTest]
    public IEnumerator RemovalSettingsAreIndependentPerSocketAndCanLockPart()
    {
        GameObject root = CreateObject("PC Components");
        GameObject cpu = CreateObject("cpu");
        cpu.transform.SetParent(root.transform, false);
        cpu.AddComponent<BoxCollider>().size = Vector3.one * 0.02f;
        Component part = cpu.AddComponent(PartType);
        XRGrabInteractable grab = cpu.GetComponent<XRGrabInteractable>();
        Type filterType = Type.GetType("NearOnlyGrabFilter, Assembly-CSharp", true);
        IXRSelectFilter nearFilter = (IXRSelectFilter)root.AddComponent(filterType);
        Component cpuSlot = CreateSlot("cpu socket", Vector3.zero, Quaternion.identity);
        Component otherSlot = CreateSlot("other socket", new Vector3(1f, 0f, 0f), Quaternion.identity);
        Set(cpuSlot, "limitGrabDistance", true);
        Set(cpuSlot, "requireApproachDirection", true);
        Assert.That((bool)Call(cpuSlot, "TrySeat", part), Is.True);

        GameObject hand = CreateObject("hand");
        NearFarInteractor nearFar = hand.AddComponent<NearFarInteractor>();
        SphereInteractionCaster sphere = (SphereInteractionCaster)nearFar.nearInteractionCaster;
        sphere.castRadius = 0.1f;
        sphere.castOrigin = hand.transform;
        hand.transform.position = new Vector3(0f, -0.02f, 0f);
        Physics.SyncTransforms();
        Assert.That(nearFilter.Process(nearFar, grab), Is.False, "CPU rejects behind");
        Assert.That((bool)Call(otherSlot, "CanRemoveFrom", hand.transform.position, grab), Is.True,
            "Other socket defaults to proximity-only");

        hand.transform.position = new Vector3(0f, 0.02f, 0f);
        Set(cpuSlot, "allowRemoval", false);
        Assert.That(nearFilter.Process(nearFar, grab), Is.False);
        Assert.That(((IXRSelectFilter)part).Process(nearFar, grab), Is.False,
            "Non-removable policy also blocks selection without the group filter");

        Set(cpuSlot, "allowRemoval", true);
        Set(cpuSlot, "limitGrabDistance", false);
        Transform sideApproach = CreateObject("side approach").transform;
        sideApproach.rotation = Quaternion.Euler(0f, 0f, 90f);
        Set(cpuSlot, "removalApproach", sideApproach);
        GameObject regionObject = CreateObject("grip region");
        regionObject.transform.position = new Vector3(-0.005f, 0f, 0f);
        SphereCollider region = regionObject.AddComponent<SphereCollider>();
        region.radius = 0.006f;
        region.isTrigger = true;
        Set(cpuSlot, "removalGripRegion", region);
        Physics.SyncTransforms();
        Assert.That((bool)Call(cpuSlot, "CanRemoveFrom", new Vector3(-0.005f, 0f, 0f), grab), Is.True);
        Assert.That((bool)Call(cpuSlot, "CanRemoveFrom", new Vector3(0.005f, 0f, 0f), grab), Is.False,
            "Wrong side of approach plane");
        Assert.That((bool)Call(cpuSlot, "CanRemoveFrom", new Vector3(-0.03f, 0f, 0f), grab), Is.False,
            "Outside grip region");
        yield return null;
    }

    [TearDown]
    public void Cleanup()
    {
        foreach (GameObject obj in objects)
            if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
        objects.Clear();
    }
}
