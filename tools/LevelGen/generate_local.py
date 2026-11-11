#!/usr/bin/env python3
"""Windows App Control fallback for the C# LevelGen tool.

The C# tool (Program.cs) is CANONICAL and runs in CI. This is a faithful Python
port used when WDAC / Smart App Control blocks unsigned locally-built .NET
binaries (error 0x800711C7) -- the same arrangement as BalanceSim/verify_local.py.

    python generate_local.py [repoRoot] [forgeCount]

Both pipelines read the SAME design/balance_config.json and
design/seed_dictionary.json and enforce the SAME content contract. Their seeded
RNGs differ, so byte-identical output is not expected; both outputs are valid.

Emits godot/data/forges/pack_01.json, godot/data/words_en.dat and copies the
balance config into res://data.
"""
import hashlib
import json
import math
import os
import random
import shutil
import struct
import sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
MAX_ATTEMPTS = 4000
MIN_WORD_LENGTH = 3


# --------------------------------------------------------------------- helpers
def clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v


def is_constructible(word, bank):
    """A word consumes each bank tile at most once; '*' is one wild tile."""
    counts = Counter(c for c in bank if c != "*")
    wilds = bank.count("*")
    for c in word:
        if not ("a" <= c <= "z"):
            return False
        if counts[c] > 0:
            counts[c] -= 1
        elif wilds > 0:
            wilds -= 1
        else:
            return False
    return True


def shape_for(index, cfg):
    d = cfg["difficulty"]
    s = d["shape"]
    index = max(1, index)
    t = 1.0 - math.exp(-index / s["decayForgeConstant"])
    words = round(s["wordCount"]["base"] + s["wordCount"]["scale"] * t)
    bank = round(s["bankSize"]["base"] + s["bankSize"]["scale"] * t ** s["bankSize"]["exponent"])
    avg = s["avgWordLength"]["base"] + s["avgWordLength"]["scale"] * t
    rarity = s["rarity"]["base"] + s["rarity"]["scale"] * t
    every = d["breatherEveryNForge"]
    breather = every > 0 and index % every == 0
    if breather:
        words = max(3, words - d["breatherWordPenalty"])
        rarity *= d["breatherRarityMultiplier"]
    return words, bank, avg, rarity, breather


def di_from_content(words, bank, common, cfg, breather):
    d = cfg["difficulty"]
    w = d["weights"]
    avg = sum(len(x) for x in words) / len(words) if words else 3.0
    rarity = sum(1 for x in words if x not in common) / len(words) if words else 0.0
    raw = (w["perExtraWord"] * (len(words) - 3)
           + w["perExtraBankLetter"] * (len(bank) - 3)
           + w["perExtraAvgLength"] * (avg - 3.0)
           + w["rarity"] * rarity
           + (d["breatherBonus"] if breather else 0.0))
    return clamp(raw, d["minDI"], d["maxDI"])


def par_time(di, cfg):
    p = cfg["difficulty"]["parTime"]
    return min(p["capSeconds"], p["base"] + p["linear"] * di + p["quadratic"] * di * di)


def band_for(forge_id, cfg):
    for p in cfg["difficulty"]["packs"]:
        if p["firstForge"] <= forge_id <= p["lastForge"]:
            return p
    return None


# ---------------------------------------------------------------- trie image
def build_trie(words):
    edges = [{}]
    terminal = [False]
    for word in sorted(words):
        node = 0
        for ch in word:
            if ch not in edges[node]:
                edges[node][ch] = len(edges)
                edges.append({})
                terminal.append(False)
            node = edges[node][ch]
        terminal[node] = True
    return edges, terminal


def save_trie(path, edges, terminal):
    with open(path, "wb") as f:
        f.write(struct.pack("<i", len(edges)))
        for e in edges:
            f.write(struct.pack("<i", len(e)))
            for ch, target in e.items():
                f.write(bytes([ord(ch)]))
                f.write(struct.pack("<i", target))
        for t in terminal:
            f.write(b"\x01" if t else b"\x00")


