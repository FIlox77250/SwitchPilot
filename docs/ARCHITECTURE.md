# Architecture multi-constructeurs (1.1.0)

Switch Pilot parle à chaque switch à travers un **pilote** (pattern Adapter/Driver). L'interface graphique, les diagnostics et l'inventaire ne connaissent que l'API commune ; tout ce qui dépend du constructeur (commandes, prompts, parsing, validation) vit dans le pilote et sa plateforme.

```
           SwitchPilot.App (WPF/MVVM)
                    │  ISwitchDriver
                    ▼
   SwitchDriverFactory ──► DeviceDetector (prompt, show version / show system / display version)
                    │
   ┌────────────────┼──────────────────────────────────────────────┐
   │ SwitchDriver (abstrait) : verrou, audit, sauvegarde, sécurité │
   ├── CliSwitchDriver (ICliSession : SSH ou console)              │
   │     ├── CiscoIosDriver        (référence IOS / IOS-XE)        │
   │     ├── AlliedTelesisDriver   (référence AlliedWare Plus)     │
   │     ├── AlliedS95Driver       (AT-8000GS, lecture seule)      │
   │     ├── CiscoLikeDriver       (NX-OS, EOS, Dell OS6/OS9/OS10) │
   │     ├── HuaweiDriver          (VRP, commit à deux étapes)     │
   │     ├── JunosDriver           (commit / rollback)             │
   │     ├── RouterOsDriver        (sauvegarde implicite)          │
   │     ├── EdgeSwitchDriver      (FASTPATH, write memory)        │
   │     └── UniFiSshDriver        (SSH local, lecture seule)      │
   └── UniFiControllerDriver (HTTPS, API du contrôleur UniFi)      │
   └───────────────────────────────────────────────────────────────┘
```

## API commune

`SwitchDriver` (`src/SwitchPilot.Infrastructure/Drivers/SwitchDriver.cs`) expose les opérations demandées, en plus des fonctions historiques (snapshot, table MAC, compteurs, TDR, export) :

| Opération | Méthode |
| --- | --- |
| detect_device | `DetectDeviceAsync()` → constructeur, plateforme, nom, modèle, version |
| get_ports | `GetPortsAsync()` |
| get_vlans | `GetVlansAsync()` |
| set_port_vlan | `SetPortVlanAsync(port, vlan, safety, dryRun)` |
| get_link_status | `GetLinkStatusAsync(port)` |
| save_config | `SaveConfigAsync(dryRun)` (`write memory`, `save`, `commit` ou sauvegarde implicite) |

Chaque écriture passe par le même pipeline `ApplyAsync`. Les étapes sont les suivantes :

1. Refus des familles en lecture seule.
2. Contrôle que le plan vise bien la famille connectée.
3. Simulation.
4. Politique de sécurité : port de gestion, contexte de moins de 30 s.
5. Revérification de l'état : VLAN existant, VLAN courant inchangé.
6. **Sauvegarde chiffrée obligatoire** de la configuration.
7. Exécution des étapes, chacune avec son expression attendue.
8. En cas d'échec, audit « Échec après N/M commandes » puis récupération propre au pilote.

## Plateformes et dialectes

- `Core/Platforms/SwitchPlatform.cs` : tableau des 13 plateformes. Chacune porte son nom affiché, le droit d'écriture, la disponibilité du TDR, le **modèle de sauvegarde** (`Explicit`, `Commit`, `Implicit`) et sa maturité (`Reference` / `Experimental`).
- `Core/Platforms/ConfigDialect.cs`, `DeclarativeDialects.cs` : génération des commandes par plateforme. `Access`, `Trunk`, `Enabled`, `Describe`, `CreateVlan`, `DeleteVlan` et `Save` produisent des `PlanStep` vérifiables.
- `Core/Platforms/PortNames.cs` : grammaire des interfaces par plateforme (forme affichée, forme de commande, clé de comparaison). Exemples : `Gi0/1` = `GigabitEthernet0/1`, `GE0/0/1`, `ge-0/0/1`, `ae0`, `Port 7`.

## Validation propre à chaque OS

| OS | Entrée en configuration | Validation |
| --- | --- | --- |
| IOS / AW+ / NX-OS / EOS / Dell | `configure terminal` … `end` | `write memory` / `copy running-config startup-config` séparé |
| Huawei VRP | `system-view` … `return` | `commit` si le prompt `[*…]` l'exige, puis `save` (confirmation `y`) ; récupération `return` + `n` |
| Junos | `configure private`, `set` / `delete` … `commit and-quit` | Commit atomique ; échec → `rollback 0` puis sortie du mode configuration |
| RouterOS | Commandes `/interface bridge …` | Appliquées immédiatement, sauvegarde implicite |
| EdgeSwitch | `configure` / `vlan database` … `exit` | `write memory` (confirmation `y`) ; `vlan participation include/exclude` par interface |
| UniFi contrôleur | API `PUT rest/device/{id}` (`port_overrides`), `rest/networkconf` | Le contrôleur reprovisionne le switch, sans sauvegarde à part |
| UniFi SSH | `info`, `swctrl port show`, `swctrl mac show` | Lecture seule (le contrôleur écraserait la configuration locale) |

## Parsing

`Core/Parsing` regroupe les parseurs. Toutes les sorties sont des fixtures testées.

- **Expressions régulières et colonnes** : `CiscoLikeParser`, `HuaweiParser`, `JunosParser`, `RouterOsParser`, `UniFiShellParser`, plus les aides `ColumnTable` et `ParseKit`.
- **TextFSM** : moteur `TextFsm` compatible avec les modèles ntc-templates (Value Filldown / Required / List, Record, Continue, Error, transitions d'état, EOF). Il sert par exemple à `show port all` sur EdgeSwitch.
- **JSON** : `UniFiParser` (contrôleur) ; `RouterOsParser` lit aussi le format `print terse`.

## Factory et détection

`SwitchDriverFactory` (`Infrastructure/Drivers`) propose trois points d'entrée :

- `Create(vendor, session, audit, backup)` : sélection manuelle.
- `DetectAsync(session)` : détection automatique, sans pilote.
- `CreateDetectedAsync(session, fallback, audit, backup)` : détection puis création du pilote. La plateforme retenue est inscrite au journal d'audit.

`DeviceDetector` procède en deux temps :

1. **Indice tiré du prompt** :
   - `<HUAWEI>` ou `[~HUAWEI]` → Huawei.
   - `user@switch>` → Junos.
   - `[admin@MikroTik] >` → RouterOS.
   - `(EdgeSwitch) >` → EdgeSwitch.
2. **Sondes adaptées à l'indice** : `display version`, `show version`, `/system resource print`, `show system`, `info`. Une commande refusée par le switch passe simplement à la sonde suivante.

Sans résultat, le constructeur choisi par l'utilisateur sert de repli.

## Ajouter un constructeur

1. Ajouter la valeur à la **fin** de `SwitchVendor`, puisque les profils enregistrent l'entier.
2. Décrire la plateforme dans `SwitchPlatforms` et sa grammaire de ports dans `PortNames`.
3. Écrire un `ConfigDialect`, un parseur et ses fixtures.
4. Dériver de `CliSwitchDriver`, puis implémenter `SnapshotCoreAsync`, `MacTableCoreAsync`, `CountersCoreAsync`, `ExportCoreAsync` et, si nécessaire, `RecoverAsync`.
5. Brancher le pilote dans `SwitchDriverFactory.Create` et ses signatures dans `DeviceDetector`.
