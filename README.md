# Claude-Sync

*Version française : [README.fr.md](README.fr.md)*

Keep your **Claude Desktop conversations** (Cowork / Code sessions) and your **project folder** identical on several Windows PCs,
through **your own server** reached over SSH. No cloud service, no account, nothing leaves your network.

> **Status: beta (2.0.0-dev).** Developed and tested by its author (including simulated slow links), not yet widely tested in daily use. Keep your own backups.

## How it works

- Each PC runs a small tray application. When Claude Desktop closes, it sends what changed to the server; when Claude opens, it fetches what the other PC did.
- Copies use **rsync over SSH**: only the *difference* of a file travels, compressed. A conversation log that grows by 1 MB sends roughly a quarter of that (measured: 274 KB), not the whole file — useful on a slow 4G link.
- The server keeps a trash and an archive for 30 days: nothing is ever overwritten or deleted without a copy.
- If both PCs changed the same file, nothing is lost: it shows up as a *conflict* and you choose which version to keep.
- Windows only (the PCs). The server can be any machine with SSH, `rsync` and Python 3 (a Linux NAS, a Raspberry Pi, a VPS…).

## Install

1. **Server** (once): an SSH account with `rsync` and `python3` installed. Create a dedicated account if you can; a normal user account works too.
2. **Each PC**: run `Claude-Sync-Setup.exe`. No administrator rights are needed (it installs into your user profile).
3. At first start, enter the server address, your user name, the folder to use on the server and your **password — it is used once** to
   install a dedicated SSH key for this PC and is never stored. After that the key (restricted to Claude-Sync only: it can run nothing else on the server) is used.
4. Done. Claude-Sync starts with Windows and watches Claude Desktop.

Windows may show *"Unknown publisher"*: the installer is not signed with a paid code-signing certificate. Check the SHA-256 shown on the release page.

## Security notes

- Keep the SSH port behind a VPN or a firewall; do not expose it to the Internet.
- Each PC gets its own key, restricted by a forced command to Claude-Sync's own operations (rsync into one folder without deletion, and the Claude-Sync server program).
- The password is only used during setup, through a temporary SSH askpass helper, and is never written to disk.
- Updates: the app can check GitHub Releases (setting `mises_a_jour` = `owner/repo` in `config.json`), verifies the published SHA-256, and **only installs when you click**.
  The checksum proves the file is intact, not who published it: trust rests on the repository's owner.

## Build from source

Requires Windows and PowerShell; the compiler (Roslyn) is vendored in `tools/roslyn`. Python 3 is needed for the tests.

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-v2.ps1            # builds and runs the tests
# result: dist\v2app\ClaudeSync2.exe  (single file, includes the tools)
```

## License

MIT (see `LICENSE`). The installer embeds unmodified rsync, OpenSSH and their libraries from MSYS2, each under its own license:
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). "Claude" is a trademark of Anthropic; this project is independent and not affiliated with or endorsed by Anthropic.
