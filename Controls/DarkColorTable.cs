using System.Drawing;
using System.Windows.Forms;

namespace Koxmix.Controls
{
    public class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(32, 32, 32);
        public override Color ImageMarginGradientBegin => Color.FromArgb(32, 32, 32);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(32, 32, 32);
        public override Color ImageMarginGradientEnd => Color.FromArgb(32, 32, 32);
        public override Color MenuBorder => Color.FromArgb(55, 55, 55);
        public override Color MenuItemBorder => Color.FromArgb(32, 32, 32);
        public override Color MenuItemSelected => Color.FromArgb(70, 70, 70);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(70, 70, 70);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(70, 70, 70);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(40, 40, 40);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(40, 40, 40);
    }
}