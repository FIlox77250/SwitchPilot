using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
namespace SwitchPilot.Infrastructure.Serial;

public record SerialDevice(string Port, string Name, string Vendor, uint ErrorCode, string Guidance)
{
    public bool Available => Port.Length > 0 && ErrorCode == 0;
    public string Label => $"{Name}{(Vendor.Length > 0 ? " · " + Vendor : "")}";
    public override string ToString() => Label;
}
[SupportedOSPlatform("windows")]
public static class SerialDeviceCatalog
{
    public static IReadOnlyList<SerialDevice> Scan()
    {
        var ports = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<SerialDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity");
            using var devices = searcher.Get();
            foreach (ManagementObject item in devices)
            {
                using (item)
                {
                    var id = item["PNPDeviceID"] as string ?? ""; var name = item["Name"] as string ?? "Adaptateur USB";
                    var vendor = Vendor(id); var match = Regex.Match(name, @"\((COM[1-9]\d*)\)", RegexOptions.IgnoreCase);
                    var port = match.Success && ports.Contains(match.Groups[1].Value) ? match.Groups[1].Value : "";
                    var error = Convert.ToUInt32(item["ConfigManagerErrorCode"] ?? 0u);
                    if (port.Length == 0 && (vendor.Length == 0 || error == 0)) continue;
                    result.Add(new(port, name, vendor, error, error == 0 ? "Pilote installé" : Guidance(vendor, error)));
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { result.Add(new("", "Inventaire des pilotes indisponible", "", 1, "Les ports COM restent listés. Vérifiez les pilotes dans le Gestionnaire de périphériques.")); }
        foreach (var port in ports.Where(p => !result.Any(d => d.Port.Equals(p, StringComparison.OrdinalIgnoreCase))))
            result.Add(new(port, port, "", 0, "Nom convivial non disponible"));
        return result.OrderBy(d => d.Port).ThenBy(d => d.Name).ToArray();
    }
    public static string Vendor(string id) => id.ToUpperInvariant() switch
    {
        var s when s.Contains("VID_0403") => "FTDI",
        var s when s.Contains("VID_067B") => "Prolific",
        var s when s.Contains("VID_10C4") => "Silicon Labs",
        var s when s.Contains("VID_05A6&PID_0009") => "Cisco USB Console",
        _ => ""
    };
    private static string Guidance(string vendor, uint error) => $"Windows : code {error}. " + (vendor switch
    {
        "Cisco USB Console" => "Téléchargez le pilote USB Console correspondant au modèle depuis software.cisco.com avec un compte Cisco, puis installez-le via le Gestionnaire de périphériques. Aucun téléchargement automatique.",
        "FTDI" => "Utilisez Windows Update ou le pilote VCP officiel sur ftdichip.com/drivers/vcp-drivers/.",
        "Prolific" => "Utilisez Windows Update ou le pilote officiel compatible avec votre puce sur prolific.com.tw.",
        "Silicon Labs" => "Utilisez Windows Update ou le pilote CP210x officiel sur silabs.com/developers/usb-to-uart-bridge-vcp-drivers.",
        _ => "Ouvrez le Gestionnaire de périphériques et consultez le fabricant du câble."
    });
}
