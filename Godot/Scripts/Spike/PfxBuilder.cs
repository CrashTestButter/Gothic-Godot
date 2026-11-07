using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Godot;
using ZenKit;
using ZenKit.Daedalus;
using Image = Godot.Image;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Gothic particle effects (torches, fires, smoke) from their C_ParticleFX instances in PARTICLEFX.DAT, mapped
    /// onto GPUParticles3D in Gothic's own units: centimetres to metres, milliseconds to seconds, velocity cm/ms to
    /// m/s (x10), gravity cm/ms² to m/s² (x10000). Animated textures (_A0, _A1, ...) become a flipbook atlas.
    /// </summary>
    public class PfxBuilder
    {
        private static readonly Regex _animatedName = new(@"^(.*_A)(\d+)$", RegexOptions.IgnoreCase);

        private readonly Vfs _vfs;
        private readonly DaedalusVm _vm;
        private readonly Dictionary<string, GpuTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);

        public List<GpuParticles3D> Emitters { get; } = new();
        public int EmitterCount => Emitters.Count;
        public int MissingCount { get; private set; }

        private class GpuTemplate
        {
            public ParticleProcessMaterial Process;
            public QuadMesh Quad;
            public int Amount;
            public float Lifetime;
        }

        public PfxBuilder(Vfs vfs)
        {
            _vfs = vfs;
            _vm = new DaedalusVm(vfs, "PARTICLEFX.DAT",
                (byte)(DaedalusExecutionFlags.AllowNullInstanceAccess | DaedalusExecutionFlags.IgnoreConstSpecifier));
        }

        public void Add(string visual, Transform3D transform, Node3D parent)
        {
            var template = GetTemplate(visual);
            if (template == null)
                return;

            var particles = new GpuParticles3D
            {
                Amount = template.Amount,
                Lifetime = template.Lifetime,
                ProcessMaterial = template.Process,
                DrawPass1 = template.Quad,
                Preprocess = template.Lifetime,
                VisibilityAabb = new Aabb(new Vector3(-2, -1, -2), new Vector3(4, 4, 4)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            parent.AddChild(particles);
            particles.GlobalTransform = transform;
            Emitters.Add(particles);
        }

        private GpuTemplate GetTemplate(string visual)
        {
            var name = Path.GetFileNameWithoutExtension(visual);
            if (_templates.TryGetValue(name, out var cached))
                return cached;

            GpuTemplate template = null;
            try
            {
                var symbol = _vm.GetSymbolByName(name);
                // InitInstance on anything but an instance symbol aborts the native side.
                if (symbol != null && symbol.Type == DaedalusDataType.Instance)
                    template = CreateTemplate(_vm.InitInstance<ParticleEffectInstance>(symbol));
            }
            catch (Exception)
            {
                template = null;
            }

            if (template == null)
                MissingCount++;
            _templates[name] = template;
            return template;
        }

        private GpuTemplate CreateTemplate(ParticleEffectInstance pfx)
        {
            var lifetime = Mathf.Max(0.05f, pfx.LspPartAvg / 1000f);
            var lifetimeVar = pfx.LspPartVar / 1000f;
            var pps = Mathf.Max(1f, pfx.PpsValue);

            var process = new ParticleProcessMaterial
            {
                LifetimeRandomness = Mathf.Clamp(lifetimeVar / (lifetime + lifetimeVar), 0f, 1f),
                InitialVelocityMin = Mathf.Max(0f, (pfx.VelAvg - pfx.VelVar) * 10f),
                InitialVelocityMax = Mathf.Max(0f, (pfx.VelAvg + pfx.VelVar) * 10f),
                Gravity = ToGodot(ParseFloats(pfx.FlyGravityS)) * 10000f,
                ParticleFlagDisableZ = false
            };

            ApplyShape(pfx, process);
            ApplyDirection(pfx, process);
            ApplyColor(pfx, process);

            var size = ParseFloats(pfx.VisSizeStartS);
            var width = size.Length > 0 ? size[0] / 100f : 0.1f;
            var height = size.Length > 1 ? size[1] / 100f : width;
            if (pfx.VisSizeEndScale > 0f && Mathf.Abs(pfx.VisSizeEndScale - 1f) > 0.001f)
            {
                var curve = new Curve();
                curve.MaxValue = Mathf.Max(1f, pfx.VisSizeEndScale);
                curve.AddPoint(new Vector2(0, 1));
                curve.AddPoint(new Vector2(1, pfx.VisSizeEndScale));
                process.ScaleCurve = new CurveTexture { Curve = curve };
            }

            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BillboardKeepScale = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = pfx.VisAlphaFuncS?.ToUpperInvariant() switch
                {
                    "ADD" => BaseMaterial3D.BlendModeEnum.Add,
                    "MUL" => BaseMaterial3D.BlendModeEnum.Mul,
                    _ => BaseMaterial3D.BlendModeEnum.Mix
                },
                DisableFog = false
            };

            var frames = LoadFrames(pfx.VisNameS);
            if (frames.Count > 0)
            {
                material.AlbedoTexture = BuildAtlas(frames);
                if (frames.Count > 1)
                {
                    material.ParticlesAnimHFrames = frames.Count;
                    material.ParticlesAnimVFrames = 1;
                    material.ParticlesAnimLoop = pfx.VisTexAniIsLooping != 0;
                    var cycles = Mathf.Max(0.1f, pfx.VisTexAniFps * lifetime / frames.Count);
                    process.AnimSpeedMin = cycles;
                    process.AnimSpeedMax = cycles;
                }
            }

            return new GpuTemplate
            {
                Process = process,
                Quad = new QuadMesh { Size = new Vector2(width, height), Material = material },
                Amount = Mathf.Clamp(Mathf.CeilToInt(pps * (lifetime + lifetimeVar)), 1, 512),
                Lifetime = lifetime
            };
        }

        private static void ApplyShape(ParticleEffectInstance pfx, ParticleProcessMaterial process)
        {
            var dim = ParseFloats(pfx.ShpDimS);
            switch (pfx.ShpTypeS?.ToUpperInvariant())
            {
                case "SPHERE":
                    process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere;
                    process.EmissionSphereRadius = dim.Length > 0 ? dim[0] / 100f : 0.1f;
                    break;
                case "CIRCLE":
                    process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring;
                    process.EmissionRingRadius = dim.Length > 0 ? dim[0] / 100f : 0.1f;
                    process.EmissionRingInnerRadius = 0;
                    process.EmissionRingHeight = 0.01f;
                    process.EmissionRingAxis = Vector3.Up;
                    break;
                case "BOX":
                case "LINE":
                    process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
                    process.EmissionBoxExtents = dim.Length >= 3
                        ? new Vector3(dim[0], dim[1], dim[2]) / 200f
                        : new Vector3(dim.Length > 0 ? dim[0] / 200f : 0.05f, 0.01f, 0.01f);
                    break;
                default:
                    process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point;
                    break;
            }

            var offset = ParseFloats(pfx.ShpOffsetVecS);
            if (offset.Length == 3)
                process.EmissionShapeOffset = ToGodot(offset) / 100f;
        }

        private static void ApplyDirection(ParticleEffectInstance pfx, ParticleProcessMaterial process)
        {
            switch (pfx.DirModeS?.ToUpperInvariant())
            {
                case "DIR":
                    var head = Mathf.DegToRad(pfx.DirAngleHead);
                    var elev = Mathf.DegToRad(pfx.DirAngleElev);
                    process.Direction = new Vector3(Mathf.Cos(elev) * Mathf.Cos(head), Mathf.Sin(elev),
                        -Mathf.Cos(elev) * Mathf.Sin(head));
                    process.Spread = Mathf.Max(pfx.DirAngleHeadVar, pfx.DirAngleElevVar);
                    break;
                case "RAND":
                    process.Spread = 180f;
                    break;
                default:
                    process.Spread = 0f;
                    process.InitialVelocityMin = 0f;
                    process.InitialVelocityMax = 0f;
                    break;
            }
        }

        private static void ApplyColor(ParticleEffectInstance pfx, ParticleProcessMaterial process)
        {
            var start = ParseFloats(pfx.VisTexColorStartS);
            var end = ParseFloats(pfx.VisTexColorEndS);
            var startColor = start.Length >= 3 ? new Color(start[0] / 255f, start[1] / 255f, start[2] / 255f) : Colors.White;
            var endColor = end.Length >= 3 ? new Color(end[0] / 255f, end[1] / 255f, end[2] / 255f) : startColor;
            startColor.A = pfx.VisAlphaStart / 255f;
            endColor.A = pfx.VisAlphaEnd / 255f;

            var gradient = new Gradient();
            gradient.SetColor(0, startColor);
            gradient.SetColor(1, endColor);
            process.ColorRamp = new GradientTexture1D { Gradient = gradient };
        }

        private List<Image> LoadFrames(string textureName)
        {
            var frames = new List<Image>();
            if (string.IsNullOrEmpty(textureName) || textureName == "=")
                return frames;

            var baseName = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
            var match = _animatedName.Match(baseName);
            if (!match.Success)
            {
                var single = LoadImage(baseName);
                if (single != null)
                    frames.Add(single);
                return frames;
            }

            for (var i = int.Parse(match.Groups[2].Value); i < 64; i++)
            {
                var frame = LoadImage($"{match.Groups[1].Value}{i}");
                if (frame == null)
                    break;
                frames.Add(frame);
            }

            return frames;
        }

        private Image LoadImage(string baseName)
        {
            if (_vfs.Find($"{baseName}-c.tex") == null)
                return null;

            var texture = new ZenKit.Texture(_vfs, $"{baseName}-c.tex");
            return Image.CreateFromData(texture.GetWidth(0), texture.GetHeight(0), false, Image.Format.Rgba8,
                texture.GetMipmapRgba(0));
        }

        private static ImageTexture BuildAtlas(List<Image> frames)
        {
            var width = frames[0].GetWidth();
            var height = frames[0].GetHeight();
            if (frames.Count == 1)
            {
                frames[0].GenerateMipmaps();
                return ImageTexture.CreateFromImage(frames[0]);
            }

            var atlas = Image.CreateEmpty(width * frames.Count, height, false, Image.Format.Rgba8);
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames[i].GetWidth() != width || frames[i].GetHeight() != height)
                    frames[i].Resize(width, height);
                atlas.BlitRect(frames[i], new Rect2I(0, 0, width, height), new Vector2I(i * width, 0));
            }

            atlas.GenerateMipmaps();
            return ImageTexture.CreateFromImage(atlas);
        }

        private static Vector3 ToGodot(float[] values) =>
            values.Length == 3 ? new Vector3(values[0], values[1], -values[2]) : Vector3.Zero;

        private static float[] ParseFloats(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim() == "=")
                return Array.Empty<float>();

            var tokens = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var values = new List<float>();
            foreach (var token in tokens)
            {
                if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
            }

            return values.ToArray();
        }
    }
}
