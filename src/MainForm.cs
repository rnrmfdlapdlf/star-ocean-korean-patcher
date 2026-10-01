using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SO4KoreanPatcher
{
    public sealed class FolderDropBox : Control
    {
        public string FolderPath = "";
        public FolderDropBox()
        {
            DoubleBuffered = true; AllowDrop = true; TabStop = true;
            AccessibleName = "게임 폴더"; AccessibleRole = AccessibleRole.Grouping;
            BackColor = Color.FromArgb(246, 248, 252);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Enabled ? Color.FromArgb(125, 146, 176) : Color.LightGray, 1))
            { pen.DashStyle = DashStyle.Dash; e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); }
            bool empty = string.IsNullOrEmpty(FolderPath);
            TextRenderer.DrawText(e.Graphics, empty ? "게임이 설치된 폴더를 여기에 드래그&드롭 해주세요." : FolderPath,
                Font, new Rectangle(12, 8, Width - 24, Height - 16), Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                (empty ? TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak : TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine));
        }
    }
    public sealed class MainForm : Form
    {
        private readonly FolderDropBox drop = new FolderDropBox();
        private readonly RadioButton pc = new RadioButton();
        private readonly Button patch = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private bool working;
        public MainForm()
        {
            Text = "SO4KoreanPatcher"; StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("맑은 고딕", 10F);
            ClientSize = new Size(680, 148); MinimumSize = new Size(560, 186);
            MaximizeBox = false; BackColor = Color.White;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            drop.Dock = DockStyle.Fill; drop.Margin = new Padding(0, 0, 14, 12);
            pc.Text = "PC"; pc.Checked = true; pc.Anchor = AnchorStyles.Left; pc.AutoSize = true; pc.Margin = new Padding(0, 0, 0, 12);
            patch.Text = "한글 패치"; patch.Enabled = false; patch.Size = new Size(112, 40); patch.Anchor = AnchorStyles.Left; patch.Margin = new Padding(0, 0, 0, 12);
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 8, 0, 0); progress.Minimum = 0; progress.Maximum = 10000; progress.Style = ProgressBarStyle.Continuous;
            progress.AccessibleName = "패치 진행률";
            layout.Controls.Add(drop, 0, 0); layout.Controls.Add(pc, 1, 0); layout.Controls.Add(patch, 2, 0);
            layout.Controls.Add(progress, 0, 1); layout.SetColumnSpan(progress, 3); Controls.Add(layout);
            drop.DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = GetFolder(e.Data) != null && !working ? DragDropEffects.Copy : DragDropEffects.None; };
            drop.DragDrop += delegate(object sender, DragEventArgs e) {
                string path = GetFolder(e.Data); if (path == null || working) return;
                if (!PatchEngine.IsGameFolder(path)) { MessageBox.Show(this, "게임 설치 폴더를 확인해 주세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                drop.FolderPath = Path.GetFullPath(path); drop.Invalidate(); patch.Enabled = true;
            };
            patch.Click += async delegate { await Install(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
        }
        private static string GetFolder(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            return paths != null && paths.Length == 1 && Directory.Exists(paths[0]) ? paths[0] : null;
        }
        private async Task Install()
        {
            working = true; drop.Enabled = pc.Enabled = patch.Enabled = false; progress.Value = 0;
            string folder = drop.FolderPath;
            var report = new Progress<int>(value => { if (value > progress.Value) progress.Value = value; });
            try
            {
                await Task.Run(() => new PatchEngine(v => ((IProgress<int>)report).Report(v)).Run(folder, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SO4KoreanPatch.data"), BuildInfo.DataHash, false, null));
                progress.Value = 10000;
                MessageBox.Show(this, "패치가 완료되었습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                Program.Log(e); MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { working = false; drop.Enabled = pc.Enabled = patch.Enabled = true; }
        }
        public void RenderPreview(string path)
        {
            ShowInTaskbar = false; Opacity = 0; Show(); PerformLayout(); Application.DoEvents();
            using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(0, 0, Width, Height)); image.Save(path); }
            Close();
        }
    }
}
