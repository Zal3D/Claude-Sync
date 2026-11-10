# Claude-Sync V2 — mesures en liaison lente (J5)

Essai du **vrai moteur** V2 (deux PC d'essai, vrai serveur d'essai (Linux), vraies copies par rsync/SSH) à travers un relais local
(`tools\proxy-latence.py`) qui simule une mauvaise liaison : **150 ms aller-retour, 2 Mbit/s montant, 20 Mbit/s descendant** (ordre de grandeur
d'une 4G via VPN). Les volumes sont ceux **réellement vus passer sur le fil** (compteurs du relais), pas une estimation.
Relancer : `powershell -ExecutionPolicy Bypass -File tools\build-v2.ps1 -Lente -Enrol <dossier> -RootKey <clé>` (tests T170-T178, ~4 min). Date : 2026-11-10.

## Résultats (34 vérifications, 0 échec)

| Situation | Durée | Montant | Descendant |
|---|---|---|---|
| Liaison mesurée par Claude-Sync | 170 ms → classée **lente** (seuil 120 ms) | | |
| Premier envoi : 150 fichiers de 2 Ko + un journal de 8 Mo | 18,5 s | 2,2 Mo | 23 Ko |
| Le journal grossit de **1 Mo** à la fin | 8,3 s | **274 Ko** | 29 Ko |
| **100 Ko modifiés au milieu** du journal (9 Mo) | 7,1 s | **35 Ko** | 29 Ko |
| Rien n'a changé (151 fichiers) | 4,6 s | 4,7 Ko | 7 Ko |
| 2e PC : première récupération (151 fichiers, 9 Mo) | 10,8 s | 17 Ko | 2,5 Mo |
| 2e PC : récupère +300 Ko et 2 fichiers (cas de l'**ouverture de Claude**) | 7,2 s | 27 Ko | 88 Ko |
| Coupure de la liaison en plein envoi de 5 Mo incompressibles | arrêt propre en 10 s (code 2) | 1,0 Mo partis | |
| Liaison revenue : nouvelle tentative | 33,7 s | 5,0 Mo (tout renvoyé) | 13 Ko |
| 2e PC récupère ce gros fichier | 11,8 s | 11 Ko | 5,0 Mo (identique octet pour octet) |

### Synchro sans changement selon la latence (c'est ce qui rythme l'ouverture/fermeture de Claude)

| Latence aller-retour | LAN (~2 ms) | 50 ms | 150 ms | 300 ms | 600 ms |
|---|---|---|---|---|---|
| Durée | 0,9 s | 2,6 s | 4,6 s | 7,7 s | 14,4 s |

## Ce que ça montre

1. **Le delta fonctionne comme prévu** : ajouter 1 Mo à un journal de 8 Mo ne fait partir que 274 Ko (le texte est aussi compressé) ; modifier 100 Ko au
   milieu n'en fait partir que 35. C'est exactement le besoin de départ (le `.jsonl` qui grossit de 205 à 210 Mo : quelques secondes, pas des minutes).
   Pour mémoire, en V1 (SMB) la même situation à 150 ms / 2 Mbit/s avait été mesurée à ~46 s contre ~2,4 s en rsync.
2. **Le cas de l'ouverture de Claude** (récupérer ce que l'autre PC a fait) : ~7 s à 150 ms, très loin du budget de 120 s du mode économe.
3. **Une coupure ne corrompt rien** : la synchro s'arrête proprement (pas de blocage, pas de conflit inventé), le fichier local reste intact, rien de
   partiel n'est visible sur le serveur, et la tentative suivante aboutit avec un fichier identique octet pour octet chez l'autre PC.
4. **Limite connue (documentée depuis le début)** : après une coupure, le fichier en cours est **renvoyé en entier** (ici 5 Mo, 34 s). Le delta ne
   sert qu'une fois le fichier arrivé une première fois. Reprendre un envoi interrompu n'est pas fiable avec la protection actuelle (écriture à part
   puis remplacement) ; à rouvrir seulement si ce cas devient fréquent pour de très gros fichiers.
5. **Le coût d'une synchro « vide » est surtout de la latence**, pas du volume (12 Ko au total) : environ 30 allers-retours (connexion SSH, hello,
   verrou, présence, un listage par jeu de dossiers). Piste d'amélioration si besoin : regrouper les listages en une seule requête ; gain attendu
   environ 1/3 du temps à latence élevée. Pas nécessaire aujourd'hui (14 s même à 600 ms).

## Limites de l'essai
- Latence et débit **simulés** (relais local), sans perte de paquets ni variations (« gigue ») : une vraie 4G sera moins régulière.
  À confirmer un jour avec un vrai partage de connexion 4G depuis le PC portable.
- Données synthétiques (texte de type journal JSON, compressible 4 à 5 fois comme les vrais `.jsonl`).
- Un seul fichier de 5 Mo pour la coupure ; une coupure au tout début ou à la toute fin du transfert n'a pas été distinguée.
