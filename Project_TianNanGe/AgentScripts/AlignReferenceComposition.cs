using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Explicit editor operation. Absolute poses make repeated adjustments idempotent.
public static class AlignReferenceComposition
{
    static Vector3 V(JToken v) => new Vector3((float)v[0], (float)v[1], (float)v[2]);
    static float[] A(Vector3 v) => new [] {v.x, v.y, v.z};
    static Bounds BoundsOf(Transform root) {
        var rs = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
        if (rs.Length == 0) throw new Exception("No active renderers: " + root.name);
        var b=rs[0].bounds; foreach(var r in rs.Skip(1)) b.Encapsulate(r.bounds); return b;
    }
    static string[] InstalledTripoMountains(Transform support)
    {
        // InstallTripoMountain permits any instance name, but its independent
        // prefab always contains ModelYaw/Visual and lives under this asset root.
        // Include inactive instances: moving their parent would also corrupt the
        // saved world-space fit when the instance is enabled again.
        return support.Cast<Transform>().Where(child =>
        {
            string prefab = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(child.gameObject);
            return child.Find("ModelYaw/Visual") != null &&
                !string.IsNullOrEmpty(prefab) &&
                prefab.StartsWith("Assets/TianNanGe/Tripo/", StringComparison.Ordinal) &&
                prefab.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }).Select(child => child.name).ToArray();
    }
    public static string Apply()
    {
        var scene=SceneManager.GetActiveScene();
        if(scene.path!="Assets/TianNanGe/Scenes/TianNanGe_Main.unity" || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new Exception("Open TianNanGe_Main in Edit Mode.");
        var repo=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
        var folder=Path.Combine(repo,"SourceAssets/Composition");
        var plan=JObject.Parse(File.ReadAllText(Path.Combine(folder,"reference-layout.json")));
        var arch=GameObject.Find("TianNanGe_Blockout/Architecture").transform;
        var mountains=GameObject.Find("TianNanGe_Blockout/Mountains").transform;
        var originals=arch.Cast<Transform>().Concat(mountains.Cast<Transform>()).Select(t => new {t.name,position=A(t.position),scale=A(t.localScale),rotation=A(t.eulerAngles)}).ToArray();
        var baseline=Path.Combine(folder,"before-reference-layout.json");
        if(!File.Exists(baseline)) File.WriteAllText(baseline,JsonConvert.SerializeObject(originals,Formatting.Indented)+"\n");
        Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName("Align temple composition to reference");
        foreach(var entry in (JArray)plan["buildings"]){
            var t=arch.Find((string)entry["name"]); if(!t) throw new Exception("Missing " + entry["name"]);
            Undo.RecordObject(t,"Align building");
            t.position=V(entry["position"]);t.localScale=V(entry["scale"]);
            if(entry["rotation"]!=null)t.eulerAngles=V(entry["rotation"]);
            PrefabUtility.RecordPrefabInstancePropertyModifications(t);
        }
        // Procedural supports still follow their building bases. Tripo supports
        // are fitted in world space by InstallTripoMountain; changing their
        // ancestor here would invalidate that fit and move the new colliders.
        var supportReports = new List<object>();
        foreach(var entry in (JArray)plan["supports"]){
            var t=mountains.Find((string)entry["name"]);
            if(!t) throw new Exception("Missing support " + entry["name"]);
            string[] tripoInstances = InstalledTripoMountains(t);
            if(tripoInstances.Length > 0){
                supportReports.Add(new {t.name,action="preserved_tripo_support",instances=tripoInstances,
                    position=A(t.position),scale=A(t.localScale)});
                continue;
            }
            var b=arch.Find((string)entry["building"]);
            var baseRenderer=b.Find("Proxy_ReplaceWithTripo/StoneBase").GetComponent<Renderer>();
            var rock=t.Find("Rock").GetComponent<MeshFilter>().sharedMesh.bounds;
            var snow=t.Find("SnowCap").GetComponent<MeshFilter>().sharedMesh.bounds;
            float diameter=new Vector2(baseRenderer.bounds.size.x,baseRenderer.bounds.size.z).magnitude+1.5f;
            // Procedural top radius is 36% of the broad base radius.
            float horizontal=diameter/(Mathf.Min(rock.size.x,rock.size.z)*.31f);
            float vertical=(float)entry["height"]/snow.size.y;
            Undo.RecordObject(t,"Fit mountain support");
            t.localScale=new Vector3(horizontal,vertical,horizontal);
            t.position=new Vector3(baseRenderer.bounds.center.x,baseRenderer.bounds.min.y+.04f-snow.max.y*vertical,baseRenderer.bounds.center.z);
            PrefabUtility.RecordPrefabInstancePropertyModifications(t);
            supportReports.Add(new {t.name,action="refitted_procedural_support",
                position=A(t.position),scale=A(t.localScale)});
        }
        Physics.SyncTransforms();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        var report=new {scene=scene.path,camera="FristCamera",referenceAspect=4028f/1949f,buildings=((JArray)plan["buildings"]).Select(e=>{
            var t=arch.Find((string)e["name"]);var b=BoundsOf(t);
            return new {t.name,position=A(t.position),scale=A(t.localScale),center=A(b.center),size=A(b.size)};
        }).ToArray(),supports=supportReports};
        File.WriteAllText(Path.Combine(folder,"applied-layout.json"),JsonConvert.SerializeObject(report,Formatting.Indented)+"\n");
        return JsonConvert.SerializeObject(report);
    }
}
