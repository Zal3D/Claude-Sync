#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Genere THIRD-PARTY-NOTICES.md (racine du depot + v2/outils/licences/) a partir de v2/outils/paquets.json et des textes de licence
presents dans v2/outils/licences/<paquet>/. Chaque texte distinct n'est imprime qu'une fois ; chaque paquet renvoie vers ses textes.
Refuse de generer si un paquet n'a aucun texte de licence (la liste doit rester complete).
Usage : python -I tools/generer-notices.py
"""
import hashlib, json, os, sys

racine = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
outils = os.path.join(racine, 'v2', 'outils')
lic_dir = os.path.join(outils, 'licences')
data = json.load(open(os.path.join(outils, 'paquets.json'), encoding='utf-8'))

textes = {}      # sha256 -> (texte, [ (paquet, fichier) ])
par_paquet = {}  # paquet -> [ (fichier, sha) ]
manque = []
for p in data['paquets']:
    nom = p['paquet']
    d = os.path.join(lic_dir, nom)
    fichiers = sorted(f for f in os.listdir(d)) if os.path.isdir(d) else []
    if not fichiers:
        manque.append(nom)
        continue
    for f in fichiers:
        raw = open(os.path.join(d, f), 'rb').read()
        txt = raw.decode('utf-8', errors='replace').replace('\r\n', '\n')
        sha = hashlib.sha256(txt.encode('utf-8')).hexdigest()
        textes.setdefault(sha, (txt, []))[1].append((nom, f))
        par_paquet.setdefault(nom, []).append((f, sha))
if manque:
    sys.exit('Paquets sans texte de licence : ' + ', '.join(manque))

num = {sha: i + 1 for i, sha in enumerate(textes)}
o = []
o.append('# Third-party notices / Notices des composants tiers\n')
o.append('Claude-Sync itself is released under the MIT License (see `LICENSE`). / Claude-Sync est publie sous licence MIT (voir `LICENSE`).\n')
o.append('The Windows installer embeds **unmodified** binaries of the programs below (rsync, OpenSSH and the libraries they need), taken from the '
         '[MSYS2](https://www.msys2.org/) package repository. Each keeps its own license. They are separate programs that Claude-Sync starts as '
         'child processes; they are not linked into Claude-Sync.\n')
o.append('Les installeurs Windows embarquent des binaires **non modifies** des programmes ci-dessous (rsync, OpenSSH et leurs bibliotheques), pris dans le depot de paquets '
         '[MSYS2](https://www.msys2.org/). Chacun garde sa licence. Ce sont des programmes distincts que Claude-Sync lance comme processus ; ils ne sont pas lies a Claude-Sync.\n')
o.append('## Source code / Code source\n')
o.append('For every component under the GPL or LGPL, the exact corresponding source archive (the MSYS2 source package for the same version, which contains the upstream '
         'sources and the build recipe) is linked below. It is also available from the maintainers of Claude-Sync on request. / '
         'Pour chaque composant GPL ou LGPL, l\'archive de sources correspondant exactement a la version distribuee (paquet source MSYS2 : sources amont et recette de compilation) est indiquee ci-dessous ; '
         'elle peut aussi etre demandee aux mainteneurs de Claude-Sync.\n')
o.append('## Components / Composants\n')
o.append('| Package | Version | License (as declared by MSYS2) | Home page | Source archive | License text(s) |')
o.append('|---|---|---|---|---|---|')
for p in data['paquets']:
    nom = p['paquet']
    lic = ' ; '.join(l.replace('spdx:', '') for l in p.get('licence', []))
    refs = ', '.join('[%d](#text-%d)' % (num[sha], num[sha]) for _, sha in par_paquet[nom])
    o.append('| %s | %s | %s | %s | [%s](%s) | %s |' % (nom, p['version'], lic, p.get('site', ''), os.path.basename(p['source']), p['source'], refs))
o.append('\nPackage archives and their SHA-256 are listed in `v2/outils/paquets.json`. / Les archives des paquets et leur empreinte SHA-256 sont dans `v2/outils/paquets.json`.\n')
o.append('Notes: where a component is offered under several licenses (for example libidn2 and libunistring: GPL or LGPL), Claude-Sync uses it under the LGPL-3.0-or-later terms; '
         'the msys2-runtime is distributed under the terms described in its `CYGWIN_LICENSE` file (LGPL-3.0-or-later with the Cygwin exceptions). '
         'This list is informational and is not legal advice. / '
         'Quand un composant est propose sous plusieurs licences (libidn2, libunistring : GPL ou LGPL), Claude-Sync l\'utilise selon la LGPL-3.0-or-later ; '
         'le runtime msys2 suit son fichier `CYGWIN_LICENSE`. Cette liste est informative et ne constitue pas un avis juridique.\n')
o.append('## License texts / Textes de licence\n')
for sha, (txt, users) in textes.items():
    i = num[sha]
    o.append('<a id="text-%d"></a>\n### Text %d — used by: %s\n' % (i, i, ', '.join('%s (%s)' % (n, f) for n, f in users)))
    o.append('```text\n' + txt.strip('\n') + '\n```\n')
out = '\n'.join(o)
for dest in (os.path.join(racine, 'THIRD-PARTY-NOTICES.md'), os.path.join(lic_dir, 'THIRD-PARTY-NOTICES.md')):
    open(dest, 'w', encoding='utf-8', newline='\n').write(out)
print('THIRD-PARTY-NOTICES.md : %d paquets, %d textes distincts, %d Ko' % (len(data['paquets']), len(textes), len(out) // 1024))
