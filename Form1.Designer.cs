#nullable disable
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace chessGUI
{
    public partial class Form1 : Form
    {
        private IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new Container();
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Text = "Form1";
        }
    }
}


