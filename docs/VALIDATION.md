# Validation de Switch Pilot 1.0.1

État au 30 septembre 2026. Ce rapport distingue les tests réellement exécutés des validations matérielles restant à effectuer.

Les compteurs issus des rapports TRX sont archivés dans [test-results.json](test-results.json).

## Exécuté dans l'environnement de développement

- Compilation Release de la solution, sans erreur ni avertissement (`-warnaserror`), avec SDK .NET 10.0.401 sous Ubuntu 24.04.
- Publication autonome Windows x64, runtime .NET / WPF 10.0.12, format PE32+ GUI. Le fichier final n'a besoin d'aucune DLL applicative à côté de lui.
- **95 tests réussis, 3 tests ignorés** lors du passage avec l'émulateur SSH local. Les trois tests ignorés sont explicitement réservés à DPAPI sous Windows natif. Sans l’émulateur SSH, sept tests supplémentaires sont ignorés (88 réussis, 10 ignorés).
- Parmi ces tests : vrais échanges SSH.NET avec un serveur Paramiko éphémère sur loopback, vérification de clé, refus d'une clé non approuvée, refus d'un mauvais mot de passe, `enable`, commandes en mode configuration, recherche MAC, compteurs et export en mémoire.
- Tests des sorties CLI et des commandes : noms Catalyst 2960+, descriptions longues, interfaces abrégées et longues, MAC, VLANs, TDR, pagination fragmentée, modes user/privileged/config/interface, refus IOS, attente bornée, annulation, injection, dry-run sans contact avec le switch, arrêt après échec partiel et absence de sauvegarde implicite.
- Régressions ajoutées : annulation avant toute émission, fermeture de session après confirmation imprévue, réponses trop volumineuses, sérialisation concurrente SSH, refus du mauvais secret enable, absence de repli après refus d’autorisation, fraîcheur du mode access/trunk et des résultats TDR, âge du contexte de sécurité, paires manquantes/inconnues/dupliquées, rotation du journal et panne disque.
- Tests LLDP/CDP : informations annoncées, tags VLAN, troncatures et 5 000 paquets aléatoires sans lecture hors limites.
- Analyse NuGet des dépendances de production, y compris transitives : aucune vulnérabilité connue signalée par la source NuGet au moment de l'analyse.

## Vérification graphique sous Wine

Un smoke test automatisé a été exécuté avec Wine 9 dans un préfixe isolé. Il a vérifié : démarrage WPF, détection fictive `Switch-A / Fa0/14 / VLAN 10`, 26 ports, rendu des cinq onglets, compteurs passifs et blocage TDR du port du poste. Le parcours étendu vérifie aussi le formulaire de connexion, les prévisualisations CLI, la simulation sans modification, une description appliquée, la création/suppression d’un VLAN et les quatre paires TDR en démonstration. Le chiffrement/déchiffrement des profils et sauvegardes et l’oubli des identifiants ont été vérifiés avec l’implémentation DPAPI de Wine.

