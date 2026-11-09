# Blender headless: convert BVH files to FBX (armature + baked animation, metres).
# usage: blender -b --factory-startup -P bvh2fbx.py -- <out_dir> <bvh> [<bvh> ...]
import bpy, sys, os
argv = sys.argv[sys.argv.index("--") + 1:]
out_dir, files = argv[0], argv[1:]
os.makedirs(out_dir, exist_ok=True)
for path in files:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_anim.bvh(filepath=path, global_scale=0.01, frame_start=0, use_fps_scale=False,
                            update_scene_fps=True, update_scene_duration=True, rotate_mode='NATIVE',
                            axis_forward='-Z', axis_up='Y')
    arm = next(o for o in bpy.context.scene.objects if o.type == 'ARMATURE')
    arm.name = "LaFAN"
    arm.data.name = "LaFAN"
    if arm.animation_data and arm.animation_data.action:
        arm.animation_data.action.name = os.path.splitext(os.path.basename(path))[0]
    out = os.path.join(out_dir, os.path.splitext(os.path.basename(path))[0] + ".fbx")
    bpy.ops.export_scene.fbx(filepath=out, object_types={'ARMATURE'}, add_leaf_bones=False,
                             bake_anim=True, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
                             bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0,
                             apply_unit_scale=True, apply_scale_options='FBX_SCALE_UNITS',
                             armature_nodetype='NULL', primary_bone_axis='Y', secondary_bone_axis='X',
                             axis_forward='-Z', axis_up='Y')
    print("CONVERTED", out, os.path.getsize(out))