# --------------------------------------------------------------- grid layout
def slot_cells(slot):
    word, row, col, vertical = slot["word"], slot["row"], slot["column"], slot["direction"] == "v"
    return [((row + i, col) if vertical else (row, col + i), word[i]) for i in range(len(word))]


def intersection_count(slots):
    occupied = {}
    for slot in slots:
        for cell, letter in slot_cells(slot):
            occupied.setdefault(cell, []).append(letter)
    return sum(1 for letters in occupied.values()
               if len(letters) > 1 and len(set(letters)) == 1)


def build_grid(words, keystone, min_intersections, rng):
    occupied = {}          # (row, col) -> [(letter, is_vertical), ...]
    placed = []            # (word, row, col, vertical)

    def place(word, row, col, vertical):
        placed.append((word, row, col, vertical))
        for i, ch in enumerate(word):
            cell = (row + i, col) if vertical else (row, col + i)
            occupied.setdefault(cell, []).append((ch, vertical))

    place(keystone, 0, 0, False)

    others = [w for w in words if w != keystone]
    rng.shuffle(others)
    for word in others:
        best = None
        directions = [False, True]
        rng.shuffle(directions)
        for vertical in directions:
            for k, anchor_letter in enumerate(word):
                for (ar, ac), letters in occupied.items():
                    if not any(ch == anchor_letter for ch, _ in letters):
                        continue
                    row = ar - k if vertical else ar
                    col = ac if vertical else ac - k
                    perpendicular = parallel = 0
                    conflict = False
                    for i, ch in enumerate(word):
                        cell = (row + i, col) if vertical else (row, col + i)
                        if cell not in occupied:
                            continue
                        for other_ch, other_vertical in occupied[cell]:
                            if other_ch != ch:
                                conflict = True
                                break
                            if other_vertical != vertical:
                                perpendicular += 1
                            else:
                                parallel += 1
                        if conflict:
                            break
                    if conflict or parallel > 0 or perpendicular <= 0:
                        continue
                    candidate = (row, col, vertical, perpendicular)
                    if best is None or candidate[3] > best[3] or (candidate[3] == best[3] and rng.random() < 0.3):
                        best = candidate
        if best is not None:
            place(word, best[0], best[1], best[2])
        else:
            next_row = max((r for r, _ in occupied), default=0) + 2
            place(word, next_row, 0, False)

    min_row = min(p[1] for p in placed)
    min_col = min(p[2] for p in placed)
    slots = [{"word": w, "row": r - min_row, "column": c - min_col,
              "direction": "v" if vertical else "h", "isKeystone": w == keystone}
             for (w, r, c, vertical) in placed]
    return slots if intersection_count(slots) >= min_intersections else None


# ------------------------------------------------------------------ checksum
def checksum(forge):
    parts = [
        str(forge["forgeId"]),
        forge["theme"],
        forge["letterBank"],
        ",".join(forge["solutionWords"]),
        forge["keystoneWord"],
        ";".join(f"{s['word']}:{s['row']}:{s['column']}:{s['direction']}:{1 if s['isKeystone'] else 0}"
                 for s in forge["slots"]),
        str(forge["seed"]),
    ]
    return hashlib.sha256("|".join(parts).encode("utf-8")).hexdigest()[:16]


