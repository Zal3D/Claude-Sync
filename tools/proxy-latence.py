#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Relais TCP local qui simule une mauvaise liaison (ex. 4G via VPN) : delai ajoute dans chaque sens
et debit limite par sens. Sert uniquement aux essais de Claude-Sync V2 (127.0.0.1 -> serveur d'essai).

Usage : python -X utf8 proxy-latence.py --listen 2222 --target 192.0.2.10:22 \
            --delay-ms 75 --up-kbps 2000 --down-kbps 20000 [--stats-file compteurs.json]
  (delay-ms est applique DANS CHAQUE SENS : 75 -> 150 ms aller-retour ;
   up = PC -> serveur, down = serveur -> PC, en kilobits par seconde)
  --stats-file : ecrit {"up": octets, "down": octets, "connexions": n} toutes les 0,3 s (pour mesurer le volume reellement transmis)
"""
import argparse, asyncio, json, os, time


async def pipe(reader, writer, delay, kbps, tag, stats):
    q = asyncio.Queue()

    async def lire():
        try:
            while True:
                d = await reader.read(16384)
                if not d:
                    break
                await q.put((time.monotonic(), d))
        finally:
            await q.put(None)

    async def ecrire():
        libre = time.monotonic()                      # instant ou le "tuyau" est libre
        while True:
            it = await q.get()
            if it is None:
                break
            t_arrivee, d = it
            debut = max(t_arrivee + delay, libre)
            attente = debut - time.monotonic()
            if attente > 0:
                await asyncio.sleep(attente)
            libre = max(time.monotonic(), debut) + (len(d) * 8 / (kbps * 1000.0) if kbps else 0)
            try:
                writer.write(d)
                await writer.drain()
            except Exception:
                break
            stats[tag] += len(d)
            rest = libre - time.monotonic()
            if rest > 0:
                await asyncio.sleep(rest)
        try:
            writer.close()
        except Exception:
            pass

    await asyncio.gather(lire(), ecrire())


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--listen", type=int, default=2222)
    ap.add_argument("--target", default="127.0.0.1:22")
    ap.add_argument("--delay-ms", type=float, default=75)
    ap.add_argument("--up-kbps", type=float, default=2000)
    ap.add_argument("--down-kbps", type=float, default=20000)
    ap.add_argument("--stats-file", default="")
    a = ap.parse_args()
    host, port = a.target.rsplit(":", 1)
    stats = {"up": 0, "down": 0, "connexions": 0}

    async def client(r1, w1):
        stats["connexions"] += 1
        try:
            r2, w2 = await asyncio.open_connection(host, int(port))
        except Exception:
            w1.close()
            return
        await asyncio.gather(pipe(r1, w2, a.delay_ms / 1000, a.up_kbps, "up", stats),
                             pipe(r2, w1, a.delay_ms / 1000, a.down_kbps, "down", stats))

    srv = await asyncio.start_server(client, "127.0.0.1", a.listen)
    print("relais pret : 127.0.0.1:%d -> %s | %g ms/sens | montant %g kbit/s | descendant %g kbit/s"
          % (a.listen, a.target, a.delay_ms, a.up_kbps, a.down_kbps), flush=True)

    async def compteurs():
        while a.stats_file:
            await asyncio.sleep(0.3)
            try:
                tmp = a.stats_file + ".tmp"
                with open(tmp, "w") as f:
                    json.dump(stats, f)
                os.replace(tmp, a.stats_file)
            except Exception:
                pass
    await asyncio.gather(srv.serve_forever(), compteurs())


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
