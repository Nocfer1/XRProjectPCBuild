using UnityEngine;
using UnityEngine.SceneManagement;

// Moves between the main menu and the build. Loading a scene replaces the current one, so going
// back to the menu throws the build away and starting again begins from the first stage.
public static class SceneFlow
{
    // Both scenes must be in Build Settings, with the menu first so the game launches into it.
    public const string MenuScene = "MainMenu";
    public const string GameScene = "XRModelTestScene";

    public static bool IsLoading => loading != null && !loading.isDone;

    private static AsyncOperation loading;

    public static void LoadMenu() => Load(MenuScene);
    public static void LoadGame() => Load(GameScene);

    // A second request while a scene is still loading is ignored.
    public static bool Load(string sceneName)
    {
        if (IsLoading) return false;
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning($"SceneFlow: Scene '{sceneName}' is not in Build Settings, so it cannot be loaded.");
            return false;
        }
        loading = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        return loading != null;
    }
}
