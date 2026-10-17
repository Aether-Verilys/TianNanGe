using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;

public static class InstallTemporaryDragon
{
    const string Source="Assets/ThirdParty/NSSMChineseDragon";
    const string Output="Assets/TianNanGe/Dragon";
    public static string Install()
    {
        var scene=EditorSceneManager.GetActiveScene();
        if(EditorApplication.isPlaying||scene.path!="Assets/TianNanGe/Scenes/TianNanGe_Main.unity")
            throw new Exception("Open TianNanGe_Main in Edit Mode.");
        var root=GameObject.Find("TianNanGe_Blockout/Dragon");
        if(root==null||root.transform.Find("Temporary_NSSM_ReplaceWithTripo")!=null)
            throw new Exception("Missing Dragon root or temporary dragon already installed.");
        Directory.CreateDirectory(Output);AssetDatabase.Refresh();
        var importer=(ModelImporter)AssetImporter.GetAtPath(Source+"/ChineseDragon.fbx");
        importer.animationType=ModelImporterAnimationType.Generic;
        importer.importAnimation=true;importer.optimizeGameObjects=false;
        importer.materialImportMode=ModelImporterMaterialImportMode.None;
        importer.animationCompression=ModelImporterAnimationCompression.Off;
        string take=importer.defaultClipAnimations[0].takeName;
        importer.clipAnimations=new[]{
            new ModelImporterClipAnimation{name="SourceMotion",takeName=take,firstFrame=1,lastFrame=270,loopTime=false},
            new ModelImporterClipAnimation{name="IdlePreview",takeName=take,firstFrame=120,lastFrame=210,loopTime=true,loopPose=true},
            new ModelImporterClipAnimation{name="Fly",takeName=take,firstFrame=230,lastFrame=270,loopTime=true,loopPose=true}
        };
        importer.SaveAndReimport();
        var model=AssetDatabase.LoadAssetAtPath<GameObject>(Source+"/ChineseDragon.fbx");
        var clips=AssetDatabase.LoadAllAssetsAtPath(Source+"/ChineseDragon.fbx").OfType<AnimationClip>().Where(c=>!c.name.StartsWith("__preview__")).ToArray();
        var controller=AnimatorController.CreateAnimatorControllerAtPath(Output+"/TemporaryDragon.controller");
        var sm=controller.layers[0].stateMachine;
        foreach(var clip in clips)
        {
            var state=sm.AddState(clip.name);state.motion=clip;
            if(clip.name=="IdlePreview"){state.speed=.55f;sm.defaultState=state;}
            if(clip.name=="Fly")state.speed=.5f;
        }
        var mat=new Material(Shader.Find("HDRP/Lit")){name="TemporaryDragon_Slate"};
        mat.SetColor("_BaseColor",new Color(.11f,.19f,.21f));mat.SetFloat("_Smoothness",.24f);
        HDMaterial.ValidateMaterial(mat);AssetDatabase.CreateAsset(mat,Output+"/TemporaryDragon_Slate.mat");
        var original=new Material(Shader.Find("HDRP/Lit")){name="NSSM_OriginalRed"};
        original.SetTexture("_BaseColorMap",AssetDatabase.LoadAssetAtPath<Texture2D>(Source+"/chinese_dragon.png"));
        original.SetFloat("_Smoothness",.2f);HDMaterial.ValidateMaterial(original);
        AssetDatabase.CreateAsset(original,Source+"/NSSM_OriginalRed.mat");
        var holder=new GameObject("Temporary_NSSM_ReplaceWithTripo");
        Undo.RegisterCreatedObjectUndo(holder,"Install animated temporary dragon");
        holder.transform.SetParent(root.transform,false);
        var visual=(GameObject)PrefabUtility.InstantiatePrefab(model);visual.name="Visual";visual.transform.SetParent(holder.transform,false);
        var animator=visual.GetComponent<Animator>();if(animator==null)animator=visual.AddComponent<Animator>();
        animator.runtimeAnimatorController=controller;animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
        foreach(var renderer in visual.GetComponentsInChildren<SkinnedMeshRenderer>())
        {renderer.sharedMaterial=mat;renderer.updateWhenOffscreen=true;}
        clips.First(c=>c.name=="IdlePreview").SampleAnimation(visual,0);
        holder.transform.localScale=Vector3.one*.9f;
        holder.transform.localRotation=Quaternion.Euler(0,-65,27);
        var head=visual.GetComponentsInChildren<Transform>().First(t=>t.name=="Bone.016");
        holder.transform.position+=new Vector3(-1.5f,1.35f,.55f)-head.position;
        PrefabUtility.SaveAsPrefabAsset(holder,Output+"/TemporaryChineseDragon.prefab");
        var proxy=root.transform.Find("Proxy_ReplaceWithTripo");
        if(proxy!=null){Undo.RecordObject(proxy.gameObject,"Preserve blockout dragon");proxy.gameObject.SetActive(false);}
        AssetDatabase.SaveAssets();EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
        Selection.activeGameObject=holder;
        return "Installed "+holder.name+"; 102 bones; clips="+string.Join(",",clips.Select(c=>c.name+" "+c.length+"s"))+"; head="+head.position;
    }
}
