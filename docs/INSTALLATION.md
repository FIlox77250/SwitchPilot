# Installer Switch Pilot 1.0.9

Switch Pilot est portable pour Windows x64. Le runtime .NET/WPF est inclus : aucune installation de runtime n’est nécessaire.

## Télécharger et lancer

1. Télécharger `SwitchPilot.exe` et `SHA256SUMS.txt` depuis la [release officielle v1.0.9](https://github.com/FIlox77250/SwitchPilot/releases/tag/v1.0.9). L’archive `SwitchPilot-1.0.9-win-x64.zip` ajoute la documentation et les licences.
2. Extraire l’archive, si nécessaire, dans un dossier utilisateur, par exemple `Documents\SwitchPilot`.
3. Vérifier le téléchargement avec `Get-FileHash .\SwitchPilot.exe -Algorithm SHA256` et comparer au fichier de sommes.
4. Lancer `SwitchPilot.exe` par double-clic, sans « Exécuter en tant qu’administrateur ».

La cible de recette est Windows 11 x64. L’exécutable est non signé ; les règles de confiance de Windows et de votre organisation restent applicables.

## Dépendance facultative Npcap

Au premier lancement sans Npcap utilisable, l’application propose **Installer maintenant** ou **Plus tard**. Npcap permet de détecter automatiquement le port du switch sans identifiants.

Avec **Installer maintenant**, Switch Pilot télécharge l’installateur officiel depuis `npcap.com`, vérifie sa signature et son éditeur, puis ouvre l’installateur avec une demande UAC. L’application reste sous le compte utilisateur courant. Accepter la licence Npcap et terminer ses écrans :

- Laisser **WinPcap API-compatible Mode** coché.
- Laisser **Restrict Npcap driver's Access to Administrators only** décoché pour capturer avec un compte standard.

La présence du pilote, des bibliothèques et du service est revérifiée après l’installation. Si Npcap est prêt, la surveillance démarre sans relancer Switch Pilot. L’installateur temporaire est supprimé après sa fermeture. **Plus tard** permet de continuer ; la proposition revient au prochain démarrage tant que Npcap manque.

**Paramètres → Dépendances** permet de consulter l’état de Npcap et des adaptateurs console, d’installer ou de vérifier à nouveau. Npcap n’est pas inclus dans l’exécutable ; sa licence reste distincte. Consulter les [conditions officielles](https://npcap.com/#download), notamment pour un déploiement sur plusieurs postes.

## Utiliser le mode Technicien

Brancher un câble Ethernet et consulter l’accueil. L’application suit le lien et ses mesures localement ; avec Npcap, elle écoute les annonces LLDP/CDP sur les cartes Ethernet physiques. Le switch doit émettre ces annonces.

Le port, le VLAN annoncé, la source et l’heure apparaissent dès réception. Les données périmées sont retirées. **Copier** prépare un résultat pour un ticket. **Inventaire des prises** permet d’associer le port détecté à un nom de prise, puis d’importer ou d’exporter un CSV.

## Connexion SSH

1. Choisir **Connecter un switch**, puis le **type de switch** (« Détection automatique » par défaut) et le mode **SSH**.
2. Saisir l’adresse, le port, l’utilisateur, le mot de passe et éventuellement le secret `enable`.
3. Vérifier l’empreinte SSH. Une clé nouvelle ou changée demande une confirmation.
4. En cas d’incompatibilité de négociation, accepter la proposition « ancien IOS » uniquement pour l’équipement concerné.

La table MAC du switch connecté complète les annonces et le contrôle passif démarre sur un port identifié sans contradiction. Une route ou une topologie incertaine ne permet pas d’autoriser une modification dangereuse.

## Connexion console

1. Installer le pilote du câble s’il manque, selon les indications de **Dépendances**.
2. Brancher le câble console et choisir **Connecter un switch → Console série**.
3. Choisir le port COM nommé. Les valeurs initiales sont **9600 bauds, 8N1, sans contrôle de flux**.
4. Adapter la vitesse ou cocher la détection automatique. Renseigner les identifiants uniquement si le switch les demande.

L’indication **Connexion console locale** rappelle l’absence de vérification de clé SSH. Le pilote USB Console Cisco se récupère manuellement avec un compte Cisco sur le site officiel.

## Modifications et diagnostics

La simulation est activée à chaque lancement. Pour modifier réellement un switch, la désactiver puis confirmer l’aperçu des commandes. Une sauvegarde de la running-config est automatiquement chiffrée avant l’écriture. `write memory` reste une action distincte.

Le TDR est manuel par défaut et peut couper le lien. En SSH, le port du poste et les chemins protégés sont bloqués. En console, le port du poste devient testable après avertissement. L’option automatique se trouve dans Paramètres, nécessite une confirmation d’activation et reste sans effet tant que la simulation est active.

Consulter l’historique dans **Diagnostics**, ou **Comparer à une sauvegarde** pour afficher côte à côte une sauvegarde déchiffrée en mémoire et la configuration actuelle.

Les paramètres restent dans `%APPDATA%\SwitchPilot`. Les identifiants mémorisés et les configurations sont protégés par DPAPI pour ce compte Windows.

## Mises à jour

Au lancement, Switch Pilot interroge les releases GitHub du projet et propose d’installer une version plus récente. Le téléchargement reprend l’exécutable portable, son empreinte SHA-256 est vérifiée, puis il est remplacé au redémarrage. **Paramètres → Vérifier les mises à jour** relance la recherche ; « Ne plus proposer cette version » évite une relance pour une version donnée. La mise à jour automatique suppose un dossier d’installation accessible en écriture ; sinon l’application renvoie vers la page GitHub.

Pour les incidents et les limites de validation : [annexe dépannage](TROUBLESHOOTING.md) et [rapport de validation](VALIDATION.md).
