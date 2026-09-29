# Composants tiers

Switch Pilot utilise des composants non modifiés. Les versions exactes et dépendances transitives sont consignées dans les fichiers `packages.lock.json`.

| Composant | Version du build initial | Licence / source |
| --- | --- | --- |
| .NET Runtime et Windows Desktop / WPF | 10.0.12 | [MIT, notices runtime](https://github.com/dotnet/runtime/blob/v10.0.12/THIRD-PARTY-NOTICES.TXT), [WPF](https://github.com/dotnet/wpf) |
| SSH.NET | 2026.0.0 | MIT, Copyright © Renci 2010–2026, [source](https://github.com/sshnet/SSH.NET) |
| SharpPcap | 6.3.1 | MIT, [source](https://github.com/dotpcap/sharppcap) |
| PacketDotNet | 1.4.8 | MPL-2.0, [source correspondant](https://github.com/dotpcap/packetnet/tree/690707ce56d6e9c266daf6236c4f76ac5035334c), [licence](https://www.mozilla.org/MPL/2.0/) |
| BouncyCastle.Cryptography | 2.7.0 | MIT, [source](https://github.com/bcgit/bc-csharp) |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT, [source](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT, [source](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT, [source](https://github.com/dotnet/runtime) |

Les textes complets de licence et notices de distribution sont fournis dans `docs/licenses/` et intégrés à l'exécutable. Aucune modification de PacketDotNet n'est incluse ; son code source est accessible via le lien ci-dessus et son dépôt amont. Npcap n'est pas inclus : son installation et sa licence sont séparées.

Les paquets xUnit et Microsoft.NET.Test.Sdk servent uniquement aux tests et ne sont pas distribués dans l'exécutable.
