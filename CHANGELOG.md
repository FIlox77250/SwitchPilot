# Changelog

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
