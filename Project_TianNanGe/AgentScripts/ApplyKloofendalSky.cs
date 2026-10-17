using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class ApplyKloofendalSky
{
    const string ImagePath="Assets/TianNanGe/Lighting/HDRI/kloofendal_38d_partly_cloudy_puresky_4k.exr";
    const string ProfilePath="Assets/TianNanGe/Lighting/KloofendalSkyVolume.asset";

    public static string Apply()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play Mode first.");
        var scene=EditorSceneManager.GetActiveScene();
        if(scene.path!="Assets/TianNanGe/Scenes/TianNanGe_Main.unity")throw new Exception("Open TianNanGe_Main first.");
        var volume=GameObject.Find("Global Look").GetComponent<Volume>();
        if(volume==null||volume.sharedProfile==null)throw new Exception("Missing scene look profile.");
        AssetDatabase.ImportAsset(ImagePath,ImportAssetOptions.ForceSynchronousImport);
        var importer=(TextureImporter)AssetImporter.GetAtPath(ImagePath);
        importer.textureType=TextureImporterType.Default;
        importer.textureShape=TextureImporterShape.TextureCube;
        importer.generateCubemap=TextureImporterGenerateCubemap.AutoCubemap;
        importer.sRGBTexture=false;
        importer.mipmapEnabled=true;
        importer.wrapMode=TextureWrapMode.Clamp;
        importer.textureCompression=TextureImporterCompression.Uncompressed;
        importer.maxTextureSize=2048;
        importer.isReadable=true;
        importer.SaveAndReimport();
        var cube=AssetDatabase.LoadAssetAtPath<Cubemap>(ImagePath);
        if(cube==null)throw new Exception("HDRI did not import as a Cubemap.");
        if(!File.Exists(ProfilePath))
        {
            var oldPath=AssetDatabase.GetAssetPath(volume.sharedProfile);
            if(!AssetDatabase.CopyAsset(oldPath,ProfilePath))throw new Exception("Profile copy failed.");
        }
        var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if(!profile.TryGet<HDRISky>(out var sky))
        {sky=profile.Add<HDRISky>(true);AssetDatabase.AddObjectToAsset(sky,profile);}
        sky.active=true;sky.hdriSky.Override(cube);
        sky.skyIntensityMode.Override(SkyIntensityMode.Lux);
        sky.desiredLuxValue.Override(18000f);
        sky.updateMode.Override(EnvironmentUpdateMode.OnChanged);
        sky.rotation.Override(0);
        sky.enableBackplate.Override(false);
        if(profile.TryGet<GradientSky>(out var gradient)) {gradient.active=false;EditorUtility.SetDirty(gradient);}
        if(!profile.TryGet<VisualEnvironment>(out var environment))throw new Exception("Missing Visual Environment");
        environment.skyType.Override((int)SkyType.HDRI);
        environment.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
        EditorUtility.SetDirty(environment);
        // Integrate irradiance on the CPU: the Inspector's GPU readback returned zero on Metal.
        // Solid-angle weighting implements the same integral of L * cos(theta) over the hemisphere.
        // Mip 2 retains the sun's energy while keeping this one-time editor calculation inexpensive.
        double red=0,green=0,blue=0;
        float maximum=-1; Vector3 sunDirection=Vector3.up;
        int mip=Math.Min(2,cube.mipmapCount-1),n=Math.Max(1,cube.width>>mip);
        for(int f=0;f<6;f++)
        {
            var pixels=cube.GetPixels((CubemapFace)f,mip);
            for(int y=0;y<n;y++)for(int x=0;x<n;x++)
            {
                float u=2*(x+.5f)/n-1,v=2*(y+.5f)/n-1;
                Vector3 dir;
                switch(f)
                {
                    case 0:dir=new Vector3(1,v,-u);break;
                    case 1:dir=new Vector3(-1,v,u);break;
                    case 2:dir=new Vector3(u,1,-v);break;
                    case 3:dir=new Vector3(u,-1,v);break;
                    case 4:dir=new Vector3(u,v,1);break;
                    default:dir=new Vector3(-u,v,-1);break;
                }
                dir.Normalize();if(dir.y<=0)continue;
                var c=pixels[y*n+x];float l=.2126f*c.r+.7152f*c.g+.0722f*c.b;
                double solidAngle=4.0/(n*(double)n*Math.Pow(1+u*u+v*v,1.5));
                double weight=dir.y*solidAngle;
                red+=c.r*weight;green+=c.g*weight;blue+=c.b*weight;
                if(dir.y>.1f&&l>maximum){maximum=l;sunDirection=dir;}
            }
        }
        float integratedLux=(float)(.2126*red+.7152*green+.0722*blue);
        if(integratedLux<=0||float.IsNaN(integratedLux)||float.IsInfinity(integratedLux))
            throw new Exception("Invalid integrated sky intensity.");
        sky.upperHemisphereLuxValue.Override(integratedLux);
        float maxChannel=(float)Math.Max(red,Math.Max(green,blue));
        sky.upperHemisphereLuxColor.Override(new Vector3((float)red,(float)green,(float)blue)*(.5f/maxChannel));
        // Rotate the detected sun behind the left side of the subject and align the direct light.
        float sourceYaw=Mathf.Atan2(sunDirection.x,sunDirection.z)*Mathf.Rad2Deg;
        sky.rotation.Override(Mathf.Repeat(sourceYaw-(-20f),360f));
        var worldSun=Quaternion.Euler(0,-sky.rotation.value,0)*sunDirection;
        var sun=GameObject.Find("Sun").GetComponent<Light>();
        Undo.RecordObject(sun.transform,"Align HDRI sun");
        sun.transform.rotation=Quaternion.LookRotation(-worldSun,Vector3.up);
        sky.lockSun.Override(false);
        if(profile.TryGet<Fog>(out var fog))
        {fog.meanFreePath.Override(95f);fog.maximumHeight.Override(8f);EditorUtility.SetDirty(fog);}
        Undo.RecordObject(volume,"Apply Kloofendal HDRI");volume.sharedProfile=profile;
        EditorUtility.SetDirty(sky);EditorUtility.SetDirty(profile);EditorUtility.SetDirty(volume);
        AssetDatabase.SaveAssets();
        string result="Cubemap="+cube.width+"px "+cube.format+"; integratedLux="+sky.upperHemisphereLuxValue.value+"; targetLux="+sky.desiredLuxValue.value+"; rotation="+sky.rotation.value+"; sourceSun="+sunDirection+"; sunEuler="+sun.transform.eulerAngles;
        importer.isReadable=false;importer.SaveAndReimport();
        (RenderPipelineManager.currentPipeline as HDRenderPipeline)?.RequestSkyEnvironmentUpdate();
        SyncLightingEnvironment();
        Selection.activeObject=profile;SceneView.RepaintAll();
        return result;
    }

    // Can be run separately to synchronize Lighting > Environment without resetting the artistic look.
    public static string SyncLightingEnvironment()
    {
        if(EditorApplication.isPlaying)throw new Exception("Exit Play Mode first.");
        var scene=EditorSceneManager.GetActiveScene();
        if(scene.path!="Assets/TianNanGe/Scenes/TianNanGe_Main.unity")throw new Exception("Open TianNanGe_Main first.");
        var profile=AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
        if(profile==null||!profile.TryGet<HDRISky>(out var sky)||!sky.active||sky.hdriSky.value==null)
            throw new Exception("Missing configured HDRI profile.");
        StaticLightingSky environment=null;
        foreach(var candidate in Resources.FindObjectsOfTypeAll<StaticLightingSky>())
        {
            if(candidate.gameObject.scene!=scene)continue;
            if(environment!=null)throw new Exception("Multiple static lighting skies in the active scene.");
            environment=candidate;
        }
        if(environment==null)
        {
            var go=new GameObject("StaticLightingSky",typeof(StaticLightingSky));
            go.hideFlags=HideFlags.HideInHierarchy;
            Undo.RegisterCreatedObjectUndo(go,"Create HDRP lighting environment");
            environment=go.GetComponent<StaticLightingSky>();
        }
        Undo.RecordObject(environment,"Sync HDRP lighting environment");
        environment.profile=profile;
        environment.staticLightingSkyUniqueID=SkySettings.GetUniqueID(typeof(HDRISky));
        EditorUtility.SetDirty(environment);
        // Match the Lighting window refresh; HDRP 17.3 exposes this editor hook as internal.
        var pipeline=RenderPipelineManager.currentPipeline as HDRenderPipeline;
        if(pipeline!=null)
            typeof(HDRenderPipeline).GetMethod("RequestStaticSkyUpdate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.Invoke(pipeline,null);
        EditorSceneManager.MarkSceneDirty(scene);
        if(!EditorSceneManager.SaveScene(scene))throw new Exception("Scene save failed.");
        return "Lighting Environment: "+AssetDatabase.GetAssetPath(environment.profile)+"; Static Lighting Sky=HDRI Sky; saved="+!scene.isDirty;
    }
}
