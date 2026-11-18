using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AndroidDesk;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Local\\AndroidDesk.Dashboard", out var first);
        if (!first) { MessageBox.Show("Android Desk is already running. Open it using its system-tray icon."); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new Dashboard());
    }
}

public sealed class Settings
{
    public List<SavedDevice> Devices { get; set; } = [];
    public Dictionary<string, Placement> Layout { get; set; } = [];
}

public sealed class Bridge
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "scrcpy");
    public string Bin { get; }
    public string Data { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroidDesk");
    public Settings Settings { get; private set; } = new();
    public Bridge()
    {
        Bin = Environment.GetEnvironmentVariable("ANDROID_DESK_SCRCPY_DIR") ??
            Path.Combine(Root, "scrcpy-win64-v5.0.1");
        Directory.CreateDirectory(Data);
        var path = Path.Combine(Data, "settings.json");
        if (File.Exists(path)) Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
        else
        {
            var legacy = Path.Combine(Root, "wireless-devices.json");
            if (File.Exists(legacy)) Settings.Devices = JsonSerializer.Deserialize<List<SavedDevice>>(File.ReadAllText(legacy)) ?? [];
        }
    }
    public void Save()
    {
        var path = Path.Combine(Data, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public async Task<string> Adb(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Bin, "adb.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start ADB.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new IOException("Device connection timed out. Check that it is awake and on the same network."); }
        var text = await output;
        var errors = await error;
        if (process.ExitCode != 0) throw new IOException((text + "\n" + errors).Trim());
        return text.Trim();
    }
    public async Task<List<Device>> Devices() => DeviceLogic.Deduplicate(DeviceLogic.Parse(await Adb("devices", "-l")), Settings.Devices);
    public void Remember(SavedDevice device)
    {
        Settings.Devices.RemoveAll(d => d.UsbSerial == device.UsbSerial || (device.Address.Length > 0 && d.Address == device.Address));
        Settings.Devices.Add(device); Save();
    }
    public string Name(Device device) => Settings.Devices.FirstOrDefault(s => s.Address == device.Serial || s.UsbSerial == device.Serial)?.Name ?? device.Model;
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool MoveWindow(nint handle, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint handle);
}

public sealed class Dashboard : Form
{
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ScaleLogicalLayout(this);
    }
    internal static void ScaleLogicalLayout(Form form)
    {
        // Programmatically created controls use logical dimensions, while fonts
        // already follow Windows DPI. Scale geometry once after handle creation.
        form.AutoScaleMode = AutoScaleMode.None;
        if (form.DeviceDpi != 96) form.Scale(new SizeF(form.DeviceDpi / 96f, form.DeviceDpi / 96f));
    }
    readonly Bridge bridge = new();
    readonly Dictionary<string, Process> sessions = [];
    readonly FlowLayoutPanel cards = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 42, Padding = new(18, 8, 8, 4), ForeColor = Color.Silver };
    readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "Android Desk", Visible = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    bool busy, compact, exiting, first = true;
    string lastSnapshot = "";
    List<Device> devices = [];
    static readonly Color Background = Color.FromArgb(19, 24, 34), Surface = Color.FromArgb(30, 38, 51);
    public Dashboard()
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96);
        Text = "Android Desk"; Size = new(720, 570); MinimumSize = new(660, 180);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Background; ForeColor = Color.White;
        Font = new("Segoe UI", 10);
        var header = new Panel { Dock = DockStyle.Top, Height = 76 };
        header.Controls.Add(new Label { Text = "Android Desk", Font = new("Segoe UI", 21, FontStyle.Bold), Location = new(18, 10), AutoSize = true });
        header.Controls.Add(new Label { Text = "Your devices, on your desktop", ForeColor = Color.Silver, Location = new(20, 48), AutoSize = true });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new(12, 4, 0, 0), WrapContents = false };
        actions.Controls.Add(Button("+ Add device", async () => { using var wizard = new AddDevice(bridge); wizard.ShowDialog(this); await RefreshDevices(); }));
        actions.Controls.Add(Button("Open all", async () => { foreach (var d in devices.Where(d => d.State == "device")) await Open(d); await RefreshDevices(); }));
        actions.Controls.Add(Button("Tile screens", () => { Tile(); return Task.CompletedTask; }));
        actions.Controls.Add(Button("Compact", () => { SetCompact(!compact); return Task.CompletedTask; }));
        Controls.Add(cards); Controls.Add(status); Controls.Add(actions); Controls.Add(header);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show controls", null, (_, _) => Reveal());
        menu.Items.Add("Tile screens", null, (_, _) => Tile());
        menu.Items.Add("Close all screens", null, async (_, _) => { foreach (var serial in sessions.Keys.ToArray()) CloseScreen(serial); await RefreshDevices(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Android Desk", null, (_, _) => { exiting = true; Close(); });
        menu.Opening += (_, _) =>
        {
            foreach (var item in menu.Items.Cast<ToolStripItem>().Where(i => Equals(i.Tag, "device")).ToArray())
            { menu.Items.Remove(item); item.Dispose(); }
            foreach (var device in devices.Where(d => d.State == "device"))
            {
                var entry = new ToolStripMenuItem(bridge.Name(device)) { Tag = "device" };
                entry.DropDownItems.Add("Open / show screen", null, async (_, _) => await TrayAction(async () => await Open(device)));
                entry.DropDownItems.Add("Close screen", null, async (_, _) => await TrayAction(() => { CloseScreen(device.Serial); return Task.CompletedTask; }));
                if (device.Serial.Contains(':'))
                    entry.DropDownItems.Add("Disconnect Wi-Fi", null, async (_, _) => await TrayAction(async () => { CloseScreen(device.Serial); await bridge.Adb("disconnect", device.Serial); }));
                menu.Items.Insert(2, entry);
            }
        };
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Reveal();
        timer.Tick += async (_, _) => await RefreshDevices();
        Shown += async (_, _) => { await RefreshDevices(); timer.Start(); };
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            timer.Stop(); foreach (var serial in sessions.Keys.ToArray()) CloseScreen(serial);
            bridge.Save(); tray.Visible = false; tray.Dispose();
        };
    }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    async Task TrayAction(Func<Task> action)
    {
        try { await action(); await RefreshDevices(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Android Desk"); }
    }
    void SetCompact(bool value) { compact = value; cards.Visible = !value; Height = (int)((value ? 210 : 570) * DeviceDpi / 96f); TopMost = value; }
    internal static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Color.White, Margin = new(4), Padding = new(8, 3, 8, 3) };
        button.FlatAppearance.BorderColor = Color.FromArgb(65, 79, 99);
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { await action(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Android Desk", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally { if (!button.IsDisposed) button.Enabled = true; }
        };
        return button;
    }
    async Task RefreshDevices()
    {
        if (busy || exiting) return;
        busy = true;
        try
        {
            if (first)
                foreach (var saved in bridge.Settings.Devices.Where(s => DeviceLogic.ValidAddress(s.Address)))
                    try { await bridge.Adb("connect", saved.Address); } catch { /* Offline entries stay visible. */ }
            devices = await bridge.Devices();
            foreach (var serial in sessions.Keys.ToArray())
                if (sessions[serial].HasExited) { sessions[serial].Dispose(); sessions.Remove(serial); }
                else SavePosition(serial, sessions[serial]);
            if (sessions.Count > 0) bridge.Save();
            if (first)
            {
                first = false;
                foreach (var d in devices.Where(d => d.State == "device")) await Open(d);
            }
            var snapshot = string.Join("|", devices.Select(d => $"{d.Serial}:{d.State}:{bridge.Name(d)}:{sessions.ContainsKey(d.Serial)}"))
                + JsonSerializer.Serialize(bridge.Settings.Devices);
            if (snapshot == lastSnapshot) return;
            lastSnapshot = snapshot;
            cards.SuspendLayout();
            while (cards.Controls.Count > 0) cards.Controls[0].Dispose();
            foreach (var device in devices) AddCard(device);
            foreach (var saved in bridge.Settings.Devices.Where(s => !devices.Any(d => d.Serial == s.Address || d.Serial == s.UsbSerial)))
                AddCard(new(saved.Address.Length > 0 ? saved.Address : saved.UsbSerial, saved.Name, "offline"));
            if (cards.Controls.Count == 0)
                cards.Controls.Add(new Label { Text = "No devices yet\n\nChoose Add device to connect a phone or tablet.", AutoSize = true, Padding = new(12), ForeColor = Color.Silver });
            status.Text = $"{devices.Count(d => d.State == "device")} connected  ·  {sessions.Count} screens open  ·  Closing controls hides them to the tray";
            cards.ResumeLayout();
        }
        catch (Exception ex) { status.Text = ex.Message; first = false; }
        finally { busy = false; }
    }
    void AddCard(Device d)
    {
        var row = new Panel { Width = 640, Height = 100, BackColor = Surface, Margin = new(4, 4, 4, 8) };
        var name = bridge.Name(d);
        row.Controls.Add(new Label { Text = name, Font = new(Font, FontStyle.Bold), Location = new(14, 10), AutoSize = true });
        var state = d.State == "unauthorized" ? "Unlock device and accept Allow USB debugging" : d.State == "offline" ? "Offline — reconnect or use Add device" : sessions.ContainsKey(d.Serial) ? "Screen open" : "Ready to open";
        row.Controls.Add(new Label { Text = state + (d.Serial.Contains(':') ? "  ·  Wi-Fi" : "  ·  USB"), Location = new(14, 34), AutoSize = true, ForeColor = Color.LightGray });
        var buttons = new FlowLayoutPanel { Location = new(8, 57), Width = 625, Height = 40 };
        if (d.State == "device")
        {
            buttons.Controls.Add(Button(sessions.ContainsKey(d.Serial) ? "Show screen" : "Open screen", async () => { await Open(d); await RefreshDevices(); }));
            buttons.Controls.Add(Button("Close screen", async () => { CloseScreen(d.Serial); await RefreshDevices(); }));
            buttons.Controls.Add(Button("Rename", async () =>
            {
                var value = Prompt("Device name", "Choose a name for this device", name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var saved = bridge.Settings.Devices.FirstOrDefault(s => s.Address == d.Serial || s.UsbSerial == d.Serial);
                    bridge.Remember(saved is null ? new(value, d.Serial, d.Serial.Contains(':') ? d.Serial : "") : saved with { Name = value });
                    await RefreshDevices();
                }
            }));
        }
        if (d.Serial.Contains(':'))
            buttons.Controls.Add(Button(d.State == "device" ? "Disconnect Wi-Fi" : "Reconnect", async () =>
            {
                if (d.State == "device") { CloseScreen(d.Serial); await bridge.Adb("disconnect", d.Serial); }
                else { await bridge.Adb("connect", d.Serial); }
                await RefreshDevices();
            }));
        row.Controls.Add(buttons);
        if (DeviceDpi != 96) row.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
        cards.Controls.Add(row);
    }
    async Task Open(Device d)
    {
        if (sessions.TryGetValue(d.Serial, out var existing) && !existing.HasExited)
        { existing.Refresh(); Native.ShowWindow(existing.MainWindowHandle, 9); Native.SetForegroundWindow(existing.MainWindowHandle); return; }
        var place = bridge.Settings.Layout.GetValueOrDefault(d.Serial) ?? DeviceLogic.Tile(Screen.FromControl(this).WorkingArea, sessions.Count, 3);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(place.X, place.Y, place.Width, place.Height))))
            place = DeviceLogic.Tile(Screen.FromControl(this).WorkingArea, 0, 3);
        var info = new ProcessStartInfo(Path.Combine(bridge.Bin, "scrcpy.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "--serial=" + d.Serial, "--window-title=" + bridge.Name(d) + " | Android Desk", $"--window-x={place.X}", $"--window-y={place.Y}", $"--window-width={place.Width}", $"--window-height={place.Height}", "--max-size=1600", "--no-audio", "--no-clipboard-autosync" }) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new IOException("Could not open scrcpy.");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) { if (errors.Length < 12000) errors.AppendLine(e.Data); } };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine(); process.BeginOutputReadLine();
        sessions[d.Serial] = process;
        await Task.Delay(750);
        if (process.HasExited) { sessions.Remove(d.Serial); process.Dispose(); throw new IOException("Could not open " + bridge.Name(d) + ":\n" + errors); }
    }
    void SavePosition(string serial, Process p)
    {
        if (p.HasExited) return;
        p.Refresh();
        if (p.MainWindowHandle != 0 && Native.GetWindowRect(p.MainWindowHandle, out var r) && r.Left > -10000)
            bridge.Settings.Layout[serial] = new(r.Left, r.Top + 30, r.Right - r.Left, Math.Max(180, r.Bottom - r.Top - 30));
    }
    void CloseScreen(string serial)
    {
        if (!sessions.Remove(serial, out var p)) return;
        try { SavePosition(serial, p); if (!p.HasExited && !p.CloseMainWindow()) p.Kill(); }
        catch (InvalidOperationException) { }
        finally { p.Dispose(); bridge.Save(); }
    }
    void Tile()
    {
        var active = sessions.Where(s => !s.Value.HasExited).ToArray();
        for (int i = 0; i < active.Length; i++)
        {
            var (serial, process) = active[i]; process.Refresh();
            var p = DeviceLogic.Tile(Screen.FromControl(this).WorkingArea, i, active.Length);
            Native.ShowWindow(process.MainWindowHandle, 9);
            Native.MoveWindow(process.MainWindowHandle, p.X, p.Y, p.Width, p.Height, true);
            SavePosition(serial, process);
        }
        bridge.Save();
    }
    internal static string? Prompt(string title, string label, string value = "")
    {
        using var form = new Form { Text = title, ClientSize = new(420, 130), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var field = new TextBox { Text = value, Location = new(16, 44), Width = 385 };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new(310, 85) };
        form.Controls.Add(new Label { Text = label, AutoSize = true, Location = new(16, 16) }); form.Controls.Add(field); form.Controls.Add(ok); form.AcceptButton = ok;
        return form.ShowDialog() == DialogResult.OK ? field.Text.Trim() : null;
    }
}
