using System.Text.RegularExpressions;
using System.Drawing;

namespace AndroidDesk;

public record Device(string Serial, string Model, string State);
public record SavedDevice(string Name, string UsbSerial, string Address);
public record Placement(int X, int Y, int Width, int Height);
public static class DeviceLogic
{
    public static List<Device> Parse(string output) => output.Split('\n')
        .Select(line => Regex.Match(line.Trim(), @"^(\S+)\s+(device|unauthorized|offline)\b(.*)$"))
        .Where(m => m.Success).Select(m => new Device(m.Groups[1].Value,
            Regex.Match(m.Groups[3].Value, @"\bmodel:(\S+)") is { Success: true } model
                ? model.Groups[1].Value.Replace('_', ' ') : "Android device", m.Groups[2].Value)).ToList();

    public static List<Device> Deduplicate(List<Device> devices, List<SavedDevice> saved) => devices
        .Where(d => !saved.Any(s => s.UsbSerial == d.Serial && devices.Any(w => w.Serial == s.Address && w.State == "device")))
        .ToList();

    public static Placement Tile(Rectangle area, int index, int count)
    {
        var columns = Math.Min(Math.Max(count, 1), 3);
        var rows = (Math.Max(count, 1) + columns - 1) / columns;
        var slotWidth = area.Width / columns;
        var slotHeight = area.Height / rows;
        return new(area.Left + index % columns * slotWidth + 24,
            area.Top + index / columns * slotHeight + 70,
            Math.Max(160, Math.Min(720, slotWidth - 48)), Math.Max(180, Math.Min(900, slotHeight - 110)));
    }

    public static bool ValidAddress(string value) => System.Net.IPEndPoint.TryParse(value, out var endpoint)
        && endpoint.Port > 0;

    public static List<string> Services(string text, string type) => text.Split('\n')
        .Where(l => l.Contains(type, StringComparison.Ordinal))
        .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
        .Where(ValidAddress).Distinct().ToList();
}