Captures réelles de ce test, avec police de substitution **DejaVu Sans** (Segoe UI n'est pas disponible dans cet environnement) :

- [Mon branchement](screenshots/screen-0.png)
- [Ports](screenshots/screen-1.png)
- [VLANs](screenshots/screen-2.png)
- [Diagnostics](screenshots/screen-3.png)
- [Journal](screenshots/screen-4.png)
- [Résultat du smoke test](screenshots/smoke-test.txt)
- [Progression des scénarios de modification](screenshots/smoke-progress.txt)

Le parcours complet a réussi sous Wine 9 avec une publication de validation utilisant `IncludeAllContentForSelfExtract=true`, qui extrait aussi les assemblies managées. **Ce réglage de validation n’est pas imposé au build Windows livré.** Le bundle non compressé initial échouait au chargement de CoreCLR (`0x8007046C`). Le bundle compressé 1.0.1 démarre et exécute plusieurs parcours, mais l’automatisation des fenêtres modales reste irrégulière sous Wine ; son smoke test complet n’a pas été validé. L’exécution de l’exécutable final sur Windows natif reste à confirmer. Une temporisation dans le test laisse les fenêtres natives se fermer avant d’ouvrir la suivante ; elle ne change pas le parcours utilisateur normal.

La variable `SWITCHPILOT_TEST_FONT` permet uniquement en mode `--smoke-test` de remplacer la police pour cet environnement de validation. Elle n'affecte pas un lancement utilisateur normal. La géométrie des captures dépend aussi du gestionnaire de fenêtres de l'hôte Linux ; elle n'est pas une capture de Windows 11.

## À valider sur Windows natif

La CI Windows fournie exécute automatiquement les tests, le build et le smoke test. Elle n'a pas été déclenchée depuis cette session : aucun dépôt distant ni runner Windows natif n'était disponible.

1. Double-clic sur l'exécutable final avec un compte standard, sur Windows 11 x64 sans runtime .NET préinstallé. Vérifier le rendu à 100 %, 125 % et 150 %, les boîtes de dialogue et l'accès clavier.
2. Profils et sauvegardes DPAPI : restauration avec le même compte, refus avec un autre compte ; lecture du journal et rotation des fichiers.
3. Capture Npcap absente, installée et restreinte à l'administrateur ; réception de véritables annonces LLDP et CDP. Aucune capture réelle n'a été effectuée ici.
4. Vérification de la sélection de carte et de la route Windows `GetBestRoute` : TDR refusé en routage, en IPv6, sur un trunk et sur le port de connexion.

## À valider sur un Catalyst réel

Aucun switch ni identifiant réel n'a été fourni. **Aucune commande n'a été envoyée à un équipement de production.** Les fixtures sont synthétiques et l'émulateur n'est pas IOS.

Sur un switch de laboratoire, noter la référence exacte et la version complète d'IOS :

1. Vérifier les droits et algorithmes SSH, la clé d'hôte, les sorties `show version`, `show interfaces status`, `show interfaces description`, `show interfaces switchport`, `show vlan brief` et `show mac address-table`.
2. Brancher le poste sur un port connu ; confronter le résultat au brassage. Vérifier les cas MAC absente, déplacée, multiple, uplink, téléphone intermédiaire et interfaces multiples.
3. Simuler chaque modification et vérifier l'absence d'effet. Appliquer ensuite sur un port de laboratoire : description, VLAN access, trunk, shutdown/no shutdown, création et suppression de VLAN. Contrôler la running-config puis confirmer séparément `write memory` et comparer la startup-config.
4. Vérifier les limites du modèle et du port avant TDR. Confirmer le blocage du port du poste. Tester un autre câble de laboratoire, contrôler la fraîcheur des résultats, les états par paire et le repli passif sur un port non compatible.
5. Exporter la configuration chiffrée, la relire dans l'application et vérifier qu'aucun secret n'apparaît en clair dans les fichiers de paramètres ou le journal.

## Limites délibérées

- Un seul switch actif à la fois ; plusieurs profils enregistrables, sans découverte récursive de topologie.
- Les interfaces physiques sont identifiées par les API Windows et un filtre des adaptateurs virtuels courants. Les configurations particulières de teaming/bridging nécessitent une recette dédiée.
- Le TDR est volontairement bloqué en l'absence de preuve suffisante du chemin direct IPv4 et du port local. Le blocage n'est pas contournable dans l'interface.
- Un TDR interrompu côté client peut continuer sur le switch. Les résultats manquants ou sans fraîcheur démontrée ne sont pas déclarés sains.
- Les compteurs CRC/collisions sont cumulatifs ; l'application ne remet aucun compteur à zéro.
- Export chiffré lié au compte Windows, sans export automatique de secrets en clair. L'exécutable est portable ; les secrets DPAPI ne sont pas portables entre comptes.
- Exécutable non signé. ARM64 prévu par le script, mais non produit ni testé dans cette livraison x64.

## Performances

Les mesures synthétiques avant/après et leurs limites figurent dans [PERFORMANCE.md](PERFORMANCE.md). Elles ne constituent pas un essai de charge sur un Catalyst réel.
