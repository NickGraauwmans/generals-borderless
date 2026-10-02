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
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

// The release version, shown in the window; a release tag must match it (the workflow checks).
[assembly: AssemblyVersion("1.3.1")]
[assembly: AssemblyFileVersion("1.3.1")]
[assembly: AssemblyTitle("Generals Borderless")]
[assembly: AssemblyProduct("Generals Borderless")]

static class Patcher
{
    public const string AppTitle = "Generals Borderless";
    public const string ProjectUrl = "https://github.com/NickGraauwmans/generals-borderless";

    public static string ReleaseVersion
    {
        get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
    }

    // The logo built into this exe (build.cmd: /win32icon), for the windows' title bars.
    public static Icon AppIcon
    {
        get { try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { return null; } }
    }

    const string Marker = "GeneralsBorderless dinput8 proxy"; // text inside our DLL, any version
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeneralsBorderless");
    static readonly string FoldersFile = Path.Combine(DataDir, "folders.txt");
    static readonly string[] RegistryKeys = { @"SOFTWARE\Electronic Arts\EA Games\Command and Conquer Generals Zero Hour", @"SOFTWARE\Electronic Arts\EA Games\Generals" };
    public static readonly string[] SettingNames = { "Enabled", "ForceNativeResolution", "LockCursor", "ScaleToScreen" };

    static string IniText(Dictionary<string, bool> s)
    {
        Func<string, string> v = name => s[name] ? "1" : "0";
        return
            "; Generals Borderless settings. Change them with GeneralsBorderless.exe (Settings...), or delete this file to get the defaults back.\r\n" +
            "[Borderless]\r\n" +
            "; 0 = off: the game starts exactly as it did before the patch\r\n" +
            "Enabled=" + v("Enabled") + "\r\n" +
            "; 1 = set the game resolution to the monitor's at every start, so the game covers the screen exactly\r\n" +
            "ForceNativeResolution=" + v("ForceNativeResolution") + "\r\n" +
            "; 1 = keep the mouse inside the game while it has focus (edge scrolling, second monitors)\r\n" +
            "LockCursor=" + v("LockCursor") + "\r\n" +
            "; With ForceNativeResolution=0 and a lower resolution: 1 = scale the game up to fill the screen\r\n" +
            "; (black bars if its shape differs), 0 = show it at its own size in the middle (box mode)\r\n" +
            "ScaleToScreen=" + v("ScaleToScreen") + "\r\n";
    }

    public static Dictionary<string, bool> DefaultSettings()
    {
        return SettingNames.ToDictionary(n => n, n => true, StringComparer.OrdinalIgnoreCase);
    }

    // The settings of a game folder; anything missing keeps its default (on), as in the DLL.
    public static Dictionary<string, bool> ReadSettings(string dir)
    {
        Dictionary<string, bool> s = DefaultSettings();
        string ini = Path.Combine(dir, "GeneralsBorderless.ini");
        if (File.Exists(ini))
            foreach (string line in File.ReadAllLines(ini))
            {
                Match m = Regex.Match(line, @"^\s*(\w+)\s*=\s*(-?\d+)");
                if (m.Success && s.ContainsKey(m.Groups[1].Value)) s[m.Groups[1].Value] = m.Groups[2].Value != "0";
            }
        return s;
    }

    // Game folders are usually admin-only (Program Files), so the settings are changed through this
    // patcher, which runs as administrator, rather than with Notepad.
    public static void WriteSettings(string dir, Dictionary<string, bool> s)
    {
        File.WriteAllText(Path.Combine(dir, "GeneralsBorderless.ini"), IniText(s));
    }

