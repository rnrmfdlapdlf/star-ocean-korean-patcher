using System;
using System.IO;
using System.Windows.Forms;

namespace SO4KoreanPatcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length == 2 && args[0] == "--render-preview")
                { using (var form = new MainForm()) form.RenderPreview(Path.GetFullPath(args[1])); return 0; }
                if (args.Length == 2 && args[0] == "--render-xbox-preview")
                { using (var form = new MainForm()) { form.SelectXboxPreview(); form.RenderPreview(Path.GetFullPath(args[1])); } return 0; }
                if (args.Length >= 3 && (args[0] == "--verify" || args[0] == "--install-test"))
                {
                    string workspace = Path.GetFullPath(args[2]); Directory.CreateDirectory(workspace);
                    int mask = 0;
                    if (args.Length != 3 && (args.Length != 5 || args[3] != "--cheats" || !int.TryParse(args[4], out mask))) throw new ArgumentException("치트 확인 인수가 올바르지 않습니다.");
                    SteamCheats.ValidateOptions((SteamCheatOptions)mask);
                    var values = new System.Collections.Generic.List<int>(); int last = -1;
                    new PatchEngine(v => { values.Add(v); if (v / 100 != last) { last = v / 100; File.WriteAllText(Path.Combine(workspace, "progress.txt"), last.ToString()); } }).Run(args[1], Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SO4KoreanPatch.data"), BuildInfo.DataHash, args[0] == "--verify", workspace, (SteamCheatOptions)mask);
                    Storage.AtomicJson(Path.Combine(workspace, "verification.json"), new { passed = true, buildId = BuildInfo.Id, kind = args[0], cheatMask = mask, progress = values }); return 0;
                }
                if (args.Length != 0) throw new ArgumentException("지원하지 않는 인수입니다.");
                Application.Run(new MainForm()); return 0;
            }
            catch (Exception e)
            {
                Log(e);
                if (args.Length >= 3) { try { Directory.CreateDirectory(args[2]); File.WriteAllText(Path.Combine(args[2], "error.txt"), e.ToString()); } catch { } }
                else MessageBox.Show(e.Message, "SO4KoreanPatcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        internal static void Log(Exception error)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SO4KoreanPatcher", "logs");
                Directory.CreateDirectory(path); File.AppendAllText(Path.Combine(path, DateTime.Now.ToString("yyyyMMdd") + ".log"), DateTime.Now.ToString("o") + " " + error + Environment.NewLine);
            }
            catch { }
        }
    }
}
