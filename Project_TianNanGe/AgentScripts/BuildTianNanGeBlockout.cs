using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

// Run with Unity Pipeline run_script. Deliberately outside Assets: no runtime dependency.
public static class BuildTianNanGeBlockout
{
    const string Root = "Assets/TianNanGe";
    const string ScenePath = Root + "/Scenes/TianNanGe_Main.unity";
    static Material snow, timber, stone, dragon, horn, teal, skirt, skin, hair, roof, gold;
    static int meshId;

    public static string Build()
    {
        if (File.Exists(ScenePath))
            throw new InvalidOperationException("Main scene already exists. Edit it directly; this builder will not overwrite hand edits.");
        for (int i=0; i<SceneManager.sceneCount; i++)
        {
            var old=SceneManager.GetSceneAt(i);
            if (old.isDirty && string.IsNullOrEmpty(old.path))
                throw new InvalidOperationException("An untitled scene has unsaved work. Save it before building.");
        }
        if (!EditorSceneManager.SaveOpenScenes()) throw new InvalidOperationException("Could not preserve open scenes.");
        foreach (string sub in new[]{"Scenes","Materials","Meshes","Lighting"})
            Directory.CreateDirectory(Root + "/" + sub);
        AssetDatabase.Refresh();
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var root=Group("TianNanGe_Blockout",null);
        snow=Mat("Snow",new Color(.78f,.86f,.87f),.2f);
        timber=Mat("BlueTimber",new Color(.12f,.21f,.27f),.22f);
        stone=Mat("Stone",new Color(.20f,.29f,.34f),.1f);
        dragon=Mat("DragonSlate",new Color(.075f,.13f,.17f),.32f);
        horn=Mat("Horn",new Color(.49f,.55f,.49f),.25f);
        teal=Mat("JadeCloth",new Color(.025f,.32f,.24f),.3f);
        skirt=Mat("DarkSkirt",new Color(.025f,.055f,.067f),.24f);
        skin=Mat("SkinProxy",new Color(.63f,.49f,.35f),.25f);
        hair=Mat("HairProxy",new Color(.035f,.044f,.046f),.2f);
        roof=Mat("Roof",new Color(.18f,.29f,.36f),.2f);
        roof.SetFloat("_DoubleSidedEnable",1);HDMaterial.ValidateMaterial(roof);EditorUtility.SetDirty(roof);
        gold=Mat("WarmEye",new Color(.8f,.52f,.12f),.4f);
        gold.SetColor("_EmissiveColor",new Color(1f,.55f,.1f)*2500f);
        EditorUtility.SetDirty(gold);
        Bridge(Group("Bridge",root));
        Dragon(Group("Dragon",root));
        Character(Group("Character",root));
        var architecture=Group("Architecture",root);
        Pavilion(architecture,"MainPavilion",new Vector3(4.1f,-.35f,10.5f),1.05f,3);
        Pavilion(architecture,"LeftPavilion",new Vector3(-5.5f,-1.2f,11.5f),.9f,3);
        Pavilion(architecture,"FarPavilion",new Vector3(-.5f,-.2f,23),1.1f,3);
        Pavilion(architecture,"FarRight",new Vector3(10,-1,21),1.1f,2);
        Box("SnowCourtyard",architecture,new Vector3(0,-1.2f,15),new Vector3(28,.3f,23),snow);
        Lighting(Group("Lighting",root));
        var cameras=Group("Cameras",root);
        var camGo=new GameObject("Main Camera",typeof(Camera),typeof(HDAdditionalCameraData));
        camGo.transform.SetParent(cameras,false);
        camGo.tag="MainCamera";
        camGo.transform.position=new Vector3(0,.88f,-4.6f);
        camGo.transform.LookAt(new Vector3(0,1.24f,0));
        var cam=camGo.GetComponent<Camera>();
        cam.fieldOfView=32; cam.nearClipPlane=.05f; cam.farClipPlane=150;
        cam.aspect=1280f/620f;
        var hd=camGo.GetComponent<HDAdditionalCameraData>();
        hd.antialiasing=HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
        hd.volumeLayerMask=1;
        hd.clearColorMode=HDAdditionalCameraData.ClearColorMode.Sky;
        var view=SceneView.lastActiveSceneView;
        if(view!=null) { view.AlignViewToObject(camGo.transform); view.sceneLighting=true; }
        Selection.activeGameObject=root.gameObject;
        AssetDatabase.SaveAssets();
        if (!EditorSceneManager.SaveScene(scene,ScenePath)) throw new Exception("Scene save failed");
        return "Saved " + ScenePath + "; " + root.GetComponentsInChildren<Renderer>().Length + " blockout renderers; Tripo replacement roots ready.";
    }

