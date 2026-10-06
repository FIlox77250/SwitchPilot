namespace SwitchPilot.Core.Platforms;

/// <summary>Reference = historical drivers exercised on hardware; Experimental = built from vendor documentation and recorded outputs.</summary>
public enum PlatformMaturity { Reference, Experimental }

/// <summary>How a platform makes a change permanent.</summary>
public enum SaveModel
{
    /// <summary>"write memory" / "copy running-config startup-config" / "save".</summary>
    Explicit,
    /// <summary>Junos: every change ends with "commit", which is already persistent.</summary>
    Commit,
    /// <summary>RouterOS, UniFi: the device stores every accepted change immediately.</summary>
    Implicit
}

/// <summary>Static description of a supported switch family.</summary>
public sealed record SwitchPlatform(SwitchVendor Vendor, string DisplayName, string ShortName, bool CanWrite, bool Tdr,
    SaveModel Save, PlatformMaturity Maturity, string Notes)
{
    public ConfigDialect Dialect => ConfigDialects.For(Vendor);
    public bool IsExperimental => Maturity == PlatformMaturity.Experimental;
    public override string ToString() => DisplayName;
}

public static class SwitchPlatforms
{
    private static readonly SwitchPlatform[] Catalog =
    [
        new(SwitchVendor.Cisco, "Cisco IOS / IOS-XE", "IOS", true, true, SaveModel.Explicit, PlatformMaturity.Reference,
            "configure terminal … end, write memory (confirmation [OK] exigée)."),
        new(SwitchVendor.AlliedTelesis, "Allied Telesis AlliedWare Plus", "AW+", true, false, SaveModel.Explicit, PlatformMaturity.Reference,
            "Syntaxe proche d'IOS ; trunk par « allowed vlan none » puis « add »."),
        new(SwitchVendor.AlliedS95, "Allied Telesis AT-S95 (AT-8000GS)", "AT-S95", false, false, SaveModel.Explicit, PlatformMaturity.Reference,
            "Lecture, détection de port et export uniquement."),
        new(SwitchVendor.CiscoNxos, "Cisco NX-OS (Nexus)", "NX-OS", true, false, SaveModel.Explicit, PlatformMaturity.Experimental,
            "Ports Ethernet1/1 ; « switchport » explicite ; copy running-config startup-config."),
        new(SwitchVendor.Arista, "Arista EOS", "EOS", true, false, SaveModel.Explicit, PlatformMaturity.Experimental,
            "Ports Ethernet1 ; write memory (« Copy completed successfully »)."),
        new(SwitchVendor.DellOs6, "Dell EMC Networking OS6 (N-Series)", "OS6", true, false, SaveModel.Explicit, PlatformMaturity.Experimental,
            "configure … end ; write memory avec confirmation (y/n)."),
        new(SwitchVendor.DellOs9, "Dell EMC Networking OS9 (FTOS)", "OS9", true, false, SaveModel.Explicit, PlatformMaturity.Experimental,
            "Appartenance VLAN centrée VLAN (interface vlan N / untagged) ; trunk non pris en charge."),
        new(SwitchVendor.DellOs10, "Dell EMC SmartFabric OS10", "OS10", true, false, SaveModel.Explicit, PlatformMaturity.Experimental,
            "Ports ethernet1/1/1 ; VLAN natif = access vlan du trunk ; show running-configuration."),
        new(SwitchVendor.Huawei, "Huawei VRP (S / CloudEngine)", "VRP", true, false, SaveModel.Explicit, PlatformMaturity.Experimental,
            "system-view … return ; port link-type ; save avec confirmation [Y/N]."),
        new(SwitchVendor.Juniper, "Juniper Junos (EX / QFX, ELS)", "Junos", true, false, SaveModel.Commit, PlatformMaturity.Experimental,
            "configure private, set/delete, commit and-quit ; annulation par rollback 0."),
        new(SwitchVendor.MikroTik, "MikroTik RouterOS (bridge VLAN)", "RouterOS", true, false, SaveModel.Implicit, PlatformMaturity.Experimental,
            "Commandes /interface bridge … ; enregistrement immédiat ; trunk non pris en charge."),
        new(SwitchVendor.UbiquitiEdge, "Ubiquiti EdgeSwitch (EdgeOS / FASTPATH)", "EdgeSwitch", true, false, SaveModel.Explicit, PlatformMaturity.Experimental,
            "vlan database, vlan pvid / participation / tagging ; write memory avec confirmation (y/n)."),
        new(SwitchVendor.UniFi, "Ubiquiti UniFi (contrôleur ou SSH)", "UniFi", true, false, SaveModel.Implicit, PlatformMaturity.Experimental,
            "Écriture par l'API du contrôleur (port_overrides) ; SSH local en lecture seule.")
    ];

    public static IReadOnlyList<SwitchPlatform> All { get; } = Array.AsReadOnly(Catalog);

    public static SwitchPlatform Get(SwitchVendor vendor) =>
        Catalog.FirstOrDefault(p => p.Vendor == vendor) ?? throw new ArgumentException("Constructeur de switch inconnu.");
}
