// Generals Borderless patcher: puts the borderless dinput8.dll (embedded in this exe) into the game
// folders of C&C Generals / Zero Hour, or takes it out again. After that the game runs borderless
// fullscreen however it is started: ShockWave launcher, plain shortcut, any mod.
//
// Without arguments it opens a small window. For scripts:
//   GeneralsBorderless.exe -install | -remove | -status  [-path <game folder>]...
// Without -path it acts on every game folder it finds. Log: %LOCALAPPDATA%\GeneralsBorderless\patcher.log
//
// Build with ..\build.cmd (it uses the C# compiler that ships with Windows).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

static class Patcher
{
    public const string AppTitle = "Generals Borderless";
    const string Marker = "GeneralsBorderless dinput8 proxy"; // text inside our DLL, any version
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeneralsBorderless");
    static readonly string FoldersFile = Path.Combine(DataDir, "folders.txt");
    static readonly string[] RegistryKeys = { @"SOFTWARE\Electronic Arts\EA Games\Command and Conquer Generals Zero Hour", @"SOFTWARE\Electronic Arts\EA Games\Generals" };
    const string IniText =
        "; Generals Borderless settings. Delete this file to get the defaults back.\r\n" +
        "[Borderless]\r\n" +
        "; 0 = off: the game starts exactly as it did before the patch\r\n" +
        "Enabled=1\r\n" +
        "; 1 = set the game resolution to the monitor's at every start, so the game covers the screen exactly\r\n" +
        "ForceNativeResolution=1\r\n" +
        "; 1 = keep the mouse inside the game while it has focus (edge scrolling, second monitors)\r\n" +
        "LockCursor=1\r\n";

