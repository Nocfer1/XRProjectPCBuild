using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

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

    [TearDown]
    public void Cleanup()
    {
        foreach (GameObject obj in objects)
            if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
        objects.Clear();
    }
}
