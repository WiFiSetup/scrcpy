using AndroidDesk;
using System.Drawing;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var devices = DeviceLogic.Parse("List of devices attached\nUSB123 device product:test model:Example_Tablet transport_id:1\n192.0.2.2:5555 device model:Example_Tablet\nUSB456 unauthorized\n192.0.2.3:5555 offline\n* daemon started successfully\n");
Check(devices.Count == 4, "Parse USB, Wi-Fi, unauthorized and offline without parsing headers");
Check(devices[0].Model == "Example Tablet", "Readable model");
var saved = new List<SavedDevice> { new("Test tablet", "USB123", "192.0.2.2:5555") };
Check(DeviceLogic.Deduplicate(devices, saved).Count == 3, "USB and Wi-Fi for same device must only appear once");
var offline = devices.Select(d => d.Serial == "192.0.2.2:5555" ? d with { State = "offline" } : d).ToList();
Check(DeviceLogic.Deduplicate(offline, saved).Any(d => d.Serial == "USB123"), "Offline Wi-Fi must not hide working USB");
var leftMonitor = new Rectangle(-1920, 0, 1920, 1080);
for (int i = 0; i < 8; i++)
{
    var p = DeviceLogic.Tile(leftMonitor, i, 8);
    Check(p.X >= leftMonitor.Left && p.X + p.Width <= leftMonitor.Right && p.Y >= 0 && p.Y + p.Height <= 1080, "Tile on negative-origin monitor without going offscreen");
}
Check(DeviceLogic.ValidAddress("192.0.2.2:5555") && DeviceLogic.ValidAddress("[2001:db8::1]:37000"), "IPv4 and IPv6 endpoints");
Check(!DeviceLogic.ValidAddress("192.0.2.2:0") && !DeviceLogic.ValidAddress("bad-address"), "Reject missing and invalid ports");
var mdns = "List of discovered mdns services\nadb-test _adb-tls-pairing._tcp 192.0.2.2:12345\nadb-test _adb-tls-connect._tcp 192.0.2.2:43210\nadb-other _adb-tls-pairing._tcp invalid\n";
Check(DeviceLogic.Services(mdns, "_adb-tls-pairing._tcp").SequenceEqual(new[] { "192.0.2.2:12345" }), "Keep pairing and connection ports separate and validate discovery results");
Console.WriteLine("All device parsing, deduplication, discovery, address and layout checks passed.");
