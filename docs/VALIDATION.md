# Validation de Switch Pilot 1.1.1

État au 6 octobre 2026.

## Version 1.1.1 : vérifications locales

- Compilation de toute la solution : **zéro erreur, zéro avertissement**.
- Suite complète : **532 tests réussis, 11 ignorés, 0 échec, 543 au total**, dont 17 nouveaux tests du lecteur Markdown (notes de version 1.1.0 réelles, liens non web refusés, entrées mal formées).
- Rendu de la fenêtre de mise à jour vérifié par capture avec les notes de la version 1.1.0.

## Version 1.1.0 : vérifications locales

Environnement : Windows 11 Pro, SDK .NET 10.0.401.

- Compilation de toute la solution : **zéro erreur, zéro avertissement**.
- Suite complète : **515 tests réussis, 11 ignorés, 0 échec, 526 au total**. Les 306 nouveaux tests couvrent :
  - les parseurs Huawei, Junos, RouterOS, EdgeSwitch, NX-OS, Arista, Dell OS6/OS9/OS10 et UniFi (shell et JSON) ;
  - le moteur TextFSM ;
  - les grammaires de ports et les dialectes de configuration ;
  - la factory et la détection automatique ;
  - le pipeline d'écriture de chaque pilote : simulation, sauvegarde obligatoire, garde de sécurité, commit Huawei à deux étapes, commit / rollback Junos, récupération EdgeSwitch, lecture seule UniFi SSH ;
  - le pilote du contrôleur UniFi face à un contrôleur HTTP simulé : classique et UniFi OS, CSRF, reconnexion, corps `port_overrides`, `networkconf`, empreinte de certificat.
- Cinq défauts de parsing trouvés par ces tests ont été corrigés (voir le [CHANGELOG](../CHANGELOG.md)).

**Limite importante** : les nouvelles plateformes (NX-OS, EOS, Dell, Huawei, Junos, RouterOS, EdgeSwitch, UniFi) sont validées uniquement sur des sorties de référence (fixtures) construites à partir de la documentation des constructeurs. Aucun de ces équipements n'a été connecté. Elles sont marquées **expérimentales** dans l'application. Avant tout usage en production, valider sur un port de laboratoire, en simulation puis en écriture, la lecture des ports/VLANs, le changement de VLAN access et la sauvegarde ou le commit.

## Version 1.0.9

État au 1er octobre 2026. Ce rapport distingue les tests exécutés des parcours seulement préparés et des essais matériels restants. Les compteurs des rapports TRX sont consignés dans [test-results.json](test-results.json).

## Correctifs 1.0.9 : vérifications locales

- Nouvelle famille AT-S95 (AT-8000GS) : parseurs dédiés `show interfaces status`, `show vlan`, `show interfaces switchport`, `show bridge address-table`, `show version` / `show system`, avec normalisation des ports `g1`, `1/g1` et `ch1`.
- Détection automatique à trois familles (`Cisco`, `AlliedWare Plus`, `AT-S95`) vérifiée sur les bannières réelles (`SW version …` et table Unit/SW/Boot/HW) ; une famille AT-S95 n'est plus confondue avec AlliedWare Plus.
- Pager Cisco-Small-Business (`More: <space>,  Quit: q…`) couvert par un test de dialogue qui vérifie l'avance de page puis la synchronisation de la commande suivante ; le pager IOS `--More--` reste couvert par les tests existants.
- La préparation de session envoie `terminal datadump` ; son refus sur les autres familles est sans effet.
- Suite locale (Linux, émulateur SSH arrêté) : **202 tests réussis, 18 ignorés, 0 échec, 220 au total**. Les tests ignorés exigent Windows (DPAPI, Authenticode) ou l'émulateur local.
- Compilation Release complète et exécutable autonome 1.0.9 produits dans `artifacts/win-x64`.

La console RS-232 d'un AT-8000GS physique, sa vitesse réelle (115 200 bauds par défaut, réglable) et la validation des écritures sur cette famille restent à confirmer par l'utilisateur.

## Correctifs 1.0.8 : vérifications locales