# ----------------------------------------------------------------- validation
def validate(forge, cfg, all_words, common, blocklist):
    errors = []
    if forge["checksum"] != checksum(forge):
        errors.append("checksum mismatch")
    bank = forge["letterBank"]
    if not (3 <= len(bank) <= 7) or any(not ("a" <= c <= "z") for c in bank):
        errors.append("invalid bank")
    if len(set(bank)) != len(bank):
        errors.append("duplicate bank tiles")

    words = forge["solutionWords"]
    contract = cfg["difficulty"]["contentContract"]
    if len(words) < 3:
        errors.append("fewer than 3 solution words")
    if len(set(words)) != len(words):
        errors.append("duplicate solution words")
    for w in words:
        if len(w) < MIN_WORD_LENGTH or any(not ("a" <= c <= "z") for c in w):
            errors.append(f"invalid word {w}")
        if w in blocklist:
            errors.append(f"blocklisted {w}")
        if w not in all_words:
            errors.append(f"{w} not in dictionary")
        if not is_constructible(w, bank):
            errors.append(f"{w} not constructible from {bank}")

    if sum(1 for w in words if len(w) <= 4) < contract["minShortWordsLengthLTE4"]:
        errors.append("not enough gimme words")
    keystone = forge["keystoneWord"]
    if not words or words[-1] != keystone:
        errors.append("keystone is not last")
    others = [w for w in words if w != keystone]
    if others and len(keystone) <= max(len(w) for w in others):
        errors.append("keystone is not uniquely longest")
    if sum(1 for w in words if len(w) == len(keystone)) != 1:
        errors.append("keystone length tie")

    slots = forge["slots"]
    if len(slots) != len(words):
        errors.append("slot count != word count")
    occupied = {}
    for s in slots:
        if s["word"] not in words:
            errors.append(f"slot word {s['word']} not a solution")
        if s["row"] < 0 or s["column"] < 0:
            errors.append("negative slot origin")
        for cell, letter in slot_cells(s):
            occupied.setdefault(cell, []).append(letter)
    if any(len(set(letters)) > 1 for letters in occupied.values()):
        errors.append("conflicting intersection letters")
    if intersection_count(slots) < contract["minGridIntersections"]:
        errors.append(f"only {intersection_count(slots)} intersections")
    if sum(1 for s in slots if s["isKeystone"]) != contract["keystoneWords"]:
        errors.append("keystone slot flag count")
    ks = next((s for s in slots if s["isKeystone"]), None)
    if ks and ks["word"] != keystone:
        errors.append("keystone flag on wrong slot")

    breather = forge["forgeId"] % cfg["difficulty"]["breatherEveryNForge"] == 0
    di = di_from_content(words, bank, common, cfg, breather)
    if abs(di - forge["difficultyIndex"]) > 0.02:
        errors.append(f"DI {forge['difficultyIndex']:.2f} != content {di:.2f}")
    band = band_for(forge["forgeId"], cfg)
    if band and not (band["band"][0] - 0.001 <= forge["difficultyIndex"] <= band["band"][1] + 0.001):
        errors.append(f"DI {forge['difficultyIndex']:.2f} outside pack band")
    if abs(par_time(forge["difficultyIndex"], cfg) - forge["parTimeSeconds"]) > 0.06:
        errors.append("parTime mismatch")

    # Independent solver: re-derive every constructible word from the bank.
    reachable = {w for w in all_words if len(w) >= MIN_WORD_LENGTH and is_constructible(w, bank)}
    for w in words:
        if w not in reachable:
            errors.append(f"independent solver: {w} unreachable")
    if len(reachable) - len(words) < 2:
        errors.append("independent solver: too few bonus words")
    return errors


# ------------------------------------------------------------------ generator
def generate(forge_id, cfg, all_words, common, blocklist, families):
    target_words, _bank, _avg, _rarity, breather = shape_for(forge_id, cfg)
    target_words = clamp(target_words, 3, 5)
    band = band_for(forge_id, cfg)
    contract = cfg["difficulty"]["contentContract"]

    for attempt in range(MAX_ATTEMPTS):
        family = families[(forge_id * 7 + attempt) % len(families)]
        bank, theme = family["letters"], family["theme"]
        rng = random.Random((forge_id * 1_000_003) ^ ((attempt + 1) * 9176))
        candidates = sorted({w for w in all_words
                             if len(w) >= MIN_WORD_LENGTH and w not in blocklist
                             and is_constructible(w, bank)})
        if len(candidates) < target_words + 2:
            continue

        max_len = max(len(w) for w in candidates)
        keystone_pool = [w for w in candidates if len(w) in (max_len, max_len - 1)
                         and sum(1 for x in candidates if len(x) < len(w)) >= target_words + 1]
        if not keystone_pool:
            continue
        rng.shuffle(keystone_pool)
        rare_pick = next((w for w in keystone_pool if w not in common and rng.random() < 0.35), None)
        keystone = rare_pick or rng.choice(keystone_pool)

        shorter = [w for w in candidates if len(w) < len(keystone) and w != keystone]
        if len(shorter) < target_words - 1:
            continue
        rng.shuffle(shorter)
        chosen = shorter[:target_words - 1]
        if sum(1 for w in chosen if len(w) <= 4) < contract["minShortWordsLengthLTE4"]:
            continue
        words = chosen + [keystone]

        di = di_from_content(words, bank, common, cfg, breather)
        if band and not (band["band"][0] - 0.001 <= di <= band["band"][1] + 0.001):
            continue

        slots = build_grid(words, keystone, contract["minGridIntersections"], rng)
        if slots is None:
            continue

        forge = {
            "forgeId": forge_id,
            "theme": theme,
            "letterBank": bank,
            "solutionWords": words,
            "keystoneWord": keystone,
            "slots": slots,
            "difficultyIndex": round(di, 10),
            "parTimeSeconds": round(par_time(di, cfg), 10),
            "seed": (forge_id * 7919) ^ attempt,
            "checksum": "",
        }
        forge["checksum"] = checksum(forge)
        if not validate(forge, cfg, all_words, common, blocklist):
            return forge
    return None


