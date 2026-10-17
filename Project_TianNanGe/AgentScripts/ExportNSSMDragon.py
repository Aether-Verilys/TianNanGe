# Run with Blender --background --factory-startup --disable-autoexec --python this_file.
# Re-exports the archived NSSM fallback; does not alter the active scene.
import bpy, os
from pathlib import Path
project = Path(__file__).resolve().parents[1]
source = project.parent / 'SourceAssets' / 'NSSMChineseDragon'

bpy.ops.wm.open_mainfile(filepath=str(source / 'chinese_dragon.blend'),load_ui=False,use_scripts=False)
scene=bpy.context.scene
arm=next(o for o in scene.objects if o.type=='ARMATURE')
mesh=next(o for o in scene.objects if o.type=='MESH')
image=bpy.data.images.load(str(source / 'chinese_dragon.png'))
mat=bpy.data.materials.new('ChineseDragon_OriginalTexture');mat.use_nodes=True
tex=mat.node_tree.nodes.new('ShaderNodeTexImage');tex.image=image
mat.node_tree.links.new(tex.outputs['Color'],mat.node_tree.nodes.get('Principled BSDF').inputs['Base Color'])
mesh.data.materials.clear();mesh.data.materials.append(mat)
scene.frame_start=1;scene.frame_end=270;scene.frame_set(1)
if bpy.context.object and bpy.context.object.mode!='OBJECT':bpy.ops.object.mode_set(mode='OBJECT')
bpy.ops.object.select_all(action='DESELECT');arm.select_set(True);mesh.select_set(True);bpy.context.view_layer.objects.active=arm
out=str(project / 'Assets/ThirdParty/NSSMChineseDragon')
os.makedirs(out,exist_ok=True)
bpy.ops.export_scene.fbx(filepath=out+'/ChineseDragon.fbx',use_selection=True,object_types={'MESH','ARMATURE'},add_leaf_bones=False,use_armature_deform_only=True,bake_anim=True,bake_anim_use_all_actions=False,bake_anim_use_nla_strips=False,bake_anim_simplify_factor=0.0,axis_forward='-Z',axis_up='Y',path_mode='STRIP',use_mesh_modifiers=True)
print('EXPORTED '+out+'/ChineseDragon.fbx')