    public static bool IsInstalled(string dir)
    {
        string dll = Path.Combine(dir, "dinput8.dll");
        return File.Exists(dll) && IsOurs(File.ReadAllBytes(dll));
    }

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
        foreach (string dir in InstalledAppFolders()) AddFolder(found, dir);
        foreach (string dir in SteamGameFolders(SteamDir())) AddFolder(found, dir);
        if (File.Exists(FoldersFile))
            foreach (string line in File.ReadAllLines(FoldersFile)) AddFolder(found, line);
        AddFolder(found, AppDomain.CurrentDomain.BaseDirectory);
        return found;
    }

    // Installers also list the game in Windows' installed apps with its folder. That still points to the
    // right place when another install (Steam on its first start) has taken over the InstallPath above.
    // The folder can hold both games (repacks), so look one level down too.
    static List<string> InstalledAppFolders()
    {
        var dirs = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey apps = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (apps == null) continue;
                        foreach (string name in apps.GetSubKeyNames())
                            try
                            {
                                using (RegistryKey app = apps.OpenSubKey(name))
                                {
                                    string title = app == null ? null : app.GetValue("DisplayName") as string;
                                    string location = app == null ? null : app.GetValue("InstallLocation") as string;
                                    if (title != null && Regex.IsMatch(title, @"Generals|Zero Hour|Command (&|and) Conquer|C&C", RegexOptions.IgnoreCase))
                                        dirs.AddRange(GameFoldersIn(location));
                                }
                            }
                            catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }
        return dirs;
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
        Version installed = MarkerVersion(current), mine = MarkerVersion(EmbeddedDll());
        if (installed == null || mine == null || installed == mine) return "Installed";
        return installed < mine ? "Installed, older version" : "Installed, newer version";
    }

    // The version after the marker text, e.g. 1.2 in "GeneralsBorderless dinput8 proxy 1.2".
    static Version MarkerVersion(byte[] dll)
    {
        int at = IndexOf(dll, Encoding.ASCII.GetBytes(Marker + " "));
        if (at < 0) return null;
        var text = new StringBuilder();
        for (int i = at + Marker.Length + 1; i < dll.Length && (char.IsDigit((char)dll[i]) || dll[i] == '.'); i++) text.Append((char)dll[i]);
        Version v;
        return Version.TryParse(text.ToString(), out v) ? v : null;
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
        if (!File.Exists(ini)) File.WriteAllText(ini, IniText(DefaultSettings()));
        Remember(dir);
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

    static bool IsOurs(byte[] file) { return IndexOf(file, Encoding.ASCII.GetBytes(Marker)) >= 0; }

    static int IndexOf(byte[] data, byte[] find)
    {
        for (int i = 0; i + find.Length <= data.Length; i++)
        {
            int k = 0;
            while (k < find.Length && data[i + k] == find[k]) k++;
            if (k == find.Length) return i;
        }
        return -1;
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

    // Folders added by hand or installed into are remembered, also after Remove, so they stay in the
    // list when nothing else finds them (e.g. the registry points to another install by now).
    // A folder that is no longer a game folder drops out of the list by itself (AddFolder).
    public static void Remember(string dir)
    {
        Directory.CreateDirectory(DataDir);
        var dirs = File.Exists(FoldersFile) ? File.ReadAllLines(FoldersFile).Where(l => l.Trim().Length > 0).ToList() : new List<string>();
        if (dirs.Any(d => string.Equals(d.Trim().TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))) return;
        dirs.Add(dir);
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
        Icon = Patcher.AppIcon;
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
                   "a shortcut or a mod. You only need to install it once.\n\n" +
                   "The game folders found are already selected. Click Install, or Remove to put everything back."
        };
        list.Columns.Add("Game");
        list.Columns.Add("Status");
        list.Columns.Add("Folder");

        var install = new Button { Text = "Install", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        var add = new Button { Text = "Add folder...", AutoSize = true };
        var settings = new Button { Text = "Settings...", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        install.Click += (s, e) => Apply(true);
        remove.Click += (s, e) => Apply(false);
        add.Click += (s, e) => AddFolder();
        settings.Click += (s, e) => EditSettings();
        close.Click += (s, e) => Close();
        // Buttons grouped by what they work on: the list (under it), the selected folders' main
        // actions (bottom right), and Close a little apart.
        close.Margin = new Padding(18, 3, 3, 3);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.AddRange(new Control[] { install, remove, close });
        add.Anchor = AnchorStyles.Left;
        settings.Anchor = AnchorStyles.Right;
        var listTools = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        listTools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listTools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listTools.Controls.Add(add, 0, 0);
        listTools.Controls.Add(settings, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // else the column grows with the folder paths
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(intro, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(listTools, 0, 2);
        layout.Controls.Add(result, 0, 3);
        // bottom row: version and a link to the project on the left, buttons on the right
        string versionText = "v" + Patcher.ReleaseVersion + "  \u00b7  ", linkText = "github.com/NickGraauwmans/generals-borderless";
        var link = new LinkLabel { Text = versionText + linkText, LinkArea = new LinkArea(versionText.Length, linkText.Length), AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 12, 0) };
        link.LinkClicked += (s, e) => OpenInBrowser(Patcher.ProjectUrl);
        var bottom = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // the link, at the left
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // the buttons, at the right
        bottom.Controls.Add(link, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);
        layout.Controls.Add(bottom, 0, 4);
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
        var item = new ListViewItem(new[] { Patcher.GameName(dir), "", dir }) { Checked = true, Tag = dir, UseItemStyleForSubItems = false };
        list.Items.Add(item);
        ShowStatus(item);
    }

    // Installed in green, an older version in amber (it wants updating), not installed in grey.
    static void ShowStatus(ListViewItem item)
    {
        string status = Patcher.Describe((string)item.Tag);
        ListViewItem.ListViewSubItem cell = item.SubItems[StatusColumn];
        cell.Text = status;
        cell.ForeColor = status.StartsWith("Installed, older") ? Color.FromArgb(179, 89, 0)
                       : status.StartsWith("Installed") ? Color.FromArgb(26, 127, 55)
                       : SystemColors.GrayText;
    }

    static string Folders(int n) { return n == 1 ? "1 game folder" : n + " game folders"; }

    void FitColumns()
    {
        list.AutoResizeColumns(list.Items.Count > 0 ? ColumnHeaderAutoResizeStyle.ColumnContent : ColumnHeaderAutoResizeStyle.HeaderSize);
        foreach (ColumnHeader c in list.Columns) c.Width = Math.Max(c.Width, TextRenderer.MeasureText(c.Text, list.Font).Width + 24);
        list.Columns[GameColumn].Width += 24; // room for the check box
    }

    // One summary line (the Status column shows each folder), plus anything that needs attention.
    void Apply(bool install)
    {
        int done = 0;
        var notes = new List<string>();
        foreach (ListViewItem item in list.CheckedItems)
        {
            string dir = (string)item.Tag;
            string outcome;
            try
            {
                outcome = install ? Patcher.Install(dir) : Patcher.Remove(dir);
                done++;
                if (outcome.Contains("(")) notes.Add(item.Text + ": " + outcome); // e.g. another tool's dinput8.dll was kept
            }
            catch (Exception e)
            {
                outcome = "failed: " + Patcher.Explain(e);
                notes.Add(item.Text + " (" + dir + "): not done, " + Patcher.Explain(e));
            }
            Patcher.Log((install ? "install " : "remove ") + dir + ": " + outcome);
            ShowStatus(item);
        }
        if (list.CheckedItems.Count == 0) { result.Text = "Select at least one game folder first."; return; }
        var lines = new List<string>();
        if (done > 0) lines.Add((install ? "Installed in " : "Removed from ") + Folders(done) + ".");
        lines.AddRange(notes);
        if (install && done > 0) { lines.Add(""); lines.Add("You can start the game now."); }
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
            {
                if (Patcher.AddFolder(known, game)) AddRow(game);
                try { Patcher.Remember(game); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            FitColumns();
        }
    }

    // This exe runs as administrator; let Explorer open the page so the browser doesn't.
    static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = false }); }
        catch (Exception) { }
    }

    void EditSettings()
    {
        var dirs = list.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).Where(Patcher.IsInstalled).ToList();
        if (dirs.Count == 0) { result.Text = "Install the patch first, then select the game folders whose settings you want to change."; return; }
        using (var dlg = new SettingsForm(Patcher.ReadSettings(dirs[0])))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            int done = 0;
            var notes = new List<string>();
            foreach (string dir in dirs)
            {
                string outcome;
                try { Patcher.WriteSettings(dir, dlg.Settings); outcome = "settings saved"; done++; }
                catch (Exception e) { outcome = "failed: " + Patcher.Explain(e); notes.Add(Patcher.GameName(dir) + " (" + dir + "): not saved, " + Patcher.Explain(e)); }
                Patcher.Log("settings " + dir + ": " + outcome);
            }
            var lines = new List<string>();
            if (done > 0) lines.Add("Settings saved for " + Folders(done) + ".");
            lines.AddRange(notes);
            if (done > 0) { lines.Add(""); lines.Add("They take effect the next time the game starts."); }
            result.Text = string.Join("\n", lines);
        }
    }
}

