# Annexe — incidents et dépannage

## Npcap

- **Pas d’Internet / proxy** : le téléchargement suit le proxy système. Utiliser le lien d’installation manuelle vers [npcap.com](https://npcap.com/), puis **Vérifier à nouveau**. SSH, console et mesures locales restent accessibles.
- **Signature refusée** : aucun installateur n’est exécuté. Vérifier l’heure Windows, les autorités de certification et l’accès aux services de révocation. Ne pas contourner la vérification ni remplacer le fichier par une source tierce.
- **UAC refusée / installation annulée** : rien n’est annoncé comme prêt. Réessayer depuis les paramètres si souhaité.
- **Service arrêté / DLL absente / version incompatible** : réparer Npcap avec l’installateur officiel. Switch Pilot ne démarre pas de service élevé en arrière-plan.
- **Accès réservé aux administrateurs** : modifier l’installation Npcap pour autoriser la capture au compte standard. L’application ne demande pas UAC pour ouvrir une capture.
- **Redémarrage demandé par Npcap** : terminer les autres travaux et redémarrer Windows. L’activation dans le processus courant n’est possible que lorsque le pilote est réellement prêt.
- **Annulation après ouverture de l’installateur** : annuler dans la fenêtre Npcap. Switch Pilot attend sa fermeture avant de supprimer le fichier et ne tue pas une installation de pilote en cours.

## Port non détecté

Sans Npcap ni switch connecté, le port ne peut pas être connu. Sans annonces LLDP/CDP, vérifier leur configuration auprès de l’administrateur (`lldp run` / `cdp run`) ; l’application ne les active jamais.

Une table MAC nécessite que le poste ait émis du trafic dans le VLAN concerné. Une MAC sur un uplink, plusieurs correspondances, un téléphone intermédiaire ou un switch connecté différent du voisin ne prouvent pas un branchement direct. Le VLAN annoncé peut être natif ; une différence de VLAN n’est pas à elle seule une contradiction.

Les lectures automatiques du switch sont espacées d’au moins l’intervalle configuré (60 secondes par défaut). Le lien local et les annonces continuent à s’actualiser pendant cette attente.

## Console et pilotes

Fermer les autres logiciels qui utilisent le même COM. Vérifier le câble console, la vitesse et le pilote dans le Gestionnaire de périphériques. L’auto-détection essaie les cinq vitesses avec une attente bornée ; elle n’envoie ni BREAK ni commande de configuration arbitraire.

Les noms FTDI/Prolific/Silicon Labs sont identifiés à partir des identifiants USB. Tous les modèles de câbles ne peuvent pas être reconnus. Pour Cisco USB Console, utiliser le téléchargement officiel correspondant au modèle et un compte Cisco ; aucun pilote Cisco n’est téléchargé automatiquement.

À 9600 bauds, la lecture de tous les ports peut prendre plus de 25 secondes. L'application attend tant que des données arrivent, avec une limite totale de cinq minutes par commande et une limite de 25 secondes sans réception. Une annulation ou un délai dépassé ferme la session pour éviter de mélanger deux réponses ; reconnecter le switch avant de reprendre. Si le problème persiste, relever le modèle, la vitesse et la dernière commande du journal `%APPDATA%\SwitchPilot\Logs\console-*.log`, en masquant les informations sensibles avant de le partager.

## Mises à jour

**Paramètres → Vérifier les mises à jour sur GitHub** ouvre la recherche d'une version plus récente. Si une nouvelle version est proposée, **Installer maintenant** télécharge l'exécutable, vérifie son empreinte et redémarre l'application. Si la version installée est à jour, aucune installation n'est nécessaire. Cette opération ne nécessite aucune connexion au switch.

## Verdict câble et TDR

Un lien à 100 Mb/s peut être normal sur un port Fast Ethernet. Une carte Gigabit à 100 Mb/s invite à contrôler le port distant et les vitesses forcées avant de conclure à un défaut de paire.

Le duplex et la capacité maximale dépendent des informations fournies par le pilote Windows. Une valeur manquante reste inconnue. Les compteurs sont cumulatifs ; un compteur historique non nul ne prouve pas un défaut actuel. « Câble OK » exige une observation favorable avec trafic et compteurs comparables ; ce n’est pas une certification du câblage.

Le TDR exige un port cuivre access reconnu. Trunks, agrégats, fibre et modes inconnus restent bloqués en console comme en SSH. Les erreurs « non autorisé » et « non pris en charge » sont distinctes. Une annulation côté application ne garantit pas l’arrêt d’un TDR déjà lancé sur le switch.

## Sauvegardes et paramètres

Une sauvegarde préalable impossible bloque l’écriture. Vérifier l’espace disque et les droits sur `%APPDATA%\SwitchPilot\Backups`. Les sauvegardes `.spbackup` contiennent la configuration complète chiffrée et sont liées au compte Windows ; ne pas les considérer comme des sauvegardes portables entre comptes.

Un fichier de paramètres illisible est conservé, avec une copie de récupération identifiée par son contenu. Plusieurs lancements sur le même fichier ne multiplient pas ces copies.

## Démarrage et signalement

L’application refuse un lancement élevé. Retirer « Exécuter ce programme en tant qu’administrateur » des propriétés de compatibilité et démarrer depuis une session standard.

Pour un incident, relever la version, Windows, le type de connexion, le modèle/IOS et le message affiché. Ne transmettre aucun secret, sortie brute de configuration ou fichier de paramètres. Consulter [VALIDATION.md](VALIDATION.md) avant d’interpréter un scénario non encore validé sur matériel.