# ------------------------------------------------------------------------ main
def main():
    root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else DEFAULT_ROOT)
    count = int(sys.argv[2]) if len(sys.argv) > 2 else 20
    design = os.path.join(root, "design")
    data = os.path.join(root, "godot", "data")
    forges_dir = os.path.join(data, "forges")
    os.makedirs(forges_dir, exist_ok=True)

    with open(os.path.join(design, "balance_config.json"), encoding="utf-8") as fh:
        cfg = json.load(fh)
    with open(os.path.join(design, "seed_dictionary.json"), encoding="utf-8") as fh:
        seed = json.load(fh)

    families = seed["families"]
    rare = set(seed["rare"])
    blocklist = set(seed["blocklist"])
    all_words = {w for f in families for w in f["words"].split()
                 if len(w) >= MIN_WORD_LENGTH and all("a" <= c <= "z" for c in w)}
    common = all_words - rare

    print(f"LexiForge LevelGen (local Python fallback) | config={cfg['configVersion']} "
          f"| dictionary={len(all_words)} words | forges={count}")
    print("=" * 96)

    edges, terminal = build_trie(all_words)
    save_trie(os.path.join(data, "words_en.dat"), edges, terminal)
    shutil.copyfile(os.path.join(design, "balance_config.json"),
                    os.path.join(data, "balance_config.json"))

    forges = []
    for forge_id in range(1, count + 1):
        forge = generate(forge_id, cfg, all_words, common, blocklist, families)
        if forge is None:
            print(f"FATAL: could not generate a valid forge {forge_id}", file=sys.stderr)
            return 2
        errors = validate(forge, cfg, all_words, common, blocklist)
        if errors:
            print(f"FATAL: forge {forge_id}: {'; '.join(errors)}", file=sys.stderr)
            return 3
        forges.append(forge)
        print(f"  forge {forge_id:2}  bank={forge['letterBank']:<7} words={len(forge['solutionWords'])} "
              f"keystone={forge['keystoneWord']:<7} DI={forge['difficultyIndex']:5.2f} "
              f"par={forge['parTimeSeconds']:5.1f}s intersections={intersection_count(forge['slots'])} "
              f"checksum={forge['checksum']}")

    band = band_for(1, cfg)
    pack = {"packId": 1, "theme": band["theme"] if band else "Emberworks",
            "difficultyBand": band["band"] if band else [5, 18], "forges": forges}
    pack_path = os.path.join(forges_dir, "pack_01.json")
    with open(pack_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(pack, fh, ensure_ascii=False, indent=2)
        fh.write("\n")
    print("-" * 96)
    print(f"Wrote {len(forges)} forges -> {pack_path}")
    print(f"Wrote dictionary  -> {os.path.join(data, 'words_en.dat')} ({len(edges)} trie nodes)")
    print("All forges pass the content contract, checksum and independent solver.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
