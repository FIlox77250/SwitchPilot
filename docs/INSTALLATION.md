# Installer Switch Pilot sur Windows

Switch Pilot est une application **portable pour Windows x64**. Il n’y a pas d’assistant d’installation : télécharger le fichier puis le lancer suffit. Aucun runtime Python, Java ou .NET n’est nécessaire.

## Télécharger et lancer

1. Se connecter au compte GitHub autorisé à consulter ce dépôt privé.
2. Ouvrir [la version 1.0.1](https://github.com/FIlox77250/SwitchPilot/releases/tag/v1.0.1).
3. Dans **Assets**, télécharger **SwitchPilot.exe** (environ 65 Mio). [Lien direct vers l’exécutable](https://github.com/FIlox77250/SwitchPilot/releases/download/v1.0.1/SwitchPilot.exe).
4. Placer le fichier dans un dossier utilisateur, par exemple `Documents\SwitchPilot`, puis double-cliquer dessus. Les droits administrateur ne sont pas nécessaires pour les fonctions SSH.
5. Cliquer sur **Démonstration** pour découvrir les écrans sans switch ni identifiants. Les données et modifications de ce mode sont fictives.

Le fichier **SwitchPilot-1.0.1-win-x64.zip** contient le même exécutable, la documentation, les licences et les informations de build. Extraire l’archive avant de lancer l’application. Les archives **Source code** proposées automatiquement par GitHub contiennent les sources, pas l’exécutable prêt à lancer.

## Connecter un switch

Cliquer sur **Connecter un switch**, saisir l’adresse et les identifiants SSH, puis vérifier l’empreinte de la clé du switch. Choisir la carte Ethernet dans **Mon branchement** et cliquer sur **Détecter mon port**.

La simulation est activée par défaut. Les modifications réelles demandent de désactiver la simulation puis de confirmer les commandes affichées. La sauvegarde `write memory` est une action distincte avec confirmation.

Les paramètres et les identifiants éventuellement mémorisés sont chiffrés dans `%APPDATA%\SwitchPilot`. Ils ne sont pas enregistrés à côté de l’exécutable. Les sauvegardes chiffrées sont liées au compte Windows.

## LLDP/CDP facultatif

La détection sans identifiants nécessite Npcap sur Windows. Sans Npcap, utiliser la détection SSH ; l’application affiche un message explicite. L’installation de Npcap et les droits de capture dépendent de la politique du poste. Npcap n’est pas inclus dans l’exécutable.

## Vérifier le téléchargement

Télécharger également **SHA256SUMS.txt** dans la release. Dans PowerShell, depuis le dossier contenant l’exécutable :

```powershell
Get-FileHash .\SwitchPilot.exe -Algorithm SHA256
```

Comparer la valeur au fichier de sommes SHA-256. L’exécutable est non signé : Windows SmartScreen ou l’antivirus peut afficher une alerte. Ne pas désactiver les protections ; vérifier la provenance, le hash et les règles de son organisation.

## En cas de problème

Préciser la version de Windows, le message affiché et si le problème survient en démonstration ou avec un switch. Pour un problème SSH, ajouter le modèle et la version IOS. Ne publier aucun mot de passe, secret de configuration ou fichier de paramètres.

Consulter le [rapport de validation](VALIDATION.md) pour distinguer les tests exécutés des vérifications matérielles et Windows encore à effectuer.
