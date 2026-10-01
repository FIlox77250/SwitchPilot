# Changelog

## 1.0.9 — 1er octobre 2026

Correctifs pour les anciennes séries Allied Telesis (AT-8000GS, logiciel AT-S95) : la console COM restait bloquée quel que soit le débit, alors que PuTTY fonctionnait.

- **Pager Cisco-Small-Business** : la ligne `More: <space>,  Quit: q or CTRL+Z…` est désormais reconnue en plus du `--More--` IOS, avec les variantes `Press any key` et `(q)uit`. Toute sortie longue (par exemple `show interfaces status` sur 24 ports) figeait la session jusqu'au timeout faute de touche d'avance.
- **`terminal datadump`** : la préparation de session tente aussi la commande qui coupe la pagination sur cette famille ; refusée silencieusement ailleurs.
- **Famille AT-S95 dédiée** (`Allied Telesis · ancienne série (AT-8000GS)`) : détection automatique depuis `show version` (bannière `SW version …` ou table Unit/SW/Boot/HW) avant la famille AlliedWare Plus.
  - Ports `g1`…`g48` et piles `1/g1`, canaux `ch1` ; requêtes `show interfaces status`, `show vlan`, `show interfaces switchport` (mode général classé access selon l'appartenance), `show bridge address-table`, `show version` / `show system`.
  - La console RS-232 de référence est à **115 200 bauds** (2400–115 200 selon réglage) ; l'auto-détection de vitesse reste disponible.
  - Lecture, détection du port et export de la configuration pris en charge ; les écritures sont refusées avec un message explicite tant que le dialecte de configuration (`configure`, `interface ethernet`, `copy running-config startup-config`) n'a pas été validé sur matériel.
- **Journal CLI** : les commandes envoyées sont tracées (mots de passe en `******`, réponses sensibles masquées) en complément des réponses reçues.

## 1.0.8 — 1er octobre 2026

- **Console lente** : la réception d'une commande distingue désormais l'inactivité de la durée totale. Une sortie qui continue d'arriver à 9600 bauds n'est plus interrompue après 25 secondes ; l'attente reste bornée à cinq minutes et annulable.
- **Console et SSH** : aucune touche Entrée supplémentaire n'est envoyée pendant une commande lente. La relance d'une console muette est réservée à la connexion initiale.
- **Dépendances et mises à jour** : correction des liaisons WPF des barres de progression qui provoquaient une `InvalidOperationException` à l'ouverture des fenêtres.
- **Recherche des mises à jour** : une vérification manuelle rafraîchit la recherche même si une version avait déjà été trouvée.
- **Messages d'erreur** : une erreur générale de l'application ne demande plus systématiquement de reconnecter le switch ; une session fermée après un échec de lecture n'est plus annoncée comme connectée.
- **Tests série** : scénario de lecture de 26 secondes sur pseudo-terminal Linux, avec vérification de la commande suivante. DTR reste activé sur Windows ; les pseudo-terminaux Linux ne disposent pas de cette ligne matérielle.

## 1.0.7 — 30 septembre 2026

Correctifs ciblés sur le cas « session ouverte mais première commande sans réponse » (timeout après connexion réussie, en SSH comme en console).

- **SSH — Enter initial** : un retour chariot est envoyé à l'ouverture du shell avant la première lecture, comme le fait naturellement un utilisateur PuTTY ; un switch qui n'affiche sa bannière qu'après une touche ne bloque plus l'initialisation.
- **Console — DTR activé** : `DtrEnable = true` (comme PuTTY) au lieu de `false` ; certains adaptateurs USB-série et UART de switch ne transmettent rien avec DTR bas, ce qui produisait un timeout silencieux. Les tampons sont purgés à l'ouverture.
- **Console — relance conditionnelle** : un seul Enter initial, puis une unique relance après 1,5 s **uniquement si rien n'est arrivé du tout** (jamais au milieu d'un dialogue, jamais sur une invite visible).
- **Hostname non verrouillé trop tôt** : une ligne de bannière/MOTD ressemblant à un prompt ne fige plus durablement un mauvais hostname ; le verrouillage n'intervient qu'après le premier aller-retour de commande.
- **Journal de dialogue CLI** : chaque session écrit `%APPDATA%\SwitchPilot\Logs\console-*.log` (commandes envoyées + réponses reçues, réponses sensibles masquées, 200 Ko max). En cas d'échec persistant, ce fichier montre exactement ce que le switch a répondu.
- **Libellé COM** : l'absence de prompt série dit « invite CLI » au lieu de « prompt IOS ».