- Compilation de toute la solution en Release avec `-warnaserror` : **zéro erreur, zéro avertissement**.
- Exécutable autonome Windows x64 1.0.8 produit dans `artifacts/win-x64`, avec runtime embarqué.
- Régression série reproduite avant correction : `show interfaces switchport` sur un pseudo-terminal avec le véritable `System.IO.Ports` échouait à 25 secondes malgré une réponse continue. Après correction, les 26 ports sont lus en 26 secondes et la commande suivante reste synchronisée.
- Suite complète avec émulateurs SSH et série : **203 réussis, 7 ignorés, 0 échec, 210 au total**. Les sept exclusions exigent Windows (DPAPI et Authenticode).
- Six tests de dialogue couvrent la sortie lente, la commande suivante, l'inactivité, l'annulation, la relance au démarrage et l'absence de touche Entrée injectée pendant une commande.
- L'erreur des dépendances a été reproduite dans WPF sous Wine avec les fenêtres de l'application : liaison `TwoWay` impossible sur la propriété `Progress` en lecture seule. Le test utilise un programme hôte séparé ; le garde contre le lancement administrateur de l'application reste inchangé.

- Après correction, le contrôle WPF sous Wine réussit : ouverture des dépendances depuis les paramètres modaux et affichage de la fenêtre de mise à jour avec nouvelle recherche après une version mémorisée. Le test emploie une source de versions fictive ; aucun téléchargement ni installateur réel n’est lancé. Le parcours complet de démarrage de l’exécutable sous Windows natif reste à valider.

Ces essais n'utilisent ni switch physique, ni adaptateur USB/COM Windows. La validation du cas exact signalé nécessite encore le modèle, la vitesse et le journal de la console, puis un essai du nouvel exécutable sur cet équipement.

## Historique des vérifications 1.0.7

Les résultats ci-dessous concernent la version précédente et ne constituent pas des validations supplémentaires de la 1.0.9.

## Vérifications exécutées

Environnement : Linux Ubuntu, SDK .NET 10.0.401, build Release, runtime cible .NET/WPF 10.0.12.

- Compilation de la solution avec `dotnet build SwitchPilot.sln -c Release -warnaserror` : **zéro erreur, zéro avertissement**.
- Suite locale (Linux, émulateur SSH arrêté) : **184 tests réussis, 17 ignorés, 0 échec, 201 au total**. Les tests ignorés nécessitent Windows (DPAPI, Authenticode) ou l’émulateur SSH local ; aucun n’échoue.
- Suite avec l’émulateur SSH local lancé : les tests d’intégration SSH complètent la suite précédente. Les exclusions restantes concernent Windows et le PTY série selon l’environnement.
- Publication autonome `win-x64`, single-file compressé, sans trimming. Le fichier produit est un exécutable PE32+ GUI x64 ; le runtime et les dépendances sont embarqués. Les fichiers de documentation accompagnent l’EXE mais ne sont pas nécessaires à son lancement.
- Analyse NuGet des dépendances de production, transitives comprises : aucune vulnérabilité connue signalée par la source NuGet lors de cette vérification. Ce résultat ne constitue pas un audit de sécurité exhaustif.

Commandes reproductibles :

```bash
dotnet build SwitchPilot.sln -c Release -warnaserror
.tools/integration-venv/bin/python tests/ssh_emulator.py
./build/build.sh
dotnet list src/SwitchPilot.App/SwitchPilot.App.csproj package --include-transitive --vulnerable
```

Le SDK doit être accessible dans `PATH`. Les émulateurs et leurs identifiants sont locaux et fictifs ; aucun équipement de production n’a reçu de commande.

## Couverture des ajouts et corrections