    [STAThread]
    static int Main(string[] args)
    {
        string action = null;
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "-install" || a == "-remove" || a == "-status") action = a.Substring(1);
            else if (a == "-path" && i + 1 < args.Length) paths.Add(args[++i]);
        }
        if (action == null)
        {
            Application.EnableVisualStyles();
            Application.Run(new PatcherForm());
            return 0;
        }
        int failures = 0;
        foreach (string dir in paths.Count > 0 ? paths.SelectMany(GameFoldersIn).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : FindFolders())
        {
            try { Log(action + " " + dir + ": " + (action == "install" ? Install(dir) : action == "remove" ? Remove(dir) : Describe(dir))); }
            catch (Exception e) { failures++; Log(action + " " + dir + ": failed, " + Explain(e)); }
        }
        return failures;
    }

    public static List<string> FindFolders()
    {
        var found = new List<string>();
        foreach (string key in RegistryKeys)
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                AddFolder(found, RegString(hive, key, "InstallPath"));
        foreach (string dir in SteamGameFolders(SteamDir())) AddFolder(found, dir);
        if (File.Exists(FoldersFile))
            foreach (string line in File.ReadAllLines(FoldersFile)) AddFolder(found, line);
        AddFolder(found, AppDomain.CurrentDomain.BaseDirectory);
        return found;
    }

    static string SteamDir()
    {
        string dir = RegString(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath") ?? RegString(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        return dir == null ? null : dir.Replace('/', '\\');
    }

    // Steam doesn't register the game the way its original installer does, so look in every Steam
    // library for folders with "Generals" in the name; AddFolder keeps only real game folders.
    public static List<string> SteamGameFolders(string steamDir)
    {
        var dirs = new List<string>();
        if (string.IsNullOrEmpty(steamDir)) return dirs;
        var libraries = new List<string> { steamDir };
        try
        {
            // Libraries are listed as "path"  "D:\\SteamLibrary" (older files: "1"  "D:\\SteamLibrary"); the
            // only values in the file that are full folder paths are those libraries.
            string vdf = Path.Combine(steamDir, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"([^\"]*)\""))
                {
                    string value = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Regex.IsMatch(value, @"^([A-Za-z]:\\|\\\\)")) libraries.Add(value);
                }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        foreach (string library in libraries)
        {
            try { dirs.AddRange(Directory.GetDirectories(Path.Combine(library, @"steamapps\common"), "*Generals*")); }
            catch (IOException) { } // library on a drive that isn't connected
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        return dirs;
    }

    public static bool AddFolder(List<string> list, string dir)
    {
        if (!IsGameFolder(dir)) return false;
        dir = Normalize(dir);
        if (list.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))) return false;
        list.Add(dir);
        return true;
    }

    // The folder itself when it is a game folder, otherwise the game folders directly inside it, so
    // picking a folder that holds both Generals and Zero Hour adds both.
    public static List<string> GameFoldersIn(string dir)
    {
        var found = new List<string>();
        if (AddFolder(found, dir)) return found;
        try
        {
            foreach (string sub in Directory.GetDirectories(dir)) AddFolder(found, sub);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return found;
    }

    // The game's exe plus its INI archive (INI.big for Generals, INIZH.big for Zero Hour), so other games
    // that happen to have a game.dat don't count.
    public static bool IsGameFolder(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir)
                && (File.Exists(Path.Combine(dir, "game.dat")) || File.Exists(Path.Combine(dir, "generals.exe")))
                && (File.Exists(Path.Combine(dir, "INIZH.big")) || File.Exists(Path.Combine(dir, "INI.big")));
        }
        catch (ArgumentException) { return false; }
    }

    static string Normalize(string dir) { return Path.GetFullPath(dir.Trim()).TrimEnd('\\'); }

    public static string GameName(string dir)
    {
        bool zeroHour = File.Exists(Path.Combine(dir, "INIZH.big")) || File.Exists(Path.Combine(dir, "WindowZH.big"));
        return zeroHour ? "Zero Hour" : "Generals";
    }

    public static string Describe(string dir)
    {
        string dll = Path.Combine(dir, "dinput8.dll");
        if (!File.Exists(dll)) return "Not installed";
        byte[] current = File.ReadAllBytes(dll);
        if (!IsOurs(current)) return "Not installed (its own dinput8.dll is kept)";
        return current.SequenceEqual(EmbeddedDll()) ? "Installed" : "Installed, older version";
    }

    // Writes the DLL next to a temporary name first, so a running game (file in use) leaves everything as it was.
    public static string Install(string dir)
    {
        string dll = Path.Combine(dir, "dinput8.dll"), original = Path.Combine(dir, "dinput8_original.dll"), fresh = dll + ".new";
        bool foreign = File.Exists(dll) && !IsOurs(File.ReadAllBytes(dll));
        if (foreign && File.Exists(original)) throw new InvalidOperationException("this folder already has both dinput8.dll and dinput8_original.dll from something else; nothing was changed");
        CheckNotInUse(dll);
        File.WriteAllBytes(fresh, EmbeddedDll());
        try
        {
            if (foreign) File.Move(dll, original);  // ours loads it in place of Windows' own dinput8.dll
            else if (File.Exists(dll)) File.Delete(dll);
            File.Move(fresh, dll);
        }
        catch
        {
            if (File.Exists(fresh)) File.Delete(fresh);
            if (foreign && !File.Exists(dll) && File.Exists(original)) File.Move(original, dll);
            throw;
        }
        string ini = Path.Combine(dir, "GeneralsBorderless.ini");
        if (!File.Exists(ini)) File.WriteAllText(ini, IniText);
        Remember(dir, true);
        return foreign ? "installed (the dinput8.dll that was there is now dinput8_original.dll and still used)" : "installed";
    }

    public static string Remove(string dir)
    {
        string dll = Path.Combine(dir, "dinput8.dll"), original = Path.Combine(dir, "dinput8_original.dll"), ini = Path.Combine(dir, "GeneralsBorderless.ini");
        bool removed = false;
        CheckNotInUse(dll);
        if (File.Exists(dll) && IsOurs(File.ReadAllBytes(dll))) { File.Delete(dll); removed = true; }
        if (removed && File.Exists(original)) File.Move(original, dll);
        if (File.Exists(ini)) { File.Delete(ini); removed = true; }
        Remember(dir, false);
        return removed ? "removed" : "was not installed";
    }

    // A running game has the DLL mapped, and deleting a mapped file fails as "access denied", which
    // would wrongly suggest missing admin rights; opening it exclusively tells the two apart.
    static void CheckNotInUse(string dll)
    {
        if (!File.Exists(dll)) return;
        try { using (File.Open(dll, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
        catch (IOException e) { throw new InUseException(e); }
    }

    class InUseException : IOException
    {
        public InUseException(Exception inner) : base("in use", inner) { }
    }

    public static string Explain(Exception e)
    {
        int code = e.HResult & 0xFFFF;
        if (e is InUseException || (e is IOException && (code == 32 || code == 33))) return "the game is running (dinput8.dll is in use). Close it and try again.";
        if (e is UnauthorizedAccessException) return "access denied. Run this as administrator.";
        return e.Message;
    }

    static bool IsOurs(byte[] file)
    {
        byte[] m = Encoding.ASCII.GetBytes(Marker);
        for (int i = 0; i + m.Length <= file.Length; i++)
        {
            int k = 0;
            while (k < m.Length && file[i + k] == m[k]) k++;
            if (k == m.Length) return true;
        }
        return false;
    }

    static byte[] embedded;
    static byte[] EmbeddedDll()
    {
        if (embedded == null)
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("dinput8.dll"))
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                embedded = m.ToArray();
            }
        return embedded;
    }

    // Folders added by hand are remembered, so Remove still finds them later.
    static void Remember(string dir, bool installed)
    {
        Directory.CreateDirectory(DataDir);
        var dirs = File.Exists(FoldersFile) ? File.ReadAllLines(FoldersFile).Where(l => l.Trim().Length > 0).ToList() : new List<string>();
        dirs.RemoveAll(d => string.Equals(d.Trim().TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase));
        if (installed) dirs.Add(dir);
        File.WriteAllLines(FoldersFile, dirs.ToArray());
    }

    static string RegString(RegistryHive hive, string path, string name)
    {
        try
        {
            using (RegistryKey k = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32).OpenSubKey(path))
                return k == null ? null : k.GetValue(name) as string;
        }
        catch (Exception) { return null; }
    }

    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.AppendAllText(Path.Combine(DataDir, "patcher.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
        }
        catch (IOException) { }
    }
}

