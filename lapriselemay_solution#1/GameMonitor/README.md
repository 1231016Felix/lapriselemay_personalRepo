# GameMonitor

Overlay de monitoring natif pour PC de jeu Windows (pensé pour l'Acer Nitro N50-656).
Il affiche en superposition, par-dessus le jeu, les FPS et l'état de la machine, en consommant le moins possible.

- Un seul fichier `GameMonitor.exe` d'environ 600 Ko, sans installation ni runtime à ajouter.
- Environ 0,2 % de CPU et 10 Mo de mémoire privée (mesuré sur un i5-1135G7).
- Win32 pur, sans moteur web ni framework graphique.

## Mesures affichées

| Ligne | Contenu | Source |
|---|---|---|
| FPS | images/s, temps d'image moyen, pire temps d'image, nom du jeu | ETW (DXGI et D3D9), comme PresentMon |
| CPU | utilisation, fréquence réelle (turbo inclus), température | sysinfo, compteurs PDH, WMI |
| GPU | utilisation, fréquence, température, consommation (W), ventilateur | NVML (pilote NVIDIA), sinon compteurs Windows |
| VRAM | mémoire vidéo utilisée / totale | NVML |
| RAM | utilisée / totale, pourcentage | sysinfo |
| Disque | lecture, écriture, % d'activité | compteurs PDH |
| Réseau | débit descendant et montant | sysinfo |

Le mode détaillé ajoute : nom du CPU et du GPU, fréquence mémoire du GPU, charge de chaque cœur,
fichier d'échange, espace libre de chaque disque, durée depuis l'allumage et la consommation de l'overlay lui-même.

## Utilisation

1. Copier `GameMonitor.exe` sur le PC de jeu (n'importe quel dossier).
2. Le lancer : Windows demande les droits administrateur (nécessaires pour les FPS et la température CPU).
3. Mettre le jeu en mode **fenêtré sans bordure** (« borderless »). En plein écran exclusif, Windows
   ne laisse aucune fenêtre s'afficher par-dessus le jeu.

| Raccourci | Action |
|---|---|
| Ctrl+Maj+F9 | Mode détaillé |
| Ctrl+Maj+F10 | Masquer / afficher (la collecte se met en pause quand c'est masqué) |
| Ctrl+Maj+F11 | Changer de coin d'écran |
| Ctrl+Maj+F12 | Quitter |

L'overlay laisse passer les clics et ne prend jamais le focus du jeu.

### Température du CPU

Windows ne donne pas la température des cœurs du processeur sans pilote spécial. GameMonitor essaie, dans l'ordre :

1. **LibreHardwareMonitor**, s'il tourne en arrière-plan. C'est la mesure la plus précise.
   Il est portable (rien à installer) : <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>.
   GameMonitor lit ses capteurs automatiquement.
2. La zone thermique ACPI de la carte mère, si elle en a une. Elle est moins précise et absente sur beaucoup de PC.

Sinon, la ligne affiche « temp. n/d ». La température du GPU NVIDIA, elle, fonctionne toujours.

### Limites connues

- FPS : les jeux DirectX 9 à 12 sont couverts. Les jeux Vulkan ou OpenGL peuvent ne pas en afficher.
- L'overlay ne s'affiche pas en plein écran exclusif (voir ci-dessus).

## Compiler

Il faut Rust (`rustup`, chaîne MSVC) et les outils de compilation C++ de Visual Studio.

```bash
cargo build --release
```

L'exe se trouve dans `target/release/GameMonitor.exe`. Le CRT est lié statiquement (`.cargo/config.toml`),
donc l'exe n'a pas besoin du Visual C++ Redistributable.

Pour tester sans la demande d'administrateur : `cargo run` (debug), ou définir `GAMEMONITOR_NO_ADMIN=1` avant
`cargo build --release`.

## Structure

```
src/
  main.rs          démarrage
  overlay.rs       fenêtre Win32, dessin GDI, raccourcis
  format.rs        mise en forme et couleurs des lignes
  metrics/
    mod.rs         thread de collecte (1 fois par seconde)
    fps.rs         FPS via ETW
    gpu.rs         NVML ou compteurs génériques
    temps.rs       température CPU via WMI
    pdh.rs         compteurs de performance Windows
```