| Domaine | Vérifications exécutées |
| --- | --- |
| Refus IOS | Fixtures avec et sans `%`, `Command rejected`, refus VTP/autorisation ; interruption de séquence et absence de faux succès. |
| Protection des écritures | Blocage shutdown/VLAN/trunk sur un chemin SSH protégé ou incertain ; exemption console ; contexte périmé et invalidé pendant la sauvegarde ; simulation sans écriture. |
| Route directe | Fonction de décision vérifiée avec type Windows DIRECT = 3 et index d’interface de route égal à celui de la carte ; les réponses DNS IPv6 sont ignorées. L’appel natif Windows reste à tester. |
| SSH ancien IOS | Serveur loopback limité à AES-CBC : échec moderne, classification de négociation puis connexion avec compatibilité explicite. L’émulateur ne couvre pas toutes les combinaisons SHA-1 des IOS anciens. |
| Fréquence de détection | Intervalle minimal de 60 s, regroupement des événements, déclenchement et rafraîchissement périodique. |
| Paramètres | Conservation du fichier illisible et déduplication des copies de récupération préparées dans les tests Windows ; suppression de la fonction inutilisée `RedactConfig`. |
| TDR | Distinction autorisation/support, gardes selon le transport, fraîcheur des résultats, paires inconnues/manquantes, accès au port du poste en console et blocage des trunks. |
| Dépendances | Plateforme simulée : Npcap présent/absent, réseau indisponible, annulation, refus de signature, relance au démarrage suivant après report, nettoyage et activation après installation. |
| Console | Automate partagé : sans login, authentification, RETURN, réponse `no` seulement au dialogue initial précis, syslogs, mode configuration, pagination, délais, annulation et vitesses. |
| Transport série réel | Un pseudo-terminal PTY Linux utilisant le véritable `SerialPort`, exercé par deux tests : initialisation/commandes et lecture fragmentée avec syslog. Aucun adaptateur USB/COM physique utilisé. |
| Détection continue | TTL, retrait TTL zéro, annonce périmée, changement de génération, plusieurs cartes, annulation/reprise, erreur de capture et corrélation LLDP/CDP–MAC. La capture injectée ne charge pas Npcap. |
| Verdict câble | Absence de mesures/duplex/compteurs, reset de compteurs, trafic observé, erreurs croissantes, Gigabit à 100, Fast Ethernet normal, oscillations et mesures switch incomplètes. |
| Inventaire/historique | CSV avec guillemets, validation des ports, doublons, caractères de contrôle/formules, persistance et bornes de l’historique. |
| Sauvegarde préalable | Ordre lecture–sauvegarde–écriture, échec de sauvegarde bloquant, absence de sauvegarde en simulation. Test DPAPI spécifique préparé pour Windows. |
| Allied Telesis | Analyse de `show interface status` (`port1.0.1`), `show vlan brief` (membres `(u)`/`(t)`, états variables) et table MAC ; modes access/trunk, compteurs en ligne, écriture access pré-visualisée, blocage du TDR non pris en charge. |
| Mise à jour | Analyse du JSON de release GitHub, comparaison de versions à 3 et 4 composants, refus des brouillons et préversions, hôtes de téléchargement autorisés, script de remplacement et refus des chemins non sûrs. |

Les tests antérieurs restent présents : parsing Cisco, commandes validées, délais et limites de taille, absence de relance après échec, clé SSH et mots de passe erronés, annulation, sérialisation des échanges, LLDP/CDP tronqués et paquets aléatoires.

Le message Paramiko « no acceptable ciphers » dans la sortie de l’intégration est attendu : il correspond au premier essai moderne volontairement refusé par le serveur limité à CBC. Le test vérifie ensuite le succès du mode compatible.

## Windows : vérifications encore nécessaires