## 1.0.6 — 30 septembre 2026

Corrections issues d'un audit complet du code (10 analyses parallèles), centrées sur la connexion switch.

- **Allied Telesis — table MAC** : la sortie réelle `VLAN port mac fwd` (port avant la MAC, colonne de forwarding, type en dernier) est désormais parsée par `AlliedTelesisParser.Macs`. La réutilisation du parseur Cisco faisait échouer toute détection sur un vrai switch Allied.
- **Console — invite de connexion Allied** : les invites `login as:` et `awplus login:` sont reconnues (et non plus seulement `Username:`/`login:`), avec envoi d'un identifiant vide pour les consoles anonymes. C'était un blocage certain de la connexion COM.
- **Syslogs AlliedWare Plus** : les lignes horodatées `… local0.notice …` (sans `%`) sont retirées comme les syslogs Cisco, sinon le prompt n'était plus détecté après une annonce asynchrone, en SSH comme en console.
- **Détection du constructeur** : un switch Allied n'est plus envoyé au pilote Cisco quand la bannière est ambiguë (essai des deux familles).
- **SSH `keyboard-interactive`** : le mot de passe est prioritaire ; un prompt « Password for user: » ne reçoit plus le login.
- **Allied — trunks et descriptions** : `switchport trunk allowed vlan none` puis `add <liste>` (forme AW+ valide) au lieu de la forme Cisco ; description limitée à 80 caractères.
- **Allied — VLANs** : les lignes de continuation (ports wrappés) sont prises en compte, un trunk n'est plus classé `access`.
- **Allied — modèle** lu depuis `show system` ; commande `show interface description` (inexistante en AW+) supprimée.
- **Cisco** : la lecture des VLANs ne fait plus échouer tout l'inventaire ; `Port-channel1` reconnu dans la table MAC ; comparaisons trunk insensibles à la casse ; modèles Catalyst récents reconnus ; fraîcheur TDR corrigée.
- **Réseau** : la preuve de route directe ne rejette plus une réponse DNS contenant de l'IPv6.
- **Terminal** : l'élévation privilégiée ne se déclenche qu'en cas de refus d'autorisation et n'efface plus l'erreur d'origine.
- **Dépendances Npcap** : `InvalidDataException` gérée (plus de boîte « erreur inattendue »), valeur de registre défensive, nettoyage qui ne masque plus le résultat, refus UAC distinct.
- **Mise à jour** : redirections revalidées par saut (`AllowAutoRedirect=false`), timeout d'inactivité sur le corps, relance dans le bon dossier, attente du PID bornée, `CanInstall` sans I/O disque à chaque requête, version affichée à 3 composants.
- **Docs/version** : source de version unique (le script PowerShell lit `Directory.Build.props`), références 1.0.2 corrigées, description `IsDirectRoute` et « PTY » rectifiées.

## 1.0.5 — 30 septembre 2026

- Détection automatique du constructeur à la connexion (`show version`) : le pilote Cisco IOS ou Allied Telesis est choisi sans intervention, en SSH comme en console. Le sélecteur « Détection automatique » est désormais l’option par défaut ; un mauvais choix envoyait les mauvaises commandes et faisait échouer les deux transports de la même façon.
- Mode privilégié adaptatif : les lectures ne forcent plus `enable` a priori ; l’élévation n’a lieu qu’en cas de refus, et l’absence de mot de passe `enable` ne bloque plus une simple lecture.
- Lecture Allied tolérante : un format `show vlan brief` inattendu ne fait plus échouer la connexion.
- Message clarifié : « Connecté, mais la lecture de l’état a échoué : … » distingue un problème de lecture d’un échec d’authentification.

## 1.0.4 — 30 septembre 2026