class PatcherForm : Form
{
    const int GameColumn = 0, StatusColumn = 1, FolderColumn = 2;
    readonly ListView list = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    readonly Label result = new Label { AutoSize = true, UseMnemonic = false, Padding = new Padding(0, 6, 0, 6) };

    public PatcherForm()
    {
        SuspendLayout(); // auto scaling to the screen's DPI happens in ResumeLayout
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Patcher.AppTitle;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(860, 360);
        MinimumSize = new Size(600, 320);
        StartPosition = FormStartPosition.CenterScreen;

        var intro = new Label
        {
            AutoSize = true,
            UseMnemonic = false,
            Padding = new Padding(0, 0, 0, 8),
            Text = "Makes C&C Generals and Zero Hour run borderless fullscreen, whatever starts them: a launcher, " +
                   "a shortcut or a mod. Install once, then play the way you always do.\n" +
                   "Tick the game folders and click Install. Remove puts everything back."
        };
        list.Columns.Add("Game");
        list.Columns.Add("Status");
        list.Columns.Add("Folder");

        var install = new Button { Text = "Install", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        var add = new Button { Text = "Add folder...", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        install.Click += (s, e) => Apply(true);
        remove.Click += (s, e) => Apply(false);
        add.Click += (s, e) => AddFolder();
        close.Click += (s, e) => Close();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { close, remove, install, add });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // else the column grows with the folder paths
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(result, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = install;
        CancelButton = close;
        // Labels only wrap when they have a maximum width.
        layout.Layout += (s, e) =>
        {
            var max = new Size(layout.ClientSize.Width - layout.Padding.Horizontal - 6, 0);
            intro.MaximumSize = max;
            result.MaximumSize = max;
        };

        foreach (string dir in Patcher.FindFolders()) AddRow(dir);
        if (list.Items.Count == 0) result.Text = "No game folder found. Click Add folder... and pick the folder with game.dat or generals.exe.";
        ResumeLayout(false);
        PerformLayout();
        FitColumns();
    }

    void AddRow(string dir)
    {
        var item = new ListViewItem(new[] { Patcher.GameName(dir), Patcher.Describe(dir), dir }) { Checked = true, Tag = dir };
        list.Items.Add(item);
    }

    void FitColumns()
    {
        list.AutoResizeColumns(list.Items.Count > 0 ? ColumnHeaderAutoResizeStyle.ColumnContent : ColumnHeaderAutoResizeStyle.HeaderSize);
        foreach (ColumnHeader c in list.Columns) c.Width = Math.Max(c.Width, TextRenderer.MeasureText(c.Text, list.Font).Width + 24);
        list.Columns[GameColumn].Width += 24; // room for the check box
    }

    void Apply(bool install)
    {
        var lines = new List<string>();
        foreach (ListViewItem item in list.CheckedItems)
        {
            string dir = (string)item.Tag;
            string outcome;
            try { outcome = install ? Patcher.Install(dir) : Patcher.Remove(dir); }
            catch (Exception e) { outcome = "failed: " + Patcher.Explain(e); }
            Patcher.Log((install ? "install " : "remove ") + dir + ": " + outcome);
            lines.Add(item.Text + ": " + outcome);
            item.SubItems[StatusColumn].Text = Patcher.Describe(dir);
        }
        if (lines.Count == 0) lines.Add("Tick at least one game folder first.");
        else if (install) lines.Add("Start the game the way you always do.");
        result.Text = string.Join("\n", lines);
        FitColumns();
    }

    void AddFolder()
    {
        using (var dlg = new FolderBrowserDialog { Description = "Select your game folder (the one with game.dat or generals.exe), or the folder that holds both Generals and Zero Hour." })
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            List<string> games = Patcher.GameFoldersIn(dlg.SelectedPath);
            if (games.Count == 0) { result.Text = "No game found in that folder. Pick the folder with game.dat or generals.exe."; return; }
            var known = list.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
            foreach (string game in games)
                if (Patcher.AddFolder(known, game)) AddRow(game);
            FitColumns();
        }
    }
}
