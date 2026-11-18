using System.Text.RegularExpressions;

namespace AndroidDesk;

public sealed class AddDevice : Form
{
    protected override void OnLoad(EventArgs e) { base.OnLoad(e); Dashboard.ScaleLogicalLayout(this); }
    readonly Bridge bridge;
    readonly ComboBox usb = new() { Width = 490, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox pairing = new() { Width = 490 };
    readonly TextBox code = new() { Width = 180, PlaceholderText = "Six-digit pairing code", MaxLength = 6 };
    readonly TextBox connect = new() { Width = 490, PlaceholderText = "Connection IP:port (only if discovery cannot find it)" };
    readonly TextBox name = new() { Width = 490, PlaceholderText = "Friendly name (optional)" };
    readonly Label result = new() { Width = 520, Height = 65, ForeColor = Color.FromArgb(30, 90, 70) };
    readonly System.Windows.Forms.Timer scan = new() { Interval = 2500 };
    readonly Dictionary<string, Device> usbDevices = [];
    bool scanning;
    public AddDevice(Bridge bridge)
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new(96, 96);
        this.bridge = bridge;
        Text = "Add a device | Android Desk"; ClientSize = new(580, 790); StartPosition = FormStartPosition.CenterParent;
        Font = new("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 250); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var page = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(22), AutoScroll = true };
        void Label(string text, int height = 26) => page.Controls.Add(new Label { Text = text, Width = 520, Height = height });
        page.Controls.Add(new Label { Text = "Connect another screen", AutoSize = true, Font = new(Font.FontFamily, 19, FontStyle.Bold), Margin = new(0, 0, 0, 12) });
        Label("Name your device"); page.Controls.Add(name);
        var tabs = new TabControl { Width = 530, Height = 465, Margin = new(0, 18, 0, 8) };
        var usbTab = new TabPage("USB · easiest"); var wifiTab = new TabPage("Wireless · Android 11+");
        tabs.TabPages.AddRange([usbTab, wifiTab]);
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (tabs.SelectedTab == wifiTab)
                try { await Discover(); } catch (Exception ex) { result.Text = ex.Message; }
        };
        FlowLayoutPanel Body(TabPage parent)
        {
            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(10) };
            parent.Controls.Add(body); return body;
        }
        var u = Body(usbTab);
        u.Controls.Add(new Label { Text = "1. Connect the device with a USB data cable.\n2. Enable Developer options → USB debugging.\n3. Unlock it and accept Allow USB debugging.\n\nWe detect the device automatically below.", Width = 490, Height = 125 });
        u.Controls.Add(usb);
        u.Controls.Add(Dashboard.Button("Use USB connection", () =>
        {
            var device = SelectedUsb();
            bridge.Remember(new(NameFor(device.Model), device.Serial, ""));
            result.Text = "Added. Select Open screen in the dashboard. USB must remain connected for this connection.";
            return Task.CompletedTask;
        }));
        u.Controls.Add(Dashboard.Button("Set up Wi-Fi, then unplug USB", async () =>
        {
            var device = SelectedUsb();
            result.Text = "Finding the device on Wi-Fi…";
            var route = await bridge.Adb("-s", device.Serial, "shell", "ip", "route");
            var match = Regex.Match(route, @"(?m)^.*\bdev wlan\S*.*\bsrc (\d+\.\d+\.\d+\.\d+)");
            if (!match.Success) throw new IOException("Connect the Android device to the same Wi-Fi network as this PC, then try again.");
            var address = match.Groups[1].Value + ":5555";
            await bridge.Adb("-s", device.Serial, "tcpip", "5555");
            await Task.Delay(1500);
            var response = await bridge.Adb("connect", address);
            if (!(await bridge.Devices()).Any(d => d.Serial == address && d.State == "device")) throw new IOException(response);
            bridge.Remember(new(NameFor(device.Model), device.Serial, address));
            result.Text = "Wi-Fi connected. You can unplug USB now. After a device reboot, repeat this USB setup or use wireless pairing.";
        }));
        u.Controls.Add(new Label { Text = "USB-assisted Wi-Fi needs setup again after a reboot.\nUse the Wireless tab for Android's saved pairing instead.", Width = 490, Height = 50 });
        var w = Body(wifiTab);
        w.Controls.Add(new Label { Text = "1. Use the same Wi-Fi network as this PC.\n2. Enable Developer options → Wireless debugging.\n3. Open Pair device with pairing code.\n4. Select the discovered address and enter that code.\n\nLeave that pairing page open while connecting.", Width = 490, Height = 125 });
        w.Controls.Add(pairing); w.Controls.Add(code);
        w.Controls.Add(Dashboard.Button("Discover pairing devices", Discover));
        w.Controls.Add(connect);
        w.Controls.Add(Dashboard.Button("Pair and connect", Pair));
        w.Controls.Add(Dashboard.Button("Connect paired device", async () =>
        {
            if (!DeviceLogic.ValidAddress(connect.Text.Trim())) throw new IOException("Enter the connection IP:port from the main Wireless debugging page.");
            await ConnectPaired(connect.Text.Trim());
        }));
        w.Controls.Add(new Label { Text = "If discovery is blocked: type the pairing IP:port above, and the separate IP:port from the main Wireless debugging page below it. Pairing does not require USB.", Width = 490, Height = 60 });
        page.Controls.Add(tabs); page.Controls.Add(result);
        var done = new Button { Text = "Done", DialogResult = DialogResult.OK, AutoSize = true, Margin = new(0, 4, 0, 0) };
        page.Controls.Add(done); Controls.Add(page);
        scan.Tick += async (_, _) => await ScanUsb();
        Shown += async (_, _) => { await ScanUsb(); scan.Start(); };
        FormClosed += (_, _) => { scan.Stop(); scan.Dispose(); };
    }
    string NameFor(string fallback) => string.IsNullOrWhiteSpace(name.Text) ? fallback : name.Text.Trim();
    Device SelectedUsb()
    {
        if (usb.SelectedItem is not string key || !usbDevices.TryGetValue(key, out var d)) throw new IOException("No USB device detected yet. Check the cable and enable USB debugging.");
        if (d.State != "device") throw new IOException("Unlock the Android device and accept the USB debugging permission prompt.");
        return d;
    }
    async Task ScanUsb()
    {
        if (scanning || IsDisposed) return;
        scanning = true;
        try
        {
            var devices = DeviceLogic.Parse(await bridge.Adb("devices", "-l")).Where(d => !d.Serial.Contains(':') && !d.Serial.StartsWith("adb-")).ToArray();
            if (IsDisposed) return;
            var selected = usb.SelectedItem as string;
            var entries = devices.Select(d => $"{d.Model} · {d.Serial} · {d.State}").ToArray();
            if (!entries.SequenceEqual(usb.Items.Cast<string>()))
            {
                usb.Items.Clear(); usbDevices.Clear();
                for (int i = 0; i < devices.Length; i++) { usb.Items.Add(entries[i]); usbDevices[entries[i]] = devices[i]; }
                if (selected != null && usb.Items.Contains(selected)) usb.SelectedItem = selected;
                else if (usb.Items.Count > 0) usb.SelectedIndex = 0;
            }
        }
        catch (Exception ex) { if (!IsDisposed) result.Text = ex.Message; }
        finally { scanning = false; }
    }
    async Task Discover()
    {
        var text = await bridge.Adb("mdns", "services");
        var addresses = DeviceLogic.Services(text, "_adb-tls-pairing._tcp");
        var current = pairing.Text;
        pairing.Items.Clear(); foreach (var address in addresses) pairing.Items.Add(address);
        pairing.Text = addresses.Contains(current) ? current : addresses.FirstOrDefault() ?? current;
        result.Text = addresses.Count > 0 ? "Pairing device found. Enter the six-digit code shown on that Android device." : "No pairing service found. Keep Pair device with pairing code open, then discover again—or type its IP:port.";
    }
    async Task Pair()
    {
        var address = pairing.Text.Trim();
        if (!DeviceLogic.ValidAddress(address)) throw new IOException("Select a discovered pairing address or enter the pairing IP:port shown on your device.");
        if (!Regex.IsMatch(code.Text, @"^\d{6}$")) throw new IOException("Enter the six-digit pairing code shown on your device.");
        result.Text = "Pairing…";
        await bridge.Adb("pair", address, code.Text);
        code.Clear();
        await Task.Delay(1500);
        var services = DeviceLogic.Services(await bridge.Adb("mdns", "services"), "_adb-tls-connect._tcp");
        var host = System.Net.IPEndPoint.Parse(address).Address;
        var target = connect.Text.Trim();
        if (target.Length == 0) target = services.FirstOrDefault(a => System.Net.IPEndPoint.Parse(a).Address.Equals(host)) ?? "";
        if (!DeviceLogic.ValidAddress(target)) throw new IOException("Pairing succeeded. Enter IP:port from the main Wireless debugging page in the connection field, then use Connect paired device below.");
        await ConnectPaired(target);
    }
    async Task ConnectPaired(string target)
    {
        await bridge.Adb("connect", target);
        var d = (await bridge.Devices()).FirstOrDefault(d => d.Serial == target && d.State == "device") ?? throw new IOException("Paired, but connection is not ready. Check Wireless debugging is enabled.");
        var serial = await bridge.Adb("-s", target, "shell", "getprop", "ro.serialno");
        bridge.Remember(new(NameFor(d.Model), serial, target));
        result.Text = "Device paired and connected. Select Done, then Open screen. Android may require Wireless debugging to be enabled again after a reboot.";
    }
}
