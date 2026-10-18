using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
public static class PlaceWalkableBuildings {
 public static string Place(){
  var scene=SceneManager.GetActiveScene();
  var arch=GameObject.Find("Architecture"); if(!arch) throw new Exception("Architecture root not found");
  string[] paths={"Assets/TianNanGe/Prefabs/Architecture/Walkable/TeaHouse_Walkable.prefab","Assets/TianNanGe/Prefabs/Architecture/Walkable/GuestHouse_Walkable.prefab","Assets/TianNanGe/Prefabs/Architecture/Walkable/MainHall_Walkable.prefab","Assets/TianNanGe/Prefabs/Architecture/Walkable/WatchTower_Walkable.prefab"};
  string[] names={"TeaHouse_Walkable_Instance","GuestHouse_Walkable_Instance","MainHall_Walkable_Instance","WatchTower_Walkable_Instance"};
  Vector3[] pos={new Vector3(-11f,-.9f,6.5f),new Vector3(15f,-.8f,12.5f),new Vector3(15.5f,-.6f,25.5f),new Vector3(-12.5f,-1.15f,17.5f)};
  float[] rot={-8f,12f,-10f,18f};
  for(int i=0;i<paths.Length;i++) {if(GameObject.Find(names[i])) continue; var p=AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]); if(!p) throw new Exception("Missing "+paths[i]); var g=(GameObject)PrefabUtility.InstantiatePrefab(p,scene); g.name=names[i]; g.transform.SetParent(arch.transform,false); g.transform.localPosition=pos[i]; g.transform.localRotation=Quaternion.Euler(0,rot[i],0); g.transform.localScale=Vector3.one; EditorUtility.SetDirty(g);}
  EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); AssetDatabase.SaveAssets(); return "Placed 4 walkable buildings under Architecture";
 }
}
