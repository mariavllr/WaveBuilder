// Menú de editor: crea una escena mínima con el RuntimeBenchmarkRunner ya configurado.
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class RuntimeBenchmarkMenu
{
    private const string ScenePath = "Assets/Scenes/ARTICLE/RuntimeBenchmark.unity";

    [MenuItem("WFC/Runtime Benchmark/Crear escena de benchmark")]
    private static void CreateScene()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cam = new GameObject("Main Camera");
        cam.AddComponent<Camera>();
        cam.tag = "MainCamera";

        var go = new GameObject("RuntimeBenchmark");
        var runner = go.AddComponent<RuntimeBenchmarkRunner>();
        runner.AutofillArticleTilesets();

        EditorSceneManager.SaveScene(scene, ScenePath);
        Selection.activeGameObject = go;
        Debug.Log("[RuntimeBenchmark] Escena creada en " + ScenePath + ". Revisa el Inspector y pulsa Play.");
    }

    [MenuItem("WFC/Runtime Benchmark/Abrir carpeta de resultados")]
    private static void OpenResults()
    {
        string dir = System.IO.Path.Combine(Application.persistentDataPath, "RuntimeBenchmark");
        System.IO.Directory.CreateDirectory(dir);
        EditorUtility.RevealInFinder(dir);
    }
}
