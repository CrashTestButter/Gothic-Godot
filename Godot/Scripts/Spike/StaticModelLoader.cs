using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using ZenKit;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Loads Gothic models (.ASC and .MDS visuals: mobs like beds, chairs, chests, doors, torch holders) in their rest
    /// pose as static mesh parts: the attachments of the mesh (.MDM) placed at their hierarchy node (.MDH). The root
    /// node keeps its rotation but sits at the object's origin, like Gothic-Unity's static models.
    /// </summary>
    public static class StaticModelLoader
    {
        public static List<MeshPart> TryLoad(Vfs vfs, string visual, TextureArrayBuilder textures)
        {
            var baseName = Path.GetFileNameWithoutExtension(visual).ToUpperInvariant();
            IModelHierarchy hierarchy = null;
            IModelMesh mesh = null;

            if (visual.EndsWith(".ASC", StringComparison.OrdinalIgnoreCase) && vfs.Find($"{baseName}.MDL") != null)
            {
                var model = new ZenKit.Model(vfs, $"{baseName}.MDL").Cache();
                hierarchy = model.Hierarchy;
                mesh = model.Mesh;
            }
            else
            {
                var meshName = baseName;
                if (visual.EndsWith(".MDS", StringComparison.OrdinalIgnoreCase) && vfs.Find($"{baseName}.MDS") != null)
                {
                    var script = new ModelScript(vfs, $"{baseName}.MDS").Cache();
                    if (!string.IsNullOrEmpty(script.SkeletonName))
                        meshName = Path.GetFileNameWithoutExtension(script.SkeletonName).ToUpperInvariant();
                }

                if (vfs.Find($"{baseName}.MDH") != null)
                    hierarchy = new ModelHierarchy(vfs, $"{baseName}.MDH").Cache();
                else if (vfs.Find($"{meshName}.MDH") != null)
                    hierarchy = new ModelHierarchy(vfs, $"{meshName}.MDH").Cache();

                if (vfs.Find($"{meshName}.MDM") != null)
                    mesh = new ModelMesh(vfs, $"{meshName}.MDM").Cache();
                else if (vfs.Find($"{baseName}.MDM") != null)
                    mesh = new ModelMesh(vfs, $"{baseName}.MDM").Cache();
            }

            if (hierarchy == null || mesh == null)
                return null;

            var nodes = hierarchy.Nodes;
            var world = new Transform3D[nodes.Count];
            var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < nodes.Count; i++)
            {
                var local = MeshPart.ToGodot(nodes[i].Transform);
                if (nodes[i].ParentIndex < 0)
                    local.Origin = Vector3.Zero;
                world[i] = nodes[i].ParentIndex < 0 ? local : world[nodes[i].ParentIndex] * local;
                indexByName[nodes[i].Name] = i;
            }

            var parts = new List<MeshPart>();
            foreach (var (nodeName, attachment) in mesh.Attachments)
            {
                if (indexByName.TryGetValue(nodeName, out var index))
                    MeshPart.AddFromMrm(attachment, textures, world[index], parts);
            }

            return parts.Count > 0 ? parts : null;
        }
    }
}
