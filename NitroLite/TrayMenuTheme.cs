using System.Drawing;
using System.Windows.Forms;

namespace NitroLite;

/// <summary>
/// Paints the tray context menu in the palette the window is using.
/// </summary>
/// <remarks>
/// The menu is a WinForms <see cref="ContextMenuStrip"/>, and WinForms has no notion of the WPF theme
/// dictionaries: left alone it draws the Windows default, so a light menu drops out of a dark window. The
/// renderer is rebuilt on every theme change rather than reading the palette lazily, because a
/// <see cref="ToolStripRenderer"/> is asked for its colours during paint and has no window to ask.
/// </remarks>
internal static class TrayMenuTheme
{
    /// <summary>The palette the menu is drawn with, taken from the live theme dictionary.</summary>
    internal readonly record struct Palette(
        Color Ground,
        Color Hover,
        Color Ink,
        Color InkFaint,
        Color Rule,
        Color Frame,
        Color Accent);

    internal static void Apply(ContextMenuStrip? menu, Palette palette)
    {
        if (menu == null) return;

        menu.Renderer = new Renderer(new Table(palette));
        menu.BackColor = palette.Ground;
        menu.ForeColor = palette.Ink;
        // Without this the strip keeps the 1px system border on the left and right of the drop-down.
        menu.DropShadowEnabled = true;
    }

    private sealed class Table : ProfessionalColorTable
    {
        internal Table(Palette palette) => Palette = palette;

        internal Palette Palette { get; }

        public override Color ToolStripDropDownBackground => Palette.Ground;
        public override Color ImageMarginGradientBegin => Palette.Ground;
        public override Color ImageMarginGradientMiddle => Palette.Ground;
        public override Color ImageMarginGradientEnd => Palette.Ground;
        public override Color MenuBorder => Palette.Frame;
        public override Color MenuItemBorder => Palette.Frame;
        public override Color MenuItemSelected => Palette.Hover;
        public override Color MenuItemSelectedGradientBegin => Palette.Hover;
        public override Color MenuItemSelectedGradientEnd => Palette.Hover;
        public override Color MenuItemPressedGradientBegin => Palette.Hover;
        public override Color MenuItemPressedGradientEnd => Palette.Hover;
        public override Color SeparatorDark => Palette.Rule;
        public override Color SeparatorLight => Palette.Rule;
        public override Color CheckBackground => Palette.Ground;
        public override Color CheckSelectedBackground => Palette.Hover;
        public override Color CheckPressedBackground => Palette.Hover;
    }

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _palette;

        internal Renderer(Table table) : base(table) => _palette = table.Palette;

        /// <summary>
        /// The base renderer draws item text in <see cref="SystemColors.ControlText"/>, which is black on every
        /// palette. A disabled item keeps a dimmer ink so "Turbo is blocked on battery" still reads as blocked.
        /// </summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _palette.Ink : _palette.InkFaint;
            base.OnRenderItemText(e);
        }

        /// <summary>The tick beside the active mode, in the accent rather than the system highlight blue.</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var box = e.ImageRectangle;
            using var pen = new Pen(_palette.Accent, 1.6f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new Point(box.Left + 2, box.Top + box.Height / 2),
                new Point(box.Left + box.Width / 2 - 1, box.Bottom - 3),
                new Point(box.Right - 2, box.Top + 3)
            });
        }
    }
}
