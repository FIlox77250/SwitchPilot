# Validation de Switch Pilot 1.0.2

État au 30 septembre 2026. Ce rapport distingue les tests exécutés des parcours seulement préparés et des essais matériels restants. Les compteurs des rapports TRX sont consignés dans [test-results.json](test-results.json).

## Vérifications exécutées

Environnement : Linux Ubuntu, SDK .NET 10.0.401, build Release, runtime cible .NET/WPF 10.0.12.

- Compilation de la solution avec `dotnet build SwitchPilot.sln -c Release -warnaserror` : **zéro erreur, zéro avertissement**.
- Suite avec émulateurs : **156 tests réussis, 6 ignorés, 0 échec, 162 au total**. Les six tests ignorés nécessitent Windows (DPAPI et Authenticode).
- Suite sans émulateurs : **146 tests réussis, 16 ignorés, 0 échec, 162 au total**. Les dix exclusions supplémentaires concernent huit tests SSH et deux tests série PTY.
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
| Route directe | Fonction de décision vérifiée avec type Windows DIRECT = 3, prochain saut non nul et interface différente. L’appel natif Windows reste à tester. |
| SSH ancien IOS | Serveur loopback limité à AES-CBC : échec moderne, classification de négociation puis connexion avec compatibilité explicite. L’émulateur ne couvre pas toutes les combinaisons SHA-1 des IOS anciens. |
| Fréquence de détection | Intervalle minimal de 60 s, regroupement des événements, déclenchement et rafraîchissement périodique. |
| Paramètres | Conservation du fichier illisible et déduplication des copies de récupération préparées dans les tests Windows ; suppression de la fonction inutilisée `RedactConfig`. |
| TDR | Distinction autorisation/support, gardes selon le transport, fraîcheur des résultats, paires inconnues/manquantes, accès au port du poste en console et blocage des trunks. |
| Dépendances | Plateforme simulée : Npcap présent/absent, réseau indisponible, annulation, refus de signature, relance au démarrage suivant après report, nettoyage et activation après installation. |
| Console | Automate partagé : sans login, authentification, RETURN, réponse `no` seulement au dialogue initial précis, syslogs, mode configuration, pagination, délais, annulation et vitesses. |
| Transport série réel | Deux consoles PTY Linux utilisant le véritable `SerialPort` : initialisation/commandes et lecture fragmentée avec syslog. Aucun adaptateur USB/COM physique utilisé. |
| Détection continue | TTL, retrait TTL zéro, annonce périmée, changement de génération, plusieurs cartes, annulation/reprise, erreur de capture et corrélation LLDP/CDP–MAC. La capture injectée ne charge pas Npcap. |
| Verdict câble | Absence de mesures/duplex/compteurs, reset de compteurs, trafic observé, erreurs croissantes, Gigabit à 100, Fast Ethernet normal, oscillations et mesures switch incomplètes. |
| Inventaire/historique | CSV avec guillemets, validation des ports, doublons, caractères de contrôle/formules, persistance et bornes de l’historique. |
| Sauvegarde préalable | Ordre lecture–sauvegarde–écriture, échec de sauvegarde bloquant, absence de sauvegarde en simulation. Test DPAPI spécifique préparé pour Windows. |

Les tests antérieurs restent présents : parsing Cisco, commandes validées, délais et limites de taille, absence de relance après échec, clé SSH et mots de passe erronés, annulation, sérialisation des échanges, LLDP/CDP tronqués et paquets aléatoires.

Le message Paramiko « no acceptable ciphers » dans la sortie de l’intégration est attendu : il correspond au premier essai moderne volontairement refusé par le serveur limité à CBC. Le test vérifie ensuite le succès du mode compatible.

## Windows : vérifications encore nécessaires

**Aucun lancement de la 1.0.2 sur Windows natif n’a été effectué dans cet environnement de développement.** Le smoke test 1.0.2 est écrit et compilé, mais n’a pas été exécuté localement. Les résultats Windows produits après publication sont consultables dans [GitHub Actions](https://github.com/FIlox77250/SwitchPilot/actions/workflows/windows.yml) pour le commit concerné ; ils complètent ce rapport local. Les anciennes captures sous `screenshots/` concernent la 1.0.1 sous Wine ; elles ne valident ni l’interface ni le bundle de la 1.0.2.

La CI `.github/workflows/windows.yml` lance les tests puis le smoke test de l’EXE publié. Le script `build/smoke-standard-user.ps1` utilise le compte courant s’il est standard, sinon crée un compte standard temporaire et une tâche limitée pour le runner. Ce parcours CI doit encore être vérifié sur le runner Windows. Le garde interdisant l’exécution élevée de l’application reste actif pendant ces essais.

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

L’EXE est non signé. ARM64 n’a pas été produit ou testé. Les [mesures de performance](PERFORMANCE.md) sont historiques (1.0.1), sans mesure du démarrage, de la consommation WMI/Npcap ou du débit CLI de la 1.0.2 sur matériel.
