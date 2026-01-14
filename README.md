# Absynthium_ReservedSlots

Plugin CounterStrikeSharp pour gérer des slots réservés avec configuration propre et logs debug optionnels.

## Installation / Build
- Compiler : `dotnet build Absynthium_ReservedSlots.csproj -c Release -f net8.0` ou `.\compile.ps1` (copie les DLL dans `compiled/counterstrikesharp/plugins/Absynthium_ReservedSlots`).
- Déployer : copier `Release/Absynthium_ReservedSlots/*.dll` dans `addons/counterstrikesharp/plugins/Absynthium_ReservedSlots/` sur le serveur.
- Config : placer `configs/plugins/Absynthium_ReservedSlots/Absynthium_ReservedSlots.json` sur le serveur (se crée automatiquement si absent).

## Configuration (`configs/plugins/Absynthium_ReservedSlots/Absynthium_ReservedSlots.json`)
- `ConfigVersion` : version du format de config (laisser 1).
- `reserve_permission_flags` : liste de flags CSS qui donnent l’accès réservé (par défaut `@css/vip`, `@css/ban`). Exemple pour admin : `["@css/vip", "@css/admin"]`.
- `css_reserved_slots` : nombre de slots réservés (0 = désactivé).
- `css_reserve_max_slot` : plafond de slots visibles avant retrait des réservés (0 = max serveur).
- `css_hide_slots` : si true, cache les slots réservés (réduit `sv_visiblemaxplayers`).
- `css_reserve_type` : stratégie
  - 0 : refuse les joueurs sans permission quand c’est plein.
  - 1 : kick un joueur sans permission pour laisser entrer un joueur autorisé.
  - 2 : mode admins réservés, jusqu’à `css_reserve_maxadmins`.
- `css_reserve_maxadmins` : nombre d’admins autorisés en plus (type 2).
- `css_reserve_kicktype` : choix de la cible à kick (0 ping le plus haut, 1 plus long temps connecté, 2 aléatoire).
- `css_reserve_check_delay` : délai (s) avant la vérification si `css_reserve_instant_check` est false ou pour le check différé.
- `css_reserve_permission_grace` : délai (s) d’attente pour que les permissions externes soient chargées après connexion.
- `css_reserve_instant_check` : si true, vérifie dès la mise en jeu.
- `css_reserve_logs` : 0 désactivé, 1 console, 2 console + fichier.
- `debug` : true pour logs détaillés (slots, flags, VIP/ban, délais).

## Réglages rapides
- 2 slots VIP cachés : `css_reserved_slots=2`, `css_hide_slots=true`, `css_reserve_type=0`.
- Priorité VIP qui remplace un joueur : `css_reserve_type=1`, choisir `css_reserve_kicktype`.
- Permissions lentes (base VIP externe) : mettre `css_reserve_permission_grace` à 1–3s et `debug=true` le temps de tester.
