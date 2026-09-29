# Switch Pilot

Application Windows de gestion de switchs Cisco IOS, centrée sur la détection du port Ethernet du poste. Première cible : Catalyst 2960 Plus / IOS 15.2. Version 1.0.1.

## Démarrer

[Télécharger SwitchPilot.exe pour Windows x64](https://github.com/FIlox77250/SwitchPilot/releases/download/v1.0.1/SwitchPilot.exe) · [Guide d’installation](docs/INSTALLATION.md) · [Version 1.0.1 et fichiers associés](https://github.com/FIlox77250/SwitchPilot/releases/tag/v1.0.1)

Lancer `artifacts/win-x64/SwitchPilot.exe` par double-clic sur Windows x64. Aucun Python, Java ou .NET à installer. L'application SSH s'exécute avec les droits de l'utilisateur courant. Les commandes IOS nécessitent les autorisations correspondantes sur le switch.

Cliquer sur **Démonstration** pour découvrir l'application sans équipement. Toutes les données sont alors fictives et toute modification reste en mémoire. Le bandeau indique ce mode en permanence. `SwitchPilot.exe --demo` ouvre directement cette démonstration.

Pour un vrai switch :

1. Cliquer sur **Connecter un switch**, saisir son adresse, l'utilisateur, le mot de passe SSH et, si nécessaire, le mot de passe `enable`.
2. Vérifier l'empreinte de la clé SSH à la première connexion. Un changement de clé exige une nouvelle confirmation.
3. Choisir la carte Ethernet physique dans **Mon branchement** et cliquer sur **Détecter mon port**. Une surveillance périodique (8 secondes) et des événements réseau relancent la recherche. Les adresses MAC ne sont pas transmises à un service externe.
4. Consulter les ports, VLANs et diagnostics. **Simulation des modifications** est activée par défaut.
5. Pour réellement modifier le switch, désactiver la simulation, puis examiner et confirmer les commandes de chaque action.

La connexion SSH doit être configurée au préalable sur le switch. Pour certains IOS anciens, activer explicitement **Compatibilité SSH ancien IOS** si la négociation moderne échoue. Cette option autorise les algorithmes hérités disponibles dans SSH.NET, sans désactiver la vérification de la clé d'hôte. Aucun repli Telnet n'est effectué.

## Fonctions

| Vue | Fonctions |
| --- | --- |
| Mon branchement | Sélection Ethernet, lecture de la MAC locale, recherche dans la table MAC du switch actif, port/VLAN/vitesse/duplex, source et heure du résultat. |
| Ports | État, vitesse, duplex, VLAN, mode opérationnel ou administratif connu, description complète lorsque disponible, filtre, VLAN access, trunk (VLAN natif + liste autorisée), shutdown/no shutdown, description. |
| VLANs | Création/renommage et suppression. Les VLANs réservés et le VLAN 1 sont protégés ; la suppression est bloquée pour les ports access encore affectés. |
| Diagnostics | État réel du lien, compteurs CRC/collisions/erreurs entrantes, interprétation de vitesse, test TDR et tableau des paires avec tolérance de longueur. |
| Journal | Actions horodatées, simulations, résultats et échecs. |
| Sauvegardes | `write memory` avec aperçu et confirmation, export complet chiffré, lecture en mémoire d'une sauvegarde chiffrée. |

Les modifications s'appliquent d'abord à la running-config. Elles ne deviennent permanentes qu'après **Sauvegarder · write memory**. En simulation, même cette action ne transmet aucune commande. Les séquences IOS ne sont pas transactionnelles : une erreur interrompt la séquence, mais les commandes déjà acceptées peuvent avoir modifié le switch. Reconnecter et relire l'état avant de reprendre. Aucune relance automatique d'une commande de configuration n'est effectuée.

## Détection : interpréter correctement le résultat

- Le logiciel recherche la MAC Ethernet dans le **switch actif**. Les profils de plusieurs switchs sont enregistrables ; changer de profil si la MAC est absente. Il n'effectue pas de scan de sous-réseau ou d'exploration récursive de topologie.
- Un poste doit émettre du trafic pour que sa MAC soit apprise. Une entrée peut aussi vieillir ou rester momentanément après débranchement. Le lien local est vérifié avant et après la recherche.
- Une MAC sur un trunk ou un agrégat indique un chemin, pas nécessairement le port final. Une correspondance sur un port access reste un candidat : un téléphone ou un petit switch peut être intermédiaire.
- Un routeur entre le poste et le switch empêche généralement de retrouver la MAC du poste dans ce switch.
- Plusieurs correspondances sont signalées comme ambiguës. Une sortie CLI non reconnue produit une erreur, jamais un succès avec un port inventé.
- Le numéro d'une prise murale doit provenir d'un inventaire ou de la description du port ; il n'est pas transmis par Ethernet.
- Les interfaces VPN/virtuelles courantes sont exclues par défaut. Les cas de ponts, NIC teaming ou virtualisation particulière nécessitent une vérification manuelle.

## LLDP / CDP, optionnel

La capture utilise SharpPcap et un pilote **Npcap installé séparément**. Npcap n'est pas distribué avec Switch Pilot. Son absence ou une erreur d'accès produit un message et, si un switch est connecté, une recherche SSH de repli.

L'installation du pilote nécessite normalement des droits administrateur. Selon les options d'installation (notamment `admin_only`), la capture peut aussi être restreinte. Cela ne concerne pas les fonctions SSH. Voir le [guide Npcap](https://npcap.com/guide/npcap-users-guide.html) et ses [conditions de distribution](https://npcap.com/).

L'écoute dure jusqu'à 65 secondes, peut être annulée et ne sauvegarde aucun paquet. Le switch doit annoncer LLDP ou CDP sur ce port. Le nom, le port et le VLAN sont lus s'ils sont annoncés ; le VLAN annoncé peut être natif plutôt qu'access. Les annonces ne sont pas authentifiées et ne suffisent pas à autoriser un TDR. Une annonce expire selon son TTL. Vitesse et duplex ne sont pas inventés à partir de l'annonce.

## Diagnostics et TDR

Un port Fast Ethernet du 2960 Plus négocié à 100 Mb/s est normal. Un port Gigabit à 100 Mb/s appelle une vérification des deux extrémités, de la vitesse forcée et du câble ; cela ne prouve pas à lui seul une panne de câble. Les compteurs sont cumulatifs depuis leur dernière remise à zéro et ne sont jamais effacés par l'application.

Avant un TDR réel, l'application :

1. Vérifie avec la table de routage Windows que le switch est directement joignable en IPv4 sur la carte sélectionnée, sans passerelle. Les chemins routés ou IPv6 restent bloqués pour le TDR dans cette version ; SSH et le contrôle passif restent disponibles.
2. Relit la table MAC et exige une correspondance dynamique unique sur un port access actif.
3. Protège les ports associés aux MAC Ethernet locales et les trunks/agrégats.
4. Bloque un port de type inconnu ou fibre et un chemin de gestion incertain.
5. Affiche les commandes et prévient de l'interruption du lien ; revérifie le contexte après la confirmation.
6. Sonde en lecture la commande TDR, exige un accusé de lancement et attend un résultat neuf (horodatage différent ou passage par un état en cours).

Le support réel dépend du modèle, du port et de l'IOS. Une commande TDR non prise en charge ou non autorisée déclenche un message explicite et un contrôle passif de repli. Sans preuve de fraîcheur, un ancien résultat n'est pas présenté comme un nouveau test réussi. « Non terminé », « inconnu » et « non pris en charge » ne sont pas convertis en « OK ». Les longueurs affichées conservent la tolérance du switch et ne constituent pas une certification du câblage.

## Stockage et secrets

Tout le stockage applicatif persistant est dans `%APPDATA%\SwitchPilot` :

```text
settings.dpapi        Profils, paramètres et clés d'hôte, chiffrés
Logs\actions-*.jsonl  Journal des actions, rétention 30 jours
SmokeTest\           Uniquement si --smoke-test est demandé
```

Les identifiants sont conservés en mémoire pendant leur utilisation. Ils ne sont enregistrés que si la mémorisation est cochée, dans un fichier protégé par **DPAPI CurrentUser**. Les mots de passe SSH/enable, sorties CLI brutes et paquets ne vont pas dans les logs. Les paramètres et noms d'utilisateur enregistrés sont aussi chiffrés. Les journaux contiennent des noms de switchs, ports et VLANs, mais aucun secret d'authentification.

DPAPI protège les fichiers au repos, pas contre un programme exécuté sous le même compte Windows. Copier l'exécutable reste possible ; déplacer les paramètres chiffrés sur un autre compte n'est pas une migration de secrets.

L'export `.spbackup`, à l'emplacement choisi explicitement, contient la configuration complète **chiffrée**, y compris ses éventuels secrets. **Lire une sauvegarde chiffrée** la déchiffre uniquement en mémoire pour le même compte Windows. Ne pas considérer ce format comme une sauvegarde de reprise sur une machine arbitraire ; conserver les moyens de récupération du compte Windows. Aucun export automatique en clair n'est proposé.

## Générer le .exe sur Windows

Prérequis sur la **machine de build uniquement** : SDK .NET 10.0.401 (ou correctif de la même bande), accès NuGet, PowerShell. La cible recommandée est Windows 11 x64. Pour Windows 10, vérifier le support de l'édition/version par .NET 10 avant déploiement ; Windows 7/8 ne sont pas ciblés.

Depuis la racine :

```powershell
.\build\build.ps1
```

Ou directement :

```powershell
dotnet test tests/SwitchPilot.Tests/SwitchPilot.Tests.csproj -c Release
dotnet publish src/SwitchPilot.App/SwitchPilot.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o artifacts/win-x64
```

Résultat : **`artifacts\win-x64\SwitchPilot.exe`**. C'est le seul fichier nécessaire à l'exécution SSH. Le script ajoute la documentation et un SHA-256 à la livraison. Le runtime .NET/WPF est inclus ; certaines DLL natives sont extraites sous `%TEMP%\.net` au lancement. Aucun runtime n'est à installer et aucun fichier de paramètres n'est écrit à côté de l'exécutable.

Changer les métadonnées dans `Directory.Build.props` (`Authors`, `Company`, `Version`) ou utiliser `build.ps1 -Version 1.0.1`. L'icône est `src/SwitchPilot.App/Assets/SwitchPilot.ico`. Le manifeste fixe `asInvoker`. Une publication ARM64 est disponible via `build.ps1 -Runtime win-arm64`, mais doit être validée sur une machine ARM64 et avec un Npcap compatible.

Sous Linux, `PATH=/chemin/du/sdk:$PATH ./build/build.sh` compile et publie le PE Windows grâce à `EnableWindowsTargeting`. Cela **ne valide pas** l'exécution native de WPF ou de DPAPI. Une CI Windows est fournie dans `.github/workflows/windows.yml` ; aucun dépôt distant n'est créé automatiquement.

La publication autonome compressée mesure environ **65 Mio** en x64 (contre 140 Mio initialement). La compression native du bundle .NET réduit la taille distribuée, avec un coût de décompression au démarrage qui reste à mesurer sur Windows natif. Aucun packer UPX n'est utilisé. La taille inclut .NET et WPF. Un exécutable nouveau/non signé peut déclencher SmartScreen ou un faux positif antivirus ; ne pas désactiver l'antivirus. Vérifier les sources/le hash et soumettre le faux positif à l'éditeur concerné. Une signature Authenticode est optionnelle :

```powershell
# Avec le certificat déjà installé dans le magasin Windows et le Windows SDK :
signtool sign /sha1 EMPREINTE_CERTIFICAT /fd SHA256 /tr URL_HORODATAGE_FOURNISSEUR /td SHA256 artifacts/win-x64/SwitchPilot.exe
```

Ne pas stocker de certificat privé ou de mot de passe dans le dépôt. Recalculer le SHA-256 après signature. Republier l'exécutable lors des mises à jour de sécurité .NET, puisque le runtime est embarqué.

## Architecture

```text
src/
  SwitchPilot.Core/              Modèles, contrats, plans immuables
    Cisco/                      Parsing CLI pur
    Discovery/                  Localisation et décodage LLDP/CDP
    Diagnostics/                Règles de sécurité et interprétation
  SwitchPilot.Infrastructure/
    Ssh/                        Session SSH.NET, automate CLI
    Cisco/                      Pilote Cisco IOS, simulateur
    Discovery/                  Cartes, routage, capture Npcap
    Storage/                    DPAPI et journal
  SwitchPilot.App/               WPF / MVVM
    ViewModels/                 Orchestration de l'interface
    Views/                      Connexion
    Services/                   Dialogues et smoke test
    Assets/                     Icône
tests/SwitchPilot.Tests/         Tests et sorties CLI synthétiques
tests/SwitchPilot.Benchmarks/    Mesures CLI et détection reproductibles
build/                          Scripts PowerShell et Bash
docs/                           Rapport de validation
```

Les modules logiques sont regroupés dans trois assemblies pour garder un déploiement et des dépendances simples. Un nouveau constructeur implémente `ISwitchDriver` ; la connexion est derrière `ICliSession`. Les règles de sécurité et les modèles ne dépendent pas de WPF. Pour exposer un nouveau pilote à l'utilisateur, ajouter ensuite son choix à la fenêtre de connexion et à sa fabrique.

La session CLI sérialise les échanges, reconnaît les invites user/privileged/config/interface, traite `enable`, désactive la pagination si possible et gère `--More--` sinon. Elle traite les données reçues progressivement, limite une réponse à 8 Mio de caractères et détecte les erreurs IOS. Elle ferme la session après un délai dépassé, une confirmation interactive inattendue ou une annulation afin de ne pas réutiliser un flux désynchronisé. Les commandes utilisateur sont construites depuis des valeurs validées ; aucune console d'exécution arbitraire n'est exposée.

## Vérifier

```powershell
dotnet test tests/SwitchPilot.Tests/SwitchPilot.Tests.csproj -c Release
# Sur Windows : teste le démarrage WPF, la démo, les onglets et le blocage TDR
.\artifacts\win-x64\SwitchPilot.exe --smoke-test
```

Le smoke test vérifie également le formulaire de connexion, le chiffrement, la simulation, les prévisualisations, les modifications de description, la création/suppression de VLAN et le tableau TDR en démonstration. Il écrit son résultat et cinq captures dans `%APPDATA%\SwitchPilot\SmokeTest`, puis quitte (code 0 en cas de succès). Il ne contacte aucun switch et n'exécute aucune modification réelle. Les trois tests DPAPI sont explicitement ignorés sur Linux.

Pour tester aussi le transport SSH réel contre un émulateur local, installer Python et Paramiko **sur la machine de développement uniquement**, puis exécuter :

```powershell
python -m venv .tools/integration-venv
.tools/integration-venv/Scripts/python -m pip install paramiko==5.0.0
.tools/integration-venv/Scripts/python tests/ssh_emulator.py
```

L'émulateur écoute uniquement sur `127.0.0.1`, avec des identifiants fictifs et une clé générée en mémoire. Il lance les tests puis s'arrête. Les sept tests SSH sont ignorés lorsqu'ils ne sont pas lancés via cet émulateur. Python et Paramiko ne font pas partie de l'application distribuée.

Les tests couvrent les sorties Cisco, formats MAC/interface, pagination fragmentée, modes IOS, erreurs, délais, annulation, injection de commandes, dry-run, échecs partiels, protections TDR et paquets LLDP/CDP tronqués. Les fixtures sont **synthétiques** et ne remplacent pas une recette sur le modèle/IOS réel. Voir [le rapport de validation](docs/VALIDATION.md) pour les résultats réellement obtenus et les essais restant à faire.

## Références

- [Publication single-file .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [Support .NET](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [SSH.NET](https://github.com/sshnet/SSH.NET)
- [SharpPcap](https://github.com/dotpcap/sharppcap)
- [Référence Cisco IOS 15.2(4)E, 2960 Plus](https://www.cisco.com/c/en/us/td/docs/switches/lan/catalyst2960/software/release/15-2_4_e/command/reference/cr_2960/cli3.html)
- [Fiche Cisco 2960 Plus](https://www.cisco.com/c/en/us/products/collateral/switches/catalyst-2960-plus-series-switches/data_sheet_c78-728003.html)

## Optimisations de la version 1.0.1

La surveillance lit la MAC ciblée, puis l’état des interfaces et le mode du port correspondant : trois commandes dans le cas courant après connexion, au lieu de six. Si la MAC est absente, une seule commande suffit. La syntaxe de filtrage non disponible déclenche un repli mémorisé sur la table complète. Le mode du port est relu à chaque détection ; une ancienne valeur ne suffit pas à autoriser un TDR.

Les paramètres chiffrés sont remplacés par une écriture atomique ; une écriture interrompue conserve l’ancien fichier. Les lectures sont bornées (2 Mio de paramètres chiffrés, 16 Mio de sauvegarde). Les fichiers de paramètres indéchiffrables sont conservés pour récupération.

Voir [les mesures avant/après](docs/PERFORMANCE.md) et [le rapport de validation](docs/VALIDATION.md).
