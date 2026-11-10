# Claude-Sync architecture

*(Résumé en français à la fin / French summary at the end.)*

## Pieces

```
PC (Windows)                                      Server (any SSH host: rsync + python3)
┌──────────────────────────┐   one SSH session   ┌─────────────────────────────────────┐
│ ClaudeSync2.exe (C#)     │◄──── JSON lines ───►│ claudesync-entree  (forced command) │
│  - tray app + window     │                     │   └─ claudesync-serveur (python3)   │
│  - engine (3-way plan)   │   rsync over SSH    │   └─ rrsync -no-del <hub folder>    │
│  - embedded rsync/ssh    │◄───── data ────────►│ hub folder: projets/ claude/ ...    │
└──────────────────────────┘                     └─────────────────────────────────────┘
```

- **Hub folder** on the server holds one copy of each synchronised set (project folder, Claude Desktop sessions, ...), plus `_corbeille/` (trash), `_anciennes/` (previous versions), `_etat/` (hub id, presence, lock).
- **Server program** (`v2/serveur/claudesync-serveur`): one process per SSH session, JSON request/response per line (`hello`, `list`, `stat`, `hash`, `touch`, `lock.acquire/release`, `presence.*`, `trash*`, `archive`, `purge`...). Paths are cleaned (no `..`, no absolute paths, no NUL), nothing is ever executed from user data.
- **Data** travels with **rsync** (`--files-from` batches, delta transfer, `-z`, `--partial-dir`, never `--delete`). Both programs come from MSYS2 and are shipped in the installer (`v2/outils`, see `THIRD-PARTY-NOTICES.md`).
- **Forced command**: each PC's key is installed with `restrict,command="CS_ROOT=<hub> ~/.claudesync/claudesync-entree"`. The only things it can run are `rsync --server` (through `rrsync -no-del`, limited to the hub) and the Claude-Sync server program.

## Engine rules (`v2/Planner.cs`, `v2/Engine2.cs`)

Three-way comparison per file: **L**(ocal) vs **H**(ub) vs **B**(ase = what both agreed on at the last sync).

- Only one side changed → copy it to the other. Both changed differently → **conflict** (nothing overwritten; the user chooses).
- A deletion is never propagated blindly: the file goes to the hub's trash (30 days) and the other PC's copy goes to its local trash after a double check.
- Every overwrite archives the previous version first. Nothing is permanently deleted before 30 days.
- Safety stops: an empty set when files existed before, read errors during scanning, a suspicious number of deletions → no deletion that run.
- After each transfer the engine re-reads the destination (`stat`) and records that as the new base; a file modified during its upload is re-sent, not treated as a conflict.
- A lock held by the SSH session (released at once if the PC dies, or after 180 s of silence) prevents two PCs from syncing at the same time. A presence record warns when Claude Desktop seems open on the other PC.

## Claude Desktop watching (`v2/ui/Watcher.cs`)

A pure state machine (time is injected, so it is unit-tested): sync when Claude closes, before it opens (it can close and relaunch Claude to sync first), and periodically; an "economy mode" on slow links (> 120 ms) limits the wait at opening to 120 s and leaves the rest for the closing sync.

## Layout

| Path | What |
|---|---|
| `v2/*.cs` | hub client, rsync transport, planner, engine, state store, enrollment, installer, updater |
| `v2/ui/` | single-window UI (custom GDI+ painting), tray, watcher |
| `v2/app/` | entry point (`ClaudeSync2.exe`), setup window |
| `v2/Shared/` | small common types and helpers |
| `v2/serveur/` | server program and its forced-command dispatcher |
| `v2/outils/` | embedded rsync/ssh (pruned MSYS2 binaries), `paquets.json` (exact packages + SHA-256), licenses |
| `v2/tests/` | ~560 checks (real server program, real rsync, two simulated PCs; optional real-SSH and slow-link suites) |
| `tools/` | build script, compiler downloader, notices generator, latency proxy |

Build: `powershell -ExecutionPolicy Bypass -File tools\build-v2.ps1` (downloads the Roslyn compiler once, verifies its SHA-256; needs Python 3 for the tests). Optional real-server tests: `-Enrol <folder>` and `-RootKey <admin key>` (a disposable test server only!), slow-link suite: `-Lente`.

## Résumé en français

Le PC (programme C#, rsync et ssh embarqués) parle au serveur par **une session SSH** : lignes JSON vers un petit programme Python (liste, empreintes, verrou, présence, corbeille, archives) et **rsync** pour les données (différences seulement). La clé de chaque PC est limitée à ces deux usages. Le moteur compare trois états (local, serveur, dernière base commune) : un seul côté a changé → on copie ; les deux → conflit, rien n'est écrasé ; une suppression passe par une corbeille de 30 jours ; tout écrasement archive l'ancienne version. Le verrou lié à la session SSH évite deux synchros simultanées. La surveillance de Claude Desktop est une machine à états pure et testée ; en liaison lente (> 120 ms), l'attente à l'ouverture est plafonnée à 120 s.
