# Composants tiers

Switch Pilot utilise des composants non modifiés. Les versions exactes et dépendances transitives sont consignées dans les fichiers `packages.lock.json`.

| Composant | Version dans Switch Pilot 1.0.6 | Licence / source |
| --- | --- | --- |
| .NET Runtime et Windows Desktop / WPF | 10.0.12 | [MIT, notices runtime](https://github.com/dotnet/runtime/blob/v10.0.12/THIRD-PARTY-NOTICES.TXT), [WPF](https://github.com/dotnet/wpf) |
| SSH.NET | 2026.0.0 | MIT, Copyright © Renci 2010–2026, [source](https://github.com/sshnet/SSH.NET) |
| SharpPcap | 6.3.1 | MIT, [source](https://github.com/dotpcap/sharppcap) |
| PacketDotNet | 1.4.8 | MPL-2.0, [source correspondant](https://github.com/dotpcap/packetnet/tree/690707ce56d6e9c266daf6236c4f76ac5035334c), [licence](https://www.mozilla.org/MPL/2.0/) |
| BouncyCastle.Cryptography | 2.7.0 | MIT, [source](https://github.com/bcgit/bc-csharp) |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT, [source](https://github.com/dotnet/runtime) |
| System.IO.Ports (assemblies et bibliothèques natives associées) | 10.0.12 | MIT, [source](https://github.com/dotnet/runtime/tree/v10.0.12/src/libraries/System.IO.Ports) |
| System.Management | 10.0.12 | MIT, [source](https://github.com/dotnet/runtime/tree/v10.0.12/src/libraries/System.Management) |
| System.CodeDom | 10.0.12 | MIT, [source](https://github.com/dotnet/runtime/tree/v10.0.12/src/libraries/System.CodeDom) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | MIT, [source](https://github.com/dotnet/runtime) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT, [source](https://github.com/dotnet/runtime) |

Les textes complets de licence et notices de distribution sont fournis dans `docs/licenses/` et intégrés à l'exécutable. Aucune modification de PacketDotNet n'est incluse ; son code source est accessible via le lien ci-dessus et son dépôt amont. Npcap n'est pas inclus : son installation et sa licence sont séparées.

Npcap 1.89 peut être téléchargé à la demande depuis son site officiel, après accord de l’utilisateur. L’installateur reste interactif et soumis à la [licence Npcap](https://npcap.com/). Aucun binaire Npcap, pilote de câble console ou installateur tiers n’est redistribué dans cette livraison.

Les paquets xUnit et Microsoft.NET.Test.Sdk servent uniquement aux tests et ne sont pas distribués dans l'exécutable. Python et Paramiko 5.0.0 (LGPL-2.1) servent uniquement aux émulateurs de développement ; ils ne sont ni requis ni embarqués dans l’application.