class SettingsForm : Form
{
    readonly CheckBox enabled = Box("Borderless fullscreen (off: the game starts like before the patch)");
    readonly CheckBox native = Box("Always use my screen's resolution");
    readonly CheckBox scale = Box("With a lower resolution: scale it up to fill the screen (off: a box in the middle)");
    readonly CheckBox lockCursor = Box("Keep the mouse inside the game while you play");

    public Dictionary<string, bool> Settings { get; private set; }

    public SettingsForm(Dictionary<string, bool> s)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = Patcher.AppTitle + " settings";
        Icon = Patcher.AppIcon;
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        enabled.Checked = s["Enabled"];
        native.Checked = s["ForceNativeResolution"];
        scale.Checked = s["ScaleToScreen"];
        lockCursor.Checked = s["LockCursor"];
        enabled.CheckedChanged += (a, b) => UpdateEnabled();
        native.CheckedChanged += (a, b) => UpdateEnabled();
        UpdateEnabled();

        var note = new Label { AutoSize = true, UseMnemonic = false, Padding = new Padding(0, 10, 0, 6), Text = "For the selected game folders. Changes take effect the next time the game starts." };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (a, b) => Settings = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            { "Enabled", enabled.Checked }, { "ForceNativeResolution", native.Checked },
            { "ScaleToScreen", scale.Checked }, { "LockCursor", lockCursor.Checked }
        };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12) };
        foreach (Control c in new Control[] { enabled, native, scale, lockCursor, note, buttons }) layout.Controls.Add(c);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(false);
        PerformLayout();
    }

    // The scaling choice only matters when the screen's resolution isn't forced, and nothing does when it's off.
    void UpdateEnabled()
    {
        native.Enabled = lockCursor.Enabled = enabled.Checked;
        scale.Enabled = enabled.Checked && !native.Checked;
    }

    static CheckBox Box(string text)
    {
        return new CheckBox { Text = text, AutoSize = true, UseMnemonic = false, Padding = new Padding(0, 3, 0, 3) };
    }
}