    static Transform Group(string name,Transform parent)
    {
        var t=new GameObject(name).transform; t.SetParent(parent,false); return t;
    }
    static Material Mat(string name,Color color,float smooth)
    {
        var m=new Material(Shader.Find("HDRP/Lit")){name=name};
        m.SetColor("_BaseColor",color); m.SetFloat("_Smoothness",smooth);
        AssetDatabase.CreateAsset(m,Root+"/Materials/"+name+".mat"); return m;
    }
    static GameObject Shape(string name,Transform p,PrimitiveType kind,Vector3 pos,Vector3 scale,Material mat)
    {
        var o=GameObject.CreatePrimitive(kind); o.name=name; o.transform.SetParent(p,false);
        o.transform.localPosition=pos; o.transform.localScale=scale;
        o.GetComponent<Renderer>().sharedMaterial=mat;
        UnityEngine.Object.DestroyImmediate(o.GetComponent<Collider>()); return o;
    }
    static GameObject Box(string n,Transform p,Vector3 pos,Vector3 size,Material m)
        =>Shape(n,p,PrimitiveType.Cube,pos,size,m);
    static GameObject Ball(string n,Transform p,Vector3 pos,Vector3 size,Material m)
        =>Shape(n,p,PrimitiveType.Sphere,pos,size,m);
    static void Beam(string n,Transform p,Vector3 a,Vector3 b,float width,float depth,Material m)
    {
        var o=Box(n,p,(a+b)*.5f,new Vector3(width,(b-a).magnitude,depth),m);
        o.transform.localRotation=Quaternion.FromToRotation(Vector3.up,b-a);
    }
    static void MeshObject(string name,Transform p,List<Vector3> vertices,List<int> tris,Material mat)
    {
        var mesh=new Mesh{name=name}; mesh.SetVertices(vertices); mesh.SetTriangles(tris,0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        AssetDatabase.CreateAsset(mesh,Root+"/Meshes/"+(meshId++).ToString("D3")+"_"+name+".asset");
        var go=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));go.transform.SetParent(p,false);
        go.GetComponent<MeshFilter>().sharedMesh=mesh;go.GetComponent<MeshRenderer>().sharedMaterial=mat;
    }
    static void Tube(string name,Transform p,Vector3[] points,float[] radii,Material mat,int sides=8)
    {
        var verts=new List<Vector3>(); var tris=new List<int>();
        for(int i=0;i<points.Length;i++)
        {
            var forward=(points[Math.Min(i+1,points.Length-1)]-points[Math.Max(0,i-1)]).normalized;
            var right=Vector3.Cross(forward,Mathf.Abs(forward.y)>.95f?Vector3.forward:Vector3.up).normalized;
            var up=Vector3.Cross(forward,right).normalized;
            for(int j=0;j<sides;j++) {float a=j*Mathf.PI*2/sides;verts.Add(points[i]+radii[i]*(right*Mathf.Cos(a)+up*Mathf.Sin(a)));}
        }
        for(int i=0;i<points.Length-1;i++)for(int j=0;j<sides;j++)
        {
            int a=i*sides+j,b=i*sides+(j+1)%sides,c=a+sides,d=b+sides;
            tris.AddRange(new[]{a,b,c,b,d,c});
        }
        int start=verts.Count;verts.Add(points[0]);int end=verts.Count;verts.Add(points[points.Length-1]);
        for(int j=0;j<sides;j++)
        {tris.AddRange(new[]{start,(j+1)%sides,j,end,(points.Length-1)*sides+j,(points.Length-1)*sides+(j+1)%sides});}
        MeshObject(name,p,verts,tris,mat);
    }
    static void Bridge(Transform root)
    {
        Box("BridgeDeck",root,new Vector3(.35f,-.17f,4),new Vector3(2.7f,.32f,17),stone);
        Box("SnowDeck",root,new Vector3(.35f,.015f,4),new Vector3(2.76f,.09f,17),snow);
        for(int side=0;side<2;side++)
        {
            float x=side==0?-1.08f:1.78f;
            for(int i=0;i<9;i++)
            {
                float z=-3.6f+i*1.8f;
                if (side!=0 || z>=0)
                {
                Box("Post",root,new Vector3(x,.73f,z),new Vector3(.14f,1.5f,.16f),timber);
                Ball("SnowCap",root,new Vector3(x,1.52f,z),new Vector3(.26f,.16f,.26f),snow);
                }
                if(i==8) continue;
                foreach(float y in new[]{.42f,1.42f})
                {
                    Beam("Rail",root,new Vector3(x,y,z),new Vector3(x,y,z+1.8f),.0936f,.1275f,timber);
                    Beam("RailSnow",root,new Vector3(x,y+.09f,z),new Vector3(x,y+.09f,z+1.8f),.1224f,.17f,snow);
                }
            }
        }
        for(int i=0;i<7;i++)Box("DeckJoint",root,new Vector3(.35f,.064f,i*1.8f),new Vector3(2.45f,.007f,.014f),stone);
    }
    static void Dragon(Transform root)
    {
        var p=Group("Proxy_ReplaceWithTripo",root);
        Tube("NeckCurve",p,new[]{new Vector3(-1.2f,1.4f,1.3f),new Vector3(-.65f,2.4f,1.8f),new Vector3(-1.15f,3.4f,2.3f),new Vector3(-2.7f,3.6f,3),new Vector3(-3.8f,2.8f,4)},new[]{.7f,.82f,.8f,.68f,.5f},dragon,10);
        var skull=Ball("HeadMass",p,new Vector3(-1.35f,1.32f,.55f),new Vector3(1.5f,1.45f,1.25f),dragon);
        skull.transform.localRotation=Quaternion.Euler(0,0,-25);
        var snout=Ball("LongMuzzle",p,new Vector3(-1.79f,.69f,-.04f),new Vector3(1.45f,.6f,.82f),dragon);
        snout.transform.localRotation=Quaternion.Euler(0,0,-18);
        Ball("LowerJaw",p,new Vector3(-1.84f,.47f,-.03f),new Vector3(1.35f,.25f,.68f),dragon);
        Ball("Nose",p,new Vector3(-2.24f,.66f,-.16f),new Vector3(.36f,.42f,.64f),dragon);
        Ball("EyeSocket",p,new Vector3(-1.28f,1.02f,-.075f),new Vector3(.39f,.23f,.14f),skirt);
        Ball("WarmEye",p,new Vector3(-1.30f,1.025f,-.16f),new Vector3(.18f,.085f,.06f),gold);
        Beam("Brow",p,new Vector3(-1.55f,1.12f,-.17f),new Vector3(-1.03f,1.19f,-.09f),.17f,.17f,dragon);
        Tube("CrownHorn",p,new[]{new Vector3(-1.25f,1.82f,.45f),new Vector3(-1.13f,2.25f,.39f),new Vector3(-.95f,2.7f,.46f),new Vector3(-.55f,3.1f,.65f)},new[]{.25f,.19f,.11f,.005f},horn);
        Tube("RearHorn",p,new[]{new Vector3(-1.72f,1.75f,.84f),new Vector3(-1.85f,2.27f,.85f),new Vector3(-1.68f,2.93f,1.1f)},new[]{.24f,.15f,.005f},horn);
        for(int i=0;i<6;i++)
        {
            float y=1.04f+i*.21f;float x=-.92f+i*.04f;
            Tube("SweptMane",p,new[]{new Vector3(x,y,.65f),new Vector3(x+.45f,y+.12f,.75f),new Vector3(x+.82f,y+.52f,.95f)},new[]{.22f,.17f,.008f},dragon);
        }
        Tube("Whisker",p,new[]{new Vector3(-2.35f,.69f,-.25f),new Vector3(-2.6f,.82f,-.35f),new Vector3(-2.8f,1.27f,-.2f)},new[]{.035f,.026f,.001f},horn,6);
    }
    static void Character(Transform root)
    {
        root.localPosition=new Vector3(.38f,.06f,.1f);
        var p=Group("Proxy_ReplaceWithTripo",root);
        Tube("SkirtSilhouette",p,new[]{new Vector3(.08f,.02f,0),new Vector3(.015f,.38f,0),new Vector3(0,.85f,0),new Vector3(0,1.15f,0)},new[]{.36f,.29f,.19f,.18f},skirt,10);
        var torso=Ball("JadeTorso",p,new Vector3(-.025f,1.38f,0),new Vector3(.43f,.65f,.27f),teal);
        torso.transform.localRotation=Quaternion.Euler(0,0,-6);
        Shape("Neck",p,PrimitiveType.Capsule,new Vector3(-.055f,1.72f,0),new Vector3(.12f,.12f,.12f),skin);
        Ball("Head",p,new Vector3(-.07f,1.91f,-.005f),new Vector3(.25f,.34f,.25f),skin);
        Ball("ShortHair",p,new Vector3(-.05f,2.00f,.025f),new Vector3(.31f,.25f,.30f),hair);
        Ball("HairBack",p,new Vector3(.04f,1.91f,.10f),new Vector3(.17f,.27f,.18f),hair);
        Ball("FaceDirection",p,new Vector3(-.18f,1.91f,-.06f),new Vector3(.11f,.11f,.14f),skin);
        Beam("LeftUpperSleeve",p,new Vector3(-.23f,1.61f,0),new Vector3(-.30f,1.25f,-.015f),.12f,.13f,teal);
        Beam("LeftForearm",p,new Vector3(-.30f,1.25f,-.015f),new Vector3(-.34f,.99f,-.09f),.09f,.1f,skin);
        Beam("RightSleeve",p,new Vector3(.2f,1.60f,0),new Vector3(.29f,1.20f,.015f),.14f,.15f,teal);
        Beam("RightForearm",p,new Vector3(.29f,1.20f,.015f),new Vector3(.31f,.98f,-.04f),.09f,.1f,skin);
        Beam("HeldProp",p,new Vector3(-.42f,1.20f,-.1f),new Vector3(-.21f,.42f,-.1f),.065f,.075f,timber);
        Tube("Sash",p,new[]{new Vector3(.16f,1.48f,.09f),new Vector3(.55f,1.35f,.17f),new Vector3(1.0f,1.40f,.4f),new Vector3(1.3f,1.54f,.65f)},new[]{.075f,.06f,.035f,.003f},teal,4);
        Ball("LeftFoot",p,new Vector3(-.12f,.04f,-.09f),new Vector3(.16f,.12f,.3f),skirt);
        Ball("RightFoot",p,new Vector3(.23f,.04f,-.06f),new Vector3(.16f,.12f,.3f),skirt);
    }
    static void Pavilion(Transform parent,string name,Vector3 pos,float scale,int floors)
    {
        var placement=Group(name,parent);placement.localPosition=pos;placement.localScale=Vector3.one*scale;
        var p=Group("Proxy_ReplaceWithTripo",placement);
        Box("StoneBase",p,new Vector3(0,.05f,0),new Vector3(5.4f,.55f,3.8f),stone);
        for(int level=0;level<floors;level++)
        {
            float width=5.5f-level*.55f,depth=3.5f-level*.32f,y=.4f+level*1.65f;
            Box("Floor",p,new Vector3(0,y,0),new Vector3(width,.16f,depth),timber);
            Box("Hall",p,new Vector3(0,y+.6f,.15f),new Vector3(width*.75f,1.15f,depth*.7f),stone);
            for(int i=0;i<5;i++)
            {
                float x=-width*.45f+i*width*.225f;
                Box("FrontColumn",p,new Vector3(x,y+.62f,-depth*.45f),new Vector3(.13f,1.24f,.13f),timber);
                Box("RearColumn",p,new Vector3(x,y+.62f,depth*.45f),new Vector3(.13f,1.24f,.13f),timber);
            }
            Beam("BalconyRail",p,new Vector3(-width*.5f,y+.45f,-depth*.52f),new Vector3(width*.5f,y+.45f,-depth*.52f),.08f,.09f,timber);
            RoofMesh(p,"Roof",width+1.3f,depth+1.2f,y+1.15f,roof,0);
            RoofMesh(p,"RoofSnow",width+1.3f,depth+1.2f,y+1.15f,snow,.065f);
        }
    }
    static void RoofMesh(Transform p,string name,float width,float depth,float y,Material mat,float lift)
    {
        // A sparse curved roof surface; raised tips make the pavilion readable at blockout scale.
        var vertices=new List<Vector3>();var tris=new List<int>();
        int nx=8,nz=8;
        for(int z=0;z<=nz;z++)for(int x=0;x<=nx;x++)
        {
            float u=(float)x/nx*2-1,v=(float)z/nz*2-1;
            float h=.67f*Mathf.Pow(1-Mathf.Abs(v),1.65f)+.34f*Mathf.Pow(Mathf.Abs(u),7)+.12f*Mathf.Pow(Mathf.Abs(v),9);
            vertices.Add(new Vector3(u*width*.5f,y+h+lift,v*depth*.5f));
        }
        for(int z=0;z<nz;z++)for(int x=0;x<nx;x++)
        {int a=z*(nx+1)+x,b=a+1,c=a+nx+1,d=c+1;tris.AddRange(new[]{a,c,b,b,c,d});}
        MeshObject(name,p,vertices,tris,mat);
    }
    static void Lighting(Transform root)
    {
        var profile=ScriptableObject.CreateInstance<VolumeProfile>();
        AssetDatabase.CreateAsset(profile,Root+"/Lighting/BlockoutVolume.asset");
        var env=profile.Add<VisualEnvironment>(true);env.skyType.value=(int)SkyType.Gradient;
        var sky=profile.Add<GradientSky>(true);
        sky.top.value=new Color(.25f,.42f,.59f);sky.middle.value=new Color(.63f,.77f,.83f);sky.bottom.value=new Color(.3f,.46f,.56f);
        sky.skyIntensityMode.value=SkyIntensityMode.Exposure;sky.exposure.value=13.3f;
        var exposure=profile.Add<Exposure>(true);exposure.mode.value=ExposureMode.Fixed;exposure.fixedExposure.value=12;
        var tone=profile.Add<Tonemapping>(true);tone.mode.value=TonemappingMode.ACES;
        var fog=profile.Add<Fog>(true);fog.enabled.value=true;fog.colorMode.value=FogColorMode.SkyColor;fog.tint.value=new Color(.85f,.94f,1f);
        fog.color.value=new Color(.56f,.71f,.80f);fog.meanFreePath.value=32;fog.baseHeight.value=-2;fog.maximumHeight.value=16;
        fog.enableVolumetricFog.value=false;
        foreach(var component in profile.components)AssetDatabase.AddObjectToAsset(component,profile);
        EditorUtility.SetDirty(profile);
        var volumeGo=new GameObject("Global Look",typeof(Volume));volumeGo.transform.SetParent(root,false);
        var volume=volumeGo.GetComponent<Volume>();volume.isGlobal=true;volume.priority=100;volume.sharedProfile=profile;
        Sun(root,"Sun",new Vector3(38,-135,0),new Color(1,.92f,.78f),50000,true);
        Sun(root,"Cold Fill",new Vector3(25,-20,0),new Color(.52f,.72f,1f),22000,false);
    }
    static void Sun(Transform root,string name,Vector3 rotation,Color color,float intensity,bool shadows)
    {
        var go=new GameObject(name);go.transform.SetParent(root,false);go.transform.rotation=Quaternion.Euler(rotation);
        var l=go.AddComponent<Light>();l.type=LightType.Directional;go.AddComponent<HDAdditionalLightData>();
        l.color=color;l.intensity=intensity;l.shadows=shadows?LightShadows.Soft:LightShadows.None;
    }
}
