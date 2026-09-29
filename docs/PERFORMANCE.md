# Mesures de performance — 1.0.1

Mesures exécutées sous Ubuntu 24.04, SDK .NET 10.0.401, build Release. Il s’agit de microbenchmarks synthétiques ; ni la latence d’un Catalyst ni le temps de démarrage sur Windows natif n’ont été mesurés.

| Scénario | Avant (1.0.0) | Après (1.0.1) | Évolution observée |
| --- | ---: | ---: | ---: |
| Traitement d’une réponse CLI d’environ 1 Mio | 4 138 ms | 1 761 ms | −57 % |
| Octets alloués pendant ce traitement | 298 831 952 | 12 704 896 | −96 % |
| Commandes pour 10 détections après connexion | 60 | 30 | −50 % |
| Durée de ces 10 détections, latence simulée de 10 ms/commande | 611 ms | 314 ms | −49 % |

La réponse CLI arrive en 64 fragments. Le traitement incrémental remplace le nettoyage répété de l’intégralité de la réponse. La reconnaissance de l’invite se limite aux 512 derniers caractères. Les allocations indiquent les octets alloués pendant l’opération, pas la mémoire maximale du processus ; cette mesure n’est pas une promesse de réduction identique de la RAM de l’application.

La détection conserve l’identité du switch pour la session, utilise la recherche MAC ciblée et relit le mode du port correspondant. Une MAC absente nécessite seulement la recherche MAC. Les résultats ambigus, plusieurs ports ou les syntaxes non prises en charge peuvent nécessiter plus de commandes. La connexion initiale et une actualisation manuelle conservent une lecture complète.

La rotation du journal ne parcourt plus les anciens fichiers à chaque action : elle s’exécute une fois par jour. La capture Npcap réénumère les cartes à chaque écoute pour prendre en compte les branchements et éviter de réutiliser un objet de capture fermé.

Le bundle Windows utilise la compression native .NET, sans UPX. Il reste autonome ; les bibliothèques natives sont extraites par .NET dans le dossier temporaire utilisateur. Le coût de démarrage et le comportement antivirus doivent encore être vérifiés sur Windows natif.

## Reproduire la mesure actuelle

Depuis la racine du projet, avec le SDK indiqué dans `global.json` :

```sh
dotnet run --project tests/SwitchPilot.Benchmarks -c Release
```

Résultats bruts archivés : [avant](performance/before.json), [après](performance/after.json). Le programme actuel mesure la version optimisée ; le résultat initial est conservé comme référence. Une seule mesure par version a été utilisée, sans intervalle statistique. Les durées varient avec la charge de la machine et l’ordonnancement ; la réduction du nombre de commandes est déterministe pour le scénario testé.
