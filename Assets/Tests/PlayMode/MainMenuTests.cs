using System;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;

// Assembly-CSharp is a predefined Unity assembly, so tests in an asmdef use reflection.
public class MainMenuTests
{
    private GameObject menuObject;
    private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);

    [TearDown]
    public void TearDown()
    {
        if (menuObject != null) UnityEngine.Object.Destroy(menuObject);
    }

    private Component CreateMenu(float armDelay)
    {
        menuObject = new GameObject("Main Menu");
        Component menu = menuObject.AddComponent(T("MainMenu"));
        Set(menu, "armDelay", armDelay);
        // No scene is loaded, so the tests only exercise the menu itself.
        Set(menu, "gameScene", "");
        return menu;
    }

    private static void Set(object obj, string name, object value) => obj.GetType().GetField(name).SetValue(obj, value);
    private static bool Get(object obj, string name) => (bool)obj.GetType().GetProperty(name).GetValue(obj);
    private static bool TryStart(object menu) => (bool)menu.GetType().GetMethod("TryStart").Invoke(menu, null);

    [Test]
    public void PressBeforeArmDelayIsIgnored()
    {
        Component menu = CreateMenu(10f);

        Assert.IsFalse(Get(menu, "IsArmed"));
        Assert.IsFalse(TryStart(menu));
        Assert.IsFalse(Get(menu, "HasStarted"));
    }

    [UnityTest]
    public IEnumerator PressAfterArmDelayStartsOnce()
    {
        Component menu = CreateMenu(0.05f);
        int starts = 0;
        ((UnityEvent)menu.GetType().GetField("onStart").GetValue(menu)).AddListener(() => starts++);

        yield return new WaitForSecondsRealtime(0.1f);

        Assert.IsTrue(Get(menu, "IsArmed"));
        Assert.IsTrue(TryStart(menu));
        Assert.IsTrue(Get(menu, "HasStarted"));
        Assert.IsFalse(TryStart(menu), "A second press must not start the game again.");
        Assert.AreEqual(1, starts);
    }

    [UnityTest]
    public IEnumerator ReopeningTheMenuWaitsAgain()
    {
        Component menu = CreateMenu(0.05f);
        yield return new WaitForSecondsRealtime(0.1f);
        Assert.IsTrue(TryStart(menu));

        menuObject.SetActive(false);
        Set(menu, "armDelay", 10f);
        menuObject.SetActive(true);

        Assert.IsFalse(Get(menu, "HasStarted"));
        Assert.IsFalse(TryStart(menu));
    }

    [Test]
    public void DisabledMenuDoesNotStart()
    {
        Component menu = CreateMenu(0f);
        ((Behaviour)menu).enabled = false;

        Assert.IsFalse(TryStart(menu));
    }

    [Test]
    public void LoadingASceneMissingFromBuildSettingsIsRefused()
    {
        MethodInfo load = T("SceneFlow").GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
        LogAssert.Expect(LogType.Warning, new Regex("not in Build Settings"));

        Assert.IsFalse((bool)load.Invoke(null, new object[] { "Scene That Does Not Exist" }));
    }
}
