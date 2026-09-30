# Changelog

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
