# Switch Pilot 1.0.6

Application Windows de gestion de switchs Cisco IOS et Allied Telesis (AlliedWare Plus), centrée sur le branchement Ethernet du poste. Première cible : Catalyst 2960 Plus / IOS 15.2. Interface WPF, connexions SSH et console série, simulation activée par défaut.

## Démarrer

[Télécharger SwitchPilot.exe](https://github.com/FIlox77250/SwitchPilot/releases/download/v1.0.6/SwitchPilot.exe) · [Release v1.0.6 et archive complète](https://github.com/FIlox77250/SwitchPilot/releases/tag/v1.0.6)

Lancer **`artifacts/win-x64/SwitchPilot.exe`** avec un compte Windows standard. L’exécutable x64 contient .NET et WPF : aucun runtime, Python ou Java à installer. Il refuse de fonctionner avec un jeton administrateur. Seul l’installateur Npcap peut demander une élévation dans un processus séparé.

[Installation](docs/INSTALLATION.md) · [Dépannage](docs/TROUBLESHOOTING.md) · [Changements](CHANGELOG.md) · [Validation et limites](docs/VALIDATION.md)

Au démarrage, l’assistant vérifie Npcap et propose **Installer maintenant** ou **Plus tard**. Sans Npcap, SSH, console et mesures locales restent disponibles. La question est reposée au prochain démarrage. **Paramètres → Dépendances** permet de vérifier ou de réparer l’installation à tout moment.

Le mode **Technicien** affiche le lien, le switch, le port, le VLAN et le verdict câble. Avec Npcap, les annonces LLDP/CDP sont reçues sans identifiants. Pour lire ou modifier le switch, choisir **Connecter un switch**, puis SSH ou console. Les commandes nécessitent les droits IOS correspondants. Le mode **Démonstration** (`--demo`) utilise exclusivement des données fictives.

## Fonctions

| Domaine | Comportement |
| --- | --- |
| Dépendances | Téléchargement HTTPS officiel Npcap 1.89, progression/annulation, vérification Authenticode et éditeur Nmap Software LLC, installateur interactif avec UAC, contrôle après installation et nettoyage. |
| Console | Ports COM et noms Windows, rafraîchissement au branchement, 9600 bauds/8N1 par défaut, cinq vitesses et détection automatique, identifiants optionnels, profils DPAPI. |
| SSH | Empreinte de clé à approuver, connexion moderne, proposition explicite de compatibilité ancien IOS après échec de négociation. Aucun repli Telnet. |
| Détection | Écoute LLDP/CDP continue par carte, expiration TTL/débranchement, recoupement avec la table MAC du switch connecté et signalement des incohérences. |
| Diagnostic | Mesures locales automatiques, compteurs passifs du port identifié, comparaison des extrémités, verdict expliqué, TDR encadré, historique filtrable par switch/port. |
| Ports et VLANs | Filtres, description, VLAN access, trunk, shutdown/no shutdown, création/renommage/suppression de VLAN avec protections. |
| Technicien | Résultat copiable, notifications Windows discrètes, inventaire local des prises et import/export CSV. |
| Sauvegardes | Copie chiffrée automatique de la running-config avant chaque modification réelle, export chiffré, lecture et comparaison en mémoire, `write memory` séparé. |
| Mises à jour | Vérification des releases GitHub au démarrage, proposition de la nouvelle version, téléchargement de l’exécutable portable, vérification de l’empreinte SHA-256, remplacement puis redémarrage. Vérification manuelle dans Paramètres ; une version peut être ignorée. |
| Constructeurs | Dialogue de connexion avec choix Cisco IOS ou Allied Telesis (AlliedWare Plus). Les ports `port1.0.1` sont reconnus, les VLANs et modes access/trunk sont lus depuis `show vlan brief`. |

Npcap n’est pas inclus dans l’exécutable. L’utilisateur termine son installateur lui-même ; aucune option silencieuse `/S` n’est utilisée. Les options demandées sont `/winpcap_mode=yes /admin_only=no /no_kill=yes`. Une installation réservée aux administrateurs laisse la capture désactivée dans Switch Pilot. Les pilotes console FTDI, Prolific, Silicon Labs et Cisco sont signalés avec des indications d’installation ; aucun pilote Cisco n’est téléchargé automatiquement. Voir [le guide et la licence Npcap](https://npcap.com/guide/npcap-users-guide.html).

## Mises à jour

Au lancement, Switch Pilot interroge la dernière release publiée du dépôt GitHub `FIlox77250/SwitchPilot` et propose d’installer une version plus récente. Le téléchargement reprend l’asset portable `SwitchPilot.exe`, l’empreinte SHA-256 fournie par GitHub est vérifiée, puis l’exécutable est remplacé au redémarrage. Les vérifications n’acceptent que les URL GitHub et n’enregistrent aucun secret. La mise à jour automatique suppose un dépôt public, une version de tag supérieure à la version installée et un dossier d’installation accessible en écriture ; sinon l’application renvoie vers la page GitHub. Une version peut être ignorée, et **Paramètres → Vérifier les mises à jour** relance la recherche à tout moment.

## Comprendre la détection

L’écoute est passive et ne conserve aucun paquet. Les annonces LLDP/CDP ne sont pas authentifiées ; leur VLAN peut désigner le VLAN natif. Sans annonce et sans switch connecté, le port reste inconnu. Activer LLDP/CDP sur le switch relève de l’administrateur ; l’application ne le fait jamais automatiquement.

La recherche MAC concerne uniquement le switch actif. Elle démarre à la connexion et suit les événements réseau, avec un intervalle minimal configurable de **60 secondes par défaut**, également utilisé pour le rafraîchissement périodique. Une MAC absente, multiple, apprise sur un trunk ou un agrégat ne confirme pas le port final. Un téléphone, un pont ou un autre switch peut être intermédiaire. Les observations arrivées après un changement de lien sont écartées.

Le numéro d’une prise murale provient de l’inventaire saisi/importé, jamais d’une déduction du câble. Le CSV utilise `Switch,Port,Prise` comme en-tête ; exporter l’inventaire fournit un modèle.

## Verdict câble et TDR

Les mesures locales portent sur le lien, la vitesse, le duplex lorsqu’il est exposé par Windows, les erreurs RX/TX et les transitions de lien. **Câble OK** exige des mesures comparables et du trafic observé sans anomalie. Une information manquante reste **À vérifier**. Des erreurs en progression ou un lien instable conduisent à **Défaut probable**. Le détail conserve les mesures et leur heure. Ce contrôle passif ne certifie pas le câblage.

Une carte Gigabit négociée à 100 Mb/s appelle une vérification des deux extrémités : un port Fast Ethernet de 2960 Plus peut expliquer ce débit normalement. Les compteurs sont cumulatifs et ne sont jamais remis à zéro par l’application.

Le TDR est manuel par défaut et peut interrompre le lien. En SSH, le port du poste, les trunks/agrégats, la fibre, les ports inconnus et les chemins de gestion incertains restent protégés. La preuve du chemin direct utilise le type de route Windows et une correspondance MAC récente. En console, le port Ethernet du poste est autorisé après avertissement, car la gestion passe par le câble série ; les restrictions de type de port restent appliquées.

L’option **TDR automatique après détection (console uniquement)** est désactivée par défaut et exige aussi de désactiver la simulation. Le résultat doit être neuf ; un ancien résultat ou des paires manquantes ne deviennent jamais « OK ». Les refus d’autorisation et l’absence de support IOS sont distingués. Une nouvelle détection suit le test.

## Modifications et données

Chaque modification dispose d’un aperçu et d’une confirmation. En simulation, aucune commande de modification ni sauvegarde préalable n’est envoyée. En mode réel, l’échec de la sauvegarde chiffrée préalable bloque la modification. Les changements sensibles du port de gestion sont bloqués en SSH. Les séquences IOS ne sont pas transactionnelles : après un refus, les commandes déjà acceptées peuvent avoir modifié le switch. Relire l’état avant de reprendre ; aucune relance automatique n’est effectuée.

Les changements restent dans la running-config jusqu’à **Sauvegarder · write memory**. Cette action exige elle aussi une confirmation.

Les données persistantes sont dans `%APPDATA%\SwitchPilot` :

| Fichier ou dossier | Contenu |
| --- | --- |
| `settings.dpapi` | Profils, paramètres et clés d’hôte chiffrés ; secrets enregistrés uniquement si leur mémorisation est cochée. |
| `Backups/*.spbackup` | Configurations complètes chiffrées avant modification. |
| `diagnostics.json` | Jusqu’à 1 000 diagnostics et leurs mesures. |
| `outlets.json` | Inventaire des prises. |
| `Logs/actions-*.jsonl` | Journal des actions, rétention de 30 jours. |
| `SmokeTest/` | Résultats du test graphique demandé par `--smoke-test`. |

DPAPI **CurrentUser** lie les profils et sauvegardes au compte Windows. Ces fichiers ne sont pas une méthode de migration vers un autre compte. La configuration complète peut contenir des secrets, mais reste chiffrée sur disque et n’est déchiffrée qu’en mémoire pour sa lecture/comparaison. Les journaux ne contiennent ni mots de passe, ni sorties CLI brutes, ni paquets. L’inventaire et l’historique contiennent des informations de topologie et des mesures en clair.

## Construire et vérifier

Sur la machine de développement uniquement : SDK .NET **10.0.401**, accès NuGet et PowerShell ou Bash. La cible livrée est Windows x64 ; la validation native recommandée est Windows 11 avec un compte standard.

```powershell
.\build\build.ps1
# Sur Windows, avec un compte standard :
.\artifacts\win-x64\SwitchPilot.exe --smoke-test
```

```bash
./build/build.sh
```

Les scripts exécutent les tests, publient l’exécutable autonome compressé et copient documentation, licences et SHA-256 dans `artifacts/win-x64`. Le runtime .NET/WPF est embarqué ; certaines bibliothèques natives sont extraites dans `%TEMP%\.net` au lancement. Aucune DLL applicative n’est requise à côté de l’EXE. Les dépendances sont verrouillées dans `packages.lock.json`.

Pour les tests de transport, installer Paramiko **sur le poste de développement uniquement**, puis lancer l’émulateur local :

```bash
python -m venv .tools/integration-venv
.tools/integration-venv/bin/python -m pip install paramiko==5.0.0
.tools/integration-venv/bin/python tests/ssh_emulator.py
```

Sous Windows, utiliser `.tools/integration-venv/Scripts/python`. L’émulateur SSH écoute uniquement sur loopback avec des identifiants fictifs ; sous Linux il lance également un pseudo-terminal PTY pour exercer le véritable canal `System.IO.Ports`. Les tests dépendants d’un émulateur ou de Windows sont explicitement ignorés quand leur environnement manque.

La CI `.github/workflows/windows.yml` comprend un smoke test de l’exécutable publié sous un compte standard. Ce parcours charge `System.IO.Ports`, énumère les ports et teste les fenêtres ainsi que DPAPI. **La compilation Linux et les tests PTY ne valident pas le fonctionnement d’un adaptateur COM sous Windows dans le bundle final.** Voir [VALIDATION.md](docs/VALIDATION.md) pour les vérifications effectuées et la recette restante.

L’exécutable livré n’est pas signé. Republier lors des mises à jour de sécurité .NET, puisque le runtime est embarqué. Le script accepte `-Runtime win-arm64` sur PowerShell ; ARM64 n’est ni produit ni validé dans cette livraison.

## Architecture

- `SwitchPilot.Core` : modèles, plans de commandes validés, parsing Cisco, règles de sécurité, annonces/TTL, corrélation MAC, verdict câble et inventaire CSV.
- `SwitchPilot.Infrastructure` : automate CLI commun dans `Terminal`, transports `Ssh` et `Serial`, pilote Cisco, dépendances Windows/Npcap, surveillance réseau, mesures et stockage.
- `SwitchPilot.App` : WPF/MVVM, connexion, dépendances, paramètres, inventaire et orchestration des diagnostics.
- `tests` : fixtures synthétiques, tests unitaires et émulateurs SSH/série ; `build` : scripts de publication et test graphique.

Un autre constructeur peut implémenter `ISwitchDriver` ; les transports partagent `ICliSession` et `ITerminalChannel`. La conversation sérialise les échanges, traite les prompts/pagination/syslogs, borne les réponses et ferme une session désynchronisée après délai, annulation ou confirmation inattendue.

Les [mesures de performance historiques 1.0.1](docs/PERFORMANCE.md) restent disponibles ; elles ne sont pas des mesures de la 1.0.6.
