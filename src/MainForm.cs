using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SO4KoreanPatcher
{
    public sealed class FolderDropBox : Control
    {
        public string FolderPath = "";
        public string Prompt = "게임이 설치된 폴더를 여기에 드래그&드롭 해주세요.";
        public bool MultipleFiles;
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
            if (!empty && MultipleFiles)
            {
                var lines = FolderPath.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                int height = (Height - 16) / Math.Max(1, lines.Length);
                for (int i = 0; i < lines.Length; i++) TextRenderer.DrawText(e.Graphics, lines[i], Font,
                    new Rectangle(12, 8 + height * i, Width - 24, height), Enabled ? ForeColor : SystemColors.GrayText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
                return;
            }
            TextRenderer.DrawText(e.Graphics, empty ? Prompt : FolderPath,
                Font, new Rectangle(12, 8, Width - 24, Height - 16), Enabled ? ForeColor : SystemColors.GrayText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                (empty ? TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak : MultipleFiles ? TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak : TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine));
        }
    }
    public sealed class MainForm : Form
    {
        private readonly FolderDropBox drop = new FolderDropBox();
        private readonly RadioButton pc = new RadioButton();
        private readonly RadioButton xbox = new RadioButton();
        private readonly Label status = new Label();
        private string[] isoPaths;
        private readonly Button patch = new Button();
        private readonly Button restore = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private bool working;
        public MainForm()
        {
            Text = "스타오션4 한글 패치 " + BuildInfo.Version; StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("맑은 고딕", 10F);
            ClientSize = new Size(820, 206); MinimumSize = new Size(740, 245);
            MaximizeBox = false; BackColor = Color.White;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            drop.Dock = DockStyle.Fill; drop.Margin = new Padding(0, 0, 14, 12);
            var platforms = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 10, 8, 0) };
            pc.Text = "PC"; pc.Checked = true; pc.AutoSize = true; pc.Margin = new Padding(0, 0, 0, 10);
            xbox.Text = "xbox360(베타)"; xbox.AutoSize = true; xbox.Margin = new Padding(0);
            platforms.Controls.Add(pc); platforms.Controls.Add(xbox);
            patch.Text = "한글 패치"; patch.Enabled = false; patch.Size = new Size(112, 36); patch.Anchor = AnchorStyles.Left; patch.Margin = new Padding(0, 0, 0, 6);
            restore.Text = "원본 복구"; restore.Enabled = false; restore.Size = new Size(112, 36); restore.Margin = new Padding(0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            actions.Controls.Add(patch); actions.Controls.Add(restore);
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 8, 0, 0); progress.Minimum = 0; progress.Maximum = 10000; progress.Style = ProgressBarStyle.Continuous;
            progress.AccessibleName = "패치 진행률";
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true;
            layout.Controls.Add(platforms, 0, 0); layout.Controls.Add(drop, 1, 0); layout.Controls.Add(actions, 2, 0);
            layout.Controls.Add(status, 0, 1); layout.SetColumnSpan(status, 3);
            layout.Controls.Add(progress, 0, 2); layout.SetColumnSpan(progress, 3); Controls.Add(layout);
            pc.CheckedChanged += delegate { if (pc.Checked) ChangePlatform(); };
            xbox.CheckedChanged += delegate { if (xbox.Checked) ChangePlatform(); };
            ChangePlatform();
            drop.DragEnter += delegate(object sender, DragEventArgs e) { e.Effect = !working && (xbox.Checked ? GetIsos(e.Data) != null : GetFolder(e.Data) != null) ? DragDropEffects.Copy : DragDropEffects.None; };
            drop.DragDrop += delegate(object sender, DragEventArgs e) {
                if (working) return;
                if (xbox.Checked)
                {
                    var paths = GetIsos(e.Data); if (paths == null) return;
                    isoPaths = paths; drop.FolderPath = string.Join(Environment.NewLine, paths.Select(Path.GetFileName));
                    drop.Invalidate(); patch.Enabled = true; status.Text = "ISO 3개가 선택되었습니다. 패치를 시작하면 디스크 번호를 자동 확인합니다."; return;
                }
                string path = GetFolder(e.Data); if (path == null || working) return;
                if (!PatchEngine.IsGameFolder(path)) { MessageBox.Show(this, "게임 설치 폴더를 확인해 주세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                drop.FolderPath = Path.GetFullPath(path); drop.Invalidate(); patch.Enabled = true;
                RefreshRestore(); status.Text = restore.Enabled ? "패치 기록과 원본 백업이 있습니다. 한글 패치 또는 원본 복구를 선택해 주세요." : "게임 폴더가 선택되었습니다. 한글 패치를 적용할 수 있습니다.";
            };
            patch.Click += async delegate { await Install(); };
            restore.Click += async delegate { await RestoreSelected(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
        }
        private void ChangePlatform()
        {
            restore.Visible = pc.Checked; restore.Enabled = false;
            isoPaths = null; drop.FolderPath = ""; drop.MultipleFiles = xbox.Checked; patch.Enabled = false; progress.Value = 0;
            drop.Prompt = xbox.Checked ? "일본판 원본 ISO 3개(Disc 1·2·3)를\n한꺼번에 여기에 드래그&드롭 해주세요." : "게임이 설치된 폴더를 여기에 드래그&드롭 해주세요.";
            drop.AccessibleName = xbox.Checked ? "Xbox 360 원본 ISO 3개" : "PC 게임 설치 폴더";
            status.Text = xbox.Checked ? "원본 ISO 옆에 원본이름_repacked.iso를 생성합니다. 디스크별 최신 1개만 유지합니다." : "PC 게임 설치 폴더를 선택해 주세요.";
            drop.Invalidate();
        }
        private static string[] GetIsos(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length != 3 || paths.Any(p => !File.Exists(p) || !string.Equals(Path.GetExtension(p), ".iso", StringComparison.OrdinalIgnoreCase))) return null;
            paths = paths.Select(Path.GetFullPath).ToArray();
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3 ? paths : null;
        }
        private static string GetFolder(IDataObject data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            return paths != null && paths.Length == 1 && Directory.Exists(paths[0]) ? paths[0] : null;
        }
        private async Task Install()
        {
            bool isXbox = xbox.Checked;
            working = true; drop.Enabled = pc.Enabled = xbox.Enabled = patch.Enabled = restore.Enabled = false; progress.Value = 0;
            string folder = drop.FolderPath;
            var report = new Progress<int>(value => { if (value > progress.Value) progress.Value = value; });
            var messages = new Progress<string>(message => status.Text = message);
            try
            {
                string[] result = null;
                if (isXbox)
                    result = await Task.Run(() => new XboxPatchEngine(v => ((IProgress<int>)report).Report(v), m => ((IProgress<string>)messages).Report(m)).Run(isoPaths, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Xbox360")));
                else
                    await Task.Run(() => new PatchEngine(v => ((IProgress<int>)report).Report(v)).Run(folder, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SO4KoreanPatch.data"), BuildInfo.DataHash, false, null));
                progress.Value = 10000;
                status.Text = isXbox ? "원본 위치에 _repacked.iso 3개를 생성했습니다." : "패치가 완료되었습니다.";
                MessageBox.Show(this, isXbox ? "패치된 ISO 3개를 생성했습니다.\n\n" + string.Join("\n", result) + "\n\n기존 _repacked.iso는 최신 결과로 교체하고 임시 파일은 정리했습니다." : "패치가 완료되었습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                Program.Log(e); MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                status.Text = "패치를 완료하지 못했습니다. " + e.Message;
            }
            finally { working = false; drop.Enabled = pc.Enabled = xbox.Enabled = true; patch.Enabled = isXbox ? isoPaths != null : !string.IsNullOrEmpty(drop.FolderPath); RefreshRestore(); }
        }
        private void RefreshRestore()
        {
            restore.Enabled = !working && pc.Checked && PatchEngine.CanRestore(drop.FolderPath);
        }
        private async Task RestoreSelected()
        {
            if (working || !pc.Checked) return;
            string folder = drop.FolderPath;
            working = true; drop.Enabled = pc.Enabled = xbox.Enabled = patch.Enabled = restore.Enabled = false; progress.Value = 0;
            status.Text = "패치 기록과 백업을 검사하고 원본을 복구하고 있습니다.";
            var report = new Progress<int>(value => { if (value > progress.Value) progress.Value = value; });
            try
            {
                await Task.Run(() => new PatchEngine(v => ((IProgress<int>)report).Report(v)).RestoreOriginal(folder));
                progress.Value = 10000; status.Text = "원본 복구가 완료되었습니다. 한글 패치를 다시 적용할 수 있습니다.";
                MessageBox.Show(this, status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception e)
            {
                Program.Log(e); status.Text = "원본 복구를 완료하지 못했습니다. " + e.Message;
                MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { working = false; drop.Enabled = pc.Enabled = xbox.Enabled = true; patch.Enabled = PatchEngine.IsGameFolder(folder); RefreshRestore(); }
        }
        public void RenderPreview(string path)
        {
            ShowInTaskbar = false; Opacity = 0; Show(); PerformLayout(); Application.DoEvents();
            using (var image = new Bitmap(Width, Height)) { DrawToBitmap(image, new Rectangle(0, 0, Width, Height)); image.Save(path); }
            Close();
        }
        public void SelectXboxPreview() { xbox.Checked = true; }
    }
}
