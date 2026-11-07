using Godot;

namespace GothicGodot.Interaction
{
    /// <summary>Text for the panels in the world (dialog, inventory): readable on top of everything, outlined.</summary>
    public static class PanelLabels
    {
        public static Label3D Create(int fontSize, Color color, float widthMetres)
        {
            return new Label3D
            {
                FontSize = fontSize,
                PixelSize = 0.001f,
                Modulate = color,
                OutlineSize = 10,
                OutlineModulate = new Color(0, 0, 0, 0.85f),
                NoDepthTest = true,
                RenderPriority = 10,
                OutlineRenderPriority = 9,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Width = widthMetres * 1000f
            };
        }
    }
}