**Aucun lancement de la 1.0.9 sur Windows natif n’a été effectué dans cet environnement de développement.** Le smoke test 1.0.9 est écrit et compilé, mais n’a pas été exécuté localement. Les résultats Windows produits après publication sont consultables dans [GitHub Actions](https://github.com/FIlox77250/SwitchPilot/actions/workflows/windows.yml) pour le commit concerné ; ils complètent ce rapport local. Les anciennes captures sous `screenshots/` concernent la 1.0.1 sous Wine ; elles ne valident ni l’interface ni le bundle de la 1.0.9.

La CI `.github/workflows/windows.yml` lance les tests puis le smoke test de l’EXE publié. Le script `build/smoke-standard-user.ps1` utilise le compte courant s’il est standard, sinon crée un compte standard temporaire et lance directement le processus avec ce compte et son profil chargé pour le runner. Ce parcours CI doit encore être vérifié sur le runner Windows. Le garde interdisant l’exécution élevée de l’application reste actif pendant ces essais.

Recette à effectuer sur Windows 11 x64 :

1. Double-clic sur l’EXE final sans runtime préinstallé, sous un compte standard ; vérifier aussi le refus du lancement élevé et le rendu à 100/125/150 %.
2. Exécuter `SwitchPilot.exe --smoke-test`. Il charge `System.IO.Ports`, énumère les ports, vérifie les profils DPAPI SSH/série, les fenêtres paramètres/dépendances/inventaire, les onglets, les aperçus/simulations et le TDR fictif. Résultat dans `%APPDATA%\SwitchPilot\SmokeTest\result.txt`.
3. Tester un vrai câble COM avec l’EXE single-file : nom convivial, branchement/débranchement, port occupé, chaque vitesse et auto-détection. **La présence de l’assembly Windows dans la publication et les tests PTY ne remplacent pas cet essai.**
4. Valider DPAPI avec le même compte puis avec un autre compte, la migration des profils 1.0.1, la déduplication `.unreadable`, les sauvegardes et la comparaison en mémoire.
5. Tester Npcap absent, compatible, incomplet, arrêté, ancien et `admin_only`. Contrôler une signature officielle valide, une signature invalide et un autre éditeur. La vérification native `WinVerifyTrust` n’a pas été exercée ici.
6. Tester téléchargement via proxy, perte réseau, annulation du téléchargement, refus UAC, annulation de l’installateur, redémarrage demandé, nettoyage et activation de la capture sans relancer l’application. Confirmer que seul l’installateur reçoit l’élévation.
7. Recevoir de vraies annonces LLDP/CDP sur plusieurs cartes, vérifier TTL et débranchement, les informations WMI de vitesse/duplex, les notifications et le presse-papiers.
8. Vérifier `GetBestRoute` sur chemin IPv4 direct/routé et IPv6, puis la protection des actions sensibles après changement de câble pendant une confirmation.

## Recette sur Catalyst et câblage réels

Aucun switch ni câble console physique n’était disponible. Les fixtures sont synthétiques et les émulateurs ne sont pas IOS. Relever le modèle, la version IOS et la version du pilote de carte réseau pour chaque essai.

- SSH et console : login, enable, algorithmes anciens, prompts, syslogs intercalés, terminal resté en configuration et dialogue initial. Contrôler les sorties `show` et les refus d’autorisation réels.
- Branchement : port connu, déplacement, MAC absente/multiple, téléphone intermédiaire, trunk/agrégat, absence d’annonce et contradiction entre annonce et table MAC. Contrôler le délai minimal entre lectures automatiques.
- Mesures : port Fast Ethernet à 100, port Gigabit, câble volontairement dégradé, erreurs croissantes, absence de trafic et duplex non fourni par le pilote. Vérifier les preuves du verdict et l’historique par port.
- Écritures sur ports de laboratoire : simulation, sauvegarde préalable déchiffrable, description, VLAN access, trunk, shutdown, création/suppression VLAN et `write memory` distinct. Vérifier le blocage du port de gestion en SSH.
- TDR : blocage du port du poste en SSH, test autorisé en console après avertissement, interruption attendue, résultats neufs, redétection, option automatique désactivée par défaut et absence de boucle après sa propre interruption. Tester refus d’autorisation et modèle non compatible.
- Inventaire et comparaison : aller-retour CSV, remplacement explicite des doublons, association à une prise et absence de secrets en clair dans les paramètres, sauvegardes et journal.

## Limites de livraison

Un seul switch actif à la fois ; aucune exploration récursive. Les annonces ne prouvent pas le port final. Les particularités de teaming/bridging nécessitent une recette dédiée. Un TDR interrompu côté client peut continuer côté switch ; les commandes IOS déjà acceptées ne sont pas annulées automatiquement. Les sauvegardes DPAPI dépendent du compte Windows.

L’EXE est non signé. ARM64 n’a pas été produit ou testé. Les [mesures de performance](PERFORMANCE.md) sont historiques (1.0.1), sans mesure du démarrage, de la consommation WMI/Npcap ou du débit CLI de la 1.0.9 sur matériel.