- Connexion SSH : l’authentification propose désormais `password` **et** `keyboard-interactive`, comme PuTTY et OpenSSH. Les switchs qui n’annoncent que `keyboard-interactive` (fréquent sur Allied Telesis et les anciens IOS) peuvent se connecter avec les mêmes identifiants.
- Détection d’incompatibilité élargie : la proposition d’algorithmes hérités se déclenche aussi lorsque la lib SSH signale un désaccord d’algorithme/chiffrement sans code `KeyExchangeFailed`.
- Diagnostic : les erreurs SSH affichent maintenant le détail renvoyé par la lib (méthode refusée, algorithme manquant, etc.) pour identifier la cause réelle.

## 1.0.3 — 30 septembre 2026

- Compatibilité multi-constructeurs : sélection du constructeur à la connexion et prise en charge d'Allied Telesis (AlliedWare Plus) — état des ports, VLANs, table MAC, compteurs et modifications access/trunk, en SSH comme en console. Le TDR Cisco reste indisponible sur ces switchs, le contrôle passif s'applique.
- Mise à jour automatique : au démarrage, Switch Pilot interroge les releases GitHub, propose la nouvelle version, télécharge l'exécutable portable, vérifie son empreinte SHA-256 puis le remplace et redémarre. Vérification manuelle depuis Paramètres ; possibilité d'ignorer une version.
- Interface adaptative : la fenêtre principale s'ajuste à l'écran (petits comme grands moniteurs), les volets latéraux sont proportionnels et les fenêtres de dialogue deviennent défilables sur les écrans peu hauts.
- Assistant Npcap : proposition d'installation/réparation pour tous les états anormaux (service arrêté, accès restreint, DLL manquante), déblocage du fichier vérifié pour laisser l'installateur s'exécuter, et nettoyage qui ne masque plus le résultat.

## 1.0.2 — 30 septembre 2026

- Assistant Npcap : état du pilote, service, DLL et version, téléchargement HTTPS officiel, vérification Authenticode et éditeur, installation interactive avec UAC séparée, nouvelle vérification et nettoyage. Accès permanent dans Paramètres ; report limité au lancement courant.
- Console série : ports COM nommés, information sur les pilotes FTDI/Prolific/Silicon Labs/Cisco, 9600–115200 bauds, 8N1, détection de vitesse, login facultatif, dialogue initial précis et retour du mode configuration. Profils DPAPI compatibles avec les anciens profils SSH.
- Conversation CLI commune à SSH et série ; refus IOS sans `%`, syslogs intercalés, distinction refus d’autorisation / fonctionnalité absente / lancement non confirmé.
- Surveillance Ethernet et LLDP/CDP continue par carte, expiration et retrait TTL, invalidation au débranchement, recoupement avec la table MAC, protection contre les réponses d’un ancien branchement.
- Contrôle local automatique : lien, vitesse, duplex lorsque disponible, erreurs, trafic et oscillations. Contrôle passif du switch après identification, verdict justifié et historique par port.
- TDR du port du poste autorisé en console ; protections SSH conservées. Option automatique console désactivée par défaut, simulation respectée, protection contre les relances dues au test.
- Accueil Technicien, inventaire local des prises et CSV, notifications Windows, copie du résultat, comparaison en mémoire des sauvegardes.
- Sauvegarde complète DPAPI avant chaque modification réelle ; échec de sauvegarde bloquant. Blocage des modifications dangereuses sur le chemin SSH.
- Correction du type de route directe Windows ; proposition de nouvelle connexion avec algorithmes SSH hérités après échec de négociation ; intervalle automatique configurable, 60 s par défaut.
- Une seule copie `.unreadable` par contenu de paramètres corrompus. Suppression de `RedactConfig`, inutilisée ; export complet chiffré conservé.
- Publication Windows x64 autonome single-file, version et documentation mises à jour. L’application refuse un lancement élevé ; seul l’installateur Npcap demande UAC.

Les résultats de tests et les validations Windows/matérielles restantes figurent dans [VALIDATION.md](docs/VALIDATION.md). La présence d’une fonctionnalité dans cette liste ne constitue pas une validation sur un switch physique.

## 1.0.1

- Détection MAC ciblée et commandes de lecture réduites.
- Traitement CLI incrémental, lectures bornées et stockage atomique.
- Publication compressée autonome et premiers tests d’intégration SSH/WPF.
