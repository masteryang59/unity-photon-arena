using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonArena.Editor
{
    /// <summary>
    /// Generates the one scene the game needs. The scene is deliberately small (camera, light, ArenaApp); the
    /// arena, avatars and UI are built at runtime from code, so there's nothing to hand-wire.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneBuilder
    {
        // The scene is generated, not committed: build it the first time the project opens.
        static SceneBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (!File.Exists(ScenePath) && !EditorApplication.isPlayingOrWillChangePlaymode) Build();
            };
        }

        public const string ScenePath = "Assets/PhotonArena/Scenes/Arena.unity";
        const string MaterialDir = "Assets/PhotonArena/Materials";

        [MenuItem("Photon Arena/Rebuild Scene")]
        public static void Build()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            Directory.CreateDirectory(MaterialDir);
            Material lit = MaterialAsset("Lit", "Standard", m =>
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            });
            Material unlit = MaterialAsset("Unlit", "Sprites/Default", null);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraGo.tag = "MainCamera";

            var lightGo = new GameObject("Sun", typeof(Light));
            var light = lightGo.GetComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.6f;
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.5f);
            RenderSettings.skybox = null;

            var app = new GameObject("ArenaApp").AddComponent<ArenaApp>();
            var so = new SerializedObject(app);
            so.FindProperty("litTemplate").objectReferenceValue = lit;
            so.FindProperty("unlitTemplate").objectReferenceValue = unlit;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("Photon Arena: scene written to " + ScenePath);
        }

        static Material MaterialAsset(string name, string shaderName, System.Action<Material> configure)
        {
            string path = $"{MaterialDir}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find(shaderName)) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            configure?.Invoke(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>Entry point for <c>-executeMethod</c> (see scripts/unity.ps1).</summary>
        public static void BuildFromCommandLine()
        {
            Build();
            EditorApplication.Exit(0);
        }
    }
}


    public static object EvictStale(object data = null)
    {
        if (data == null) return null;
        return data;
    }
