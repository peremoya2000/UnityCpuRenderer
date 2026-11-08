using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class E1M1SceneBuilder
{
    private const string FOLDER = "Assets/E1M1";
    private const string MODEL_PATH = FOLDER + "/e1m1.obj";
    private const string MATERIAL_LIBRARY_PATH = FOLDER + "/e1m1.mtl";
    private const string MATERIALS_FOLDER = FOLDER + "/Materials";
    private const string SCENE_PATH = FOLDER + "/E1M1.unity";
    private const string LEVEL_DATA_PATH = FOLDER + "/Source~/e1m1_entities.json";
    private const float BOUNDS_TOLERANCE = 0.01f;
    private const StaticEditorFlags WORLD_FLAGS = StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;
    private const StaticEditorFlags MOVER_FLAGS = StaticEditorFlags.ContributeGI;

    [MenuItem("Tools/Build E1M1 Scene")]
    private static void Build()
    {
        if (File.Exists(SCENE_PATH) && !EditorUtility.DisplayDialog("Build E1M1 Scene", SCENE_PATH + " already exists and will be overwritten.", "Overwrite", "Cancel")) return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        LevelData level = JsonUtility.FromJson<LevelData>(File.ReadAllText(LEVEL_DATA_PATH));
        CreateMaterials();
        ConfigureModelImport();
        BuildScene(level);
    }

    private static void CreateMaterials()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        Debug.Assert(lit != null, "The URP Lit shader was not found.");
        if (!AssetDatabase.IsValidFolder(MATERIALS_FOLDER)) AssetDatabase.CreateFolder(FOLDER, "Materials");

        string materialName = null;
        foreach (string line in File.ReadAllLines(MATERIAL_LIBRARY_PATH))
        {
            if (line.StartsWith("newmtl "))
            {
                materialName = line.Substring("newmtl ".Length);
            }
            else if (line.StartsWith("map_Kd "))
            {
                string texturePath = FOLDER + "/" + line.Substring("map_Kd ".Length);
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                Debug.Assert(texture != null, $"Texture {texturePath} is missing.");

                string materialPath = MATERIALS_FOLDER + "/" + materialName + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    material = new Material(lit);
                    AssetDatabase.CreateAsset(material, materialPath);
                }
                material.SetTexture("_BaseMap", texture);
                material.SetFloat("_Smoothness", 0f);
                EditorUtility.SetDirty(material);
            }
        }
        AssetDatabase.SaveAssets();
    }

    private static void ConfigureModelImport()
    {
        ModelImporter importer = (ModelImporter)AssetImporter.GetAtPath(MODEL_PATH);
        importer.useFileScale = false;
        importer.globalScale = 1f;
        importer.isReadable = true;
        importer.importNormals = ModelImporterNormals.Import;
        importer.generateSecondaryUV = true;
        importer.addCollider = true;
        importer.importAnimation = false;
        importer.animationType = ModelImporterAnimationType.None;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
        importer.SaveAndReimport();

        importer.SearchAndRemapMaterials(ModelImporterMaterialName.BasedOnMaterialName, ModelImporterMaterialSearch.Local);
        importer.SaveAndReimport();
    }

    private static void BuildScene(LevelData level)
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(MODEL_PATH), scene);
        model.name = "E1M1";
        Debug.Assert(model.transform.childCount == level.Objects.Length, $"The model imported as {model.transform.childCount} objects, the converter wrote {level.Objects.Length}.");

        int triangles = 0;
        Bounds bounds = model.GetComponentInChildren<MeshRenderer>().bounds;
        foreach (ObjectData data in level.Objects)
        {
            Transform child = model.transform.Find(data.Name);
            Debug.Assert(child != null, $"The imported model has no object named {data.Name}.");
            GameObjectUtility.SetStaticEditorFlags(child.gameObject, data.IsStatic ? WORLD_FLAGS : MOVER_FLAGS);

            Mesh mesh = child.GetComponent<MeshFilter>().sharedMesh;
            int childTriangles = mesh.triangles.Length / 3;
            Debug.Assert(childTriangles == data.Triangles, $"{data.Name} imported with {childTriangles} triangles, the converter wrote {data.Triangles}.");
            int inverted = CountTrianglesWoundAgainstNormals(mesh);
            Debug.Assert(inverted == 0, $"{data.Name} has {inverted} triangles wound against their normals, so they face the wrong way.");
            triangles += childTriangles;

            MeshRenderer renderer = child.GetComponent<MeshRenderer>();
            bounds.Encapsulate(renderer.bounds);
            foreach (Material material in renderer.sharedMaterials)
            {
                Debug.Assert(AssetDatabase.GetAssetPath(material).StartsWith(MATERIALS_FOLDER), $"{data.Name} uses {material.name}, which was not remapped to {MATERIALS_FOLDER}.");
            }
            child.localPosition = data.StartOffset;
        }
        Debug.Assert(triangles == level.Triangles, $"The model imported with {triangles} triangles, the converter wrote {level.Triangles}.");
        Debug.Assert(Vector3.Distance(bounds.min, level.BoundsMin) < BOUNDS_TOLERANCE && Vector3.Distance(bounds.max, level.BoundsMax) < BOUNDS_TOLERANCE,
            $"The model imported with bounds {bounds.min}..{bounds.max}, the converter wrote {level.BoundsMin}..{level.BoundsMax}. The axis or scale conversion is wrong.");

        Transform lights = new GameObject("Lights").transform;
        foreach (LightData data in level.Lights)
        {
            GameObject lightObject = new GameObject(data.Name);
            lightObject.transform.SetParent(lights, false);
            lightObject.transform.localPosition = data.Position;
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = data.Range;
            light.intensity = data.Intensity;
            light.shadows = LightShadows.Hard;
            light.lightmapBakeType = LightmapBakeType.Mixed;
            light.enabled = !data.StartsOff;
        }

        Transform views = new GameObject("Views").transform;
        foreach (ViewData data in level.Views)
        {
            Transform view = new GameObject(data.Name).transform;
            view.SetParent(views, false);
            view.SetLocalPositionAndRotation(data.Position, Quaternion.Euler(data.Rotation));
        }

        EditorSceneManager.SaveScene(scene, SCENE_PATH);
        Debug.Log($"Built {SCENE_PATH}: {level.Objects.Length} objects, {triangles} triangles, {level.Lights.Length} lights, {level.Views.Length} views.");
    }

    private static int CountTrianglesWoundAgainstNormals(Mesh mesh)
    {
        int[] indices = mesh.triangles;
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        int inverted = 0;
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a = vertices[indices[i]];
            Vector3 windingNormal = Vector3.Cross(vertices[indices[i + 1]] - a, vertices[indices[i + 2]] - a);
            if (Vector3.Dot(windingNormal, normals[indices[i]]) <= 0f) inverted++;
        }
        return inverted;
    }

    [System.Serializable]
    public class LevelData
    {
        public int Triangles;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public ObjectData[] Objects;
        public LightData[] Lights;
        public ViewData[] Views;
    }

    [System.Serializable]
    public class ObjectData
    {
        public string Name;
        public bool IsStatic;
        public int Triangles;
        public Vector3 StartOffset;
    }

    [System.Serializable]
    public class LightData
    {
        public string Name;
        public Vector3 Position;
        public float Range;
        public float Intensity;
        public bool StartsOff;
    }

    [System.Serializable]
    public class ViewData
    {
        public string Name;
        public Vector3 Position;
        public Vector3 Rotation;
    }
}
