using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;

public static class InstallSketchfabDragon
{
    const string Source="Assets/ThirdParty/MAXDESIGNChineseDragon";
    const string Output="Assets/TianNanGe/Dragon/MAXDESIGN";
    public static string Install()
    {
        var scene=EditorSceneManager.GetActiveScene();
        if(EditorApplication.isPlaying||scene.path!="Assets/TianNanGe/Scenes/TianNanGe_Main.unity")
            throw new Exception("Open TianNanGe_Main in Edit Mode.");
        var root=GameObject.Find("TianNanGe_Blockout/Dragon");
        if(root==null||root.transform.Find("Temporary_MAXDESIGN_ReplaceWithTripo")!=null)
            throw new Exception("Missing Dragon root or model already installed; do not overwrite hand edits.");
        Directory.CreateDirectory(Output);AssetDatabase.Refresh();
        var imp=(ModelImporter)AssetImporter.GetAtPath(Source+"/ChineseDragon.fbx");
        imp.animationType=ModelImporterAnimationType.Generic;imp.importAnimation=true;
        imp.globalScale=.0025f;imp.optimizeGameObjects=false;imp.importCameras=false;imp.importLights=false;
        imp.materialImportMode=ModelImporterMaterialImportMode.None;
        imp.animationCompression=ModelImporterAnimationCompression.Off;imp.SaveAndReimport();
        var normalImp=(TextureImporter)AssetImporter.GetAtPath(Source+"/normal.png");
        normalImp.textureType=TextureImporterType.NormalMap;normalImp.SaveAndReimport();
        var body=new Material(Shader.Find("HDRP/Lit")){name="Dragon_Slate"};
        body.SetColor("_BaseColor",new Color(.16f,.23f,.23f));body.SetFloat("_Smoothness",.3f);
        body.SetTexture("_NormalMap",AssetDatabase.LoadAssetAtPath<Texture2D>(Source+"/normal.png"));
        body.SetFloat("_NormalScale",.65f);HDMaterial.ValidateMaterial(body);
        AssetDatabase.CreateAsset(body,Output+"/Dragon_Slate.mat");
        var eye=new Material(Shader.Find("HDRP/Lit")){name="Dragon_Eye"};
        eye.SetTexture("_BaseColorMap",AssetDatabase.LoadAssetAtPath<Texture2D>(Source+"/dragon_(1).png"));
        eye.SetFloat("_Smoothness",.65f);HDMaterial.ValidateMaterial(eye);
        AssetDatabase.CreateAsset(eye,Output+"/Dragon_Eye.mat");
        var model=AssetDatabase.LoadAssetAtPath<GameObject>(Source+"/ChineseDragon.fbx");
        var clip=AssetDatabase.LoadAllAssetsAtPath(Source+"/ChineseDragon.fbx").OfType<AnimationClip>().First(c=>!c.name.StartsWith("__preview__"));
        var controller=AnimatorController.CreateAnimatorControllerAtPath(Output+"/Dragon.controller");
        var state=controller.layers[0].stateMachine.AddState("SourceMotion");state.motion=clip;
        controller.layers[0].stateMachine.defaultState=state;
        var holder=new GameObject("Temporary_MAXDESIGN_ReplaceWithTripo");
        Undo.RegisterCreatedObjectUndo(holder,"Install MAXDESIGN animated dragon");holder.transform.SetParent(root.transform,false);
        var visual=(GameObject)PrefabUtility.InstantiatePrefab(model);visual.name="Visual";visual.transform.SetParent(holder.transform,false);
        var a=visual.GetComponent<Animator>();if(a==null)a=visual.AddComponent<Animator>();
        a.runtimeAnimatorController=controller;a.applyRootMotion=false;a.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        foreach(var r in visual.GetComponentsInChildren<SkinnedMeshRenderer>())
        {r.sharedMaterials=new[]{body,eye};r.updateWhenOffscreen=true;}
        clip.SampleAnimation(visual,0);
        holder.transform.localRotation=Quaternion.Euler(0,-70,10);
        var head=visual.GetComponentInChildren<SkinnedMeshRenderer>().bones[12].parent;
        holder.transform.position+=new Vector3(-1.3f,1.3f,.3f)-head.position;
        foreach(Transform child in root.transform)if(child!=holder.transform){Undo.RecordObject(child.gameObject,"Retain previous dragon as inactive fallback");child.gameObject.SetActive(false);}
        PrefabUtility.SaveAsPrefabAsset(holder,Output+"/TemporaryChineseDragon.prefab");
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject=holder;
        return "Installed MAXDESIGN dragon. "+ConfigurePreview();
    }

    // Explicit setup step; rerunning resets this dragon's preview pose and placement.
    public static string ConfigurePreview()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play Mode first.");
        var holder=GameObject.Find("TianNanGe_Blockout/Dragon/Temporary_MAXDESIGN_ReplaceWithTripo");
        if(holder==null)throw new Exception("Install the dragon first.");
        var imp=(ModelImporter)AssetImporter.GetAtPath(Source+"/ChineseDragon.fbx");
        imp.clipAnimations=new[]{
            new ModelImporterClipAnimation{name="SourceMotion",takeName="Take 001",firstFrame=0,lastFrame=1725},
            new ModelImporterClipAnimation{name="IdlePreview",takeName="Take 001",firstFrame=1604,lastFrame=1724,loopTime=true,loopPose=true},
            new ModelImporterClipAnimation{name="SwayPreview",takeName="Take 001",firstFrame=1422,lastFrame=1542,loopTime=true,loopPose=true}
        };imp.SaveAndReimport();
        var clips=AssetDatabase.LoadAllAssetsAtPath(Source+"/ChineseDragon.fbx").OfType<AnimationClip>().Where(c=>!c.name.StartsWith("__preview__")).ToArray();
        var controller=AssetDatabase.LoadAssetAtPath<AnimatorController>(Output+"/Dragon.controller");
        var sm=controller.layers[0].stateMachine;
        foreach(var old in sm.states)sm.RemoveState(old.state);
        foreach(var c in clips){var st=sm.AddState(c.name);st.motion=c;if(c.name=="IdlePreview"){st.speed=.65f;sm.defaultState=st;}}
        var visual=holder.transform.Find("Visual").gameObject;
        var animator=visual.GetComponent<Animator>();animator.runtimeAnimatorController=controller;
        clips.First(c=>c.name=="IdlePreview").SampleAnimation(visual,0);
        holder.transform.SetLocalPositionAndRotation(Vector3.zero,Quaternion.Euler(0,215,0));
        holder.transform.localScale=Vector3.one*1.4f;
        var head=visual.GetComponentInChildren<SkinnedMeshRenderer>().bones[12].parent;
        holder.transform.position+=new Vector3(-1.3f,1.25f,-.4f)-head.position;
        PrefabUtility.SaveAsPrefabAsset(holder,Output+"/TemporaryChineseDragon.prefab");
        AssetDatabase.SaveAssets();var scene=EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        return "IdlePreview: 1604–1724; SwayPreview: 1422–1542; 30fps; configured and saved.";
    }
}
