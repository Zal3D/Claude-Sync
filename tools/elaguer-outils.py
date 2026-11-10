#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Prepare les outils embarques de Claude-Sync V2 (rsync.exe + ssh.exe de MSYS2) a partir des paquets MSYS2 deja
telecharges et extraits :
  * lit la table d'imports de rsync.exe et ssh.exe (format PE) et garde seulement les bibliotheques necessaires ;
  * copie le tout dans <sortie>\\win-x64 ;
  * copie les textes de licence des paquets utilises dans <sortie>\\licences\\<paquet> ;
  * ecrit <sortie>\\paquets.json : paquet, version, fichier, SHA-256, URL officielle, licence, source.
Ne lance AUCUN programme telecharge : il ne fait que lire des fichiers.

Usage : python -I -X utf8 tools\\elaguer-outils.py --depot <dossier des paquets extraits> --sortie v2\\outils
  <depot> contient : rsync-bin\\ (assemblage complet), pkg\\ pkg2\\ pkg3\\ (fichiers .pkg.tar.zst), x-* y-* z-* (paquets extraits)
"""
import argparse, hashlib, json, os, shutil, struct, sys


def imports(path):
    d = open(path, "rb").read()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    if d[pe:pe + 4] != b"PE\0\0":
        raise ValueError("pas un PE : " + path)
    nsec = struct.unpack_from("<H", d, pe + 6)[0]
    optsz = struct.unpack_from("<H", d, pe + 20)[0]
    opt = pe + 24
    magic = struct.unpack_from("<H", d, opt)[0]
    dd = opt + (112 if magic == 0x20B else 96)
    imp_rva, _ = struct.unpack_from("<II", d, dd + 8)
    secs = []
    for i in range(nsec):
        _n, vs, va, rs, rp = struct.unpack_from("<8sIIII", d, opt + optsz + i * 40)
        secs.append((vs, va, rs, rp))

    def off(rva):
        for vs, va, rs, rp in secs:
            if va <= rva < va + max(vs, rs):
                return rp + rva - va
        return None

    res = []
    p = off(imp_rva) if imp_rva else None
    while p is not None:
        _a, _b, _c, name_rva, _e = struct.unpack_from("<IIIII", d, p)
        if name_rva == 0:
            break
        o = off(name_rva)
        res.append(d[o:d.index(b"\0", o)].decode("ascii", "replace"))
        p += 20
    return res


def lire_pkginfo(dossier):
    info = {"license": []}
    p = os.path.join(dossier, ".PKGINFO")
    if not os.path.isfile(p):
        return None
    for ligne in open(p, encoding="utf-8"):
        if " = " in ligne and not ligne.startswith("#"):
            k, v = ligne.rstrip("\n").split(" = ", 1)
            if k == "license":
                info["license"].append(v)
            elif k in ("pkgname", "pkgver", "pkgbase", "url"):
                info[k] = v
    return info


def sha256(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--depot", required=True)
    ap.add_argument("--sortie", required=True)
    a = ap.parse_args()
    src = os.path.join(a.depot, "rsync-bin")
    dispo = {f.lower(): f for f in os.listdir(src)}
    besoin, file_ = set(), ["rsync.exe", "ssh.exe", "ssh-keygen.exe"]
    while file_:
        f = file_.pop()
        if f.lower() in besoin:
            continue
        if f.lower() not in dispo:
            continue                                  # DLL du systeme Windows : rien a embarquer
        besoin.add(f.lower())
        for dll in imports(os.path.join(src, dispo[f.lower()])):
            file_.append(dll)
    gardes = sorted(dispo[b] for b in besoin)
    inutiles = sorted(f for f in dispo.values() if f.lower() not in besoin)

    sortie = os.path.join(a.sortie, "win-x64")
    os.makedirs(sortie, exist_ok=True)
    for f in os.listdir(sortie):
        os.remove(os.path.join(sortie, f))
    taille = 0
    for f in gardes:
        shutil.copy2(os.path.join(src, f), os.path.join(sortie, f))
        taille += os.path.getsize(os.path.join(sortie, f))
    print("gardes : %d fichiers, %.1f Mo ; ecartes : %d (%s)" % (len(gardes), taille / 1048576, len(inutiles), ", ".join(inutiles)))

    # paquets : quel paquet a fourni quel fichier gardé
    pkgs = {}
    fichiers_pkg = {}
    for sub in ("pkg", "pkg2", "pkg3"):
        d = os.path.join(a.depot, sub)
        if os.path.isdir(d):
            for fn in os.listdir(d):
                if fn.endswith(".pkg.tar.zst"):
                    fichiers_pkg[fn] = os.path.join(d, fn)
    for nom in sorted(os.listdir(a.depot)):
        dossier = os.path.join(a.depot, nom)
        if not (os.path.isdir(dossier) and nom[:2] in ("x-", "y-", "z-")):
            continue
        info = lire_pkginfo(dossier)
        bin_ = os.path.join(dossier, "usr", "bin")
        if not info or not os.path.isdir(bin_):
            continue
        fournis = [f for f in os.listdir(bin_) if f.lower() in besoin]
        if not fournis:
            continue
        fich = "%s-%s-x86_64.pkg.tar.zst" % (info["pkgname"], info["pkgver"])
        if fich not in fichiers_pkg:
            print("ATTENTION : fichier de paquet introuvable pour", fich)
            continue
        cle = info["pkgname"]
        if cle in pkgs and pkgs[cle]["version"] > info["pkgver"]:
            continue                                  # garde la version la plus recente
        base = info.get("pkgbase", info["pkgname"])
        pkgs[cle] = {
            "paquet": info["pkgname"], "version": info["pkgver"], "fichier": fich,
            "sha256": sha256(fichiers_pkg[fich]), "taille": os.path.getsize(fichiers_pkg[fich]),
            "url": "https://repo.msys2.org/msys/x86_64/" + fich,
            "licence": info["license"], "site": info.get("url", ""),
            "source": "https://repo.msys2.org/msys/sources/%s-%s.src.tar.zst" % (base, info["pkgver"]),
            "fournit": sorted(fournis),
        }
        # textes de licence
        lic = os.path.join(dossier, "usr", "share", "licenses")
        if os.path.isdir(lic):
            dest = os.path.join(a.sortie, "licences")
            shutil.copytree(lic, dest, dirs_exist_ok=True)
    with open(os.path.join(a.sortie, "paquets.json"), "w", encoding="utf-8") as f:
        json.dump({"depot": "https://repo.msys2.org/msys/x86_64/", "note": "Empreintes SHA-256 des paquets MSYS2 d'origine (verifiees contre msys.db au telechargement). Les 'source' sont les archives de sources correspondantes du depot MSYS2.",
                   "paquets": [pkgs[k] for k in sorted(pkgs)]}, f, indent=2, ensure_ascii=False)
    print("paquets utilises :", ", ".join("%s %s" % (p["paquet"], p["version"]) for p in (pkgs[k] for k in sorted(pkgs))))
    print("manifeste :", os.path.join(a.sortie, "paquets.json"))


if __name__ == "__main__":
    main()
