#!/usr/bin/env python3
"""Local verification harness for the LexiForge balance model.

The C# simulator (Program.cs) is CANONICAL and links the exact pure-C# formulas
that ship in the Godot game. This script is a faithful Python port used only when
Windows Smart App Control / WDAC blocks execution of unsigned locally-built .NET
binaries (error 0x800711C7). It reads the same design/balance_config.json and
reproduces the same outputs so numbers can still be validated in that environment.

    python verify_local.py [config.json] [players] [days]
"""
import json
import math
import os
import random
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_CFG = os.path.normpath(os.path.join(HERE, "..", "..", "..", "design", "balance_config.json"))


def clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v


class Model:
    def __init__(self, cfg):
        d = cfg["difficulty"]
        s, w, p = d["shape"], d["weights"], d["parTime"]
        e, ec = cfg["economy"], cfg["economy"]["coins"]
        f = ec["flow"]
        h = cfg["heat"]
        a, g, rev = cfg["ads"], cfg["ads"]["global"], cfg["ads"]["revenueAssumptions"]
        k = cfg["kpis"]
        atlas = e["sinks"]["atlasScene"]

        self.shape = dict(decay=s["decayForgeConstant"], wb=s["wordCount"]["base"], ws=s["wordCount"]["scale"],
                          bb=s["bankSize"]["base"], bs=s["bankSize"]["scale"], be=s["bankSize"]["exponent"],
                          ab=s["avgWordLength"]["base"], as_=s["avgWordLength"]["scale"],
                          rb=s["rarity"]["base"], rs=s["rarity"]["scale"],
                          bn=d["breatherEveryNForge"], bp=d["breatherWordPenalty"], bm=d["breatherRarityMultiplier"])
        self.w = dict(pw=w["perExtraWord"], pb=w["perExtraBankLetter"], pa=w["perExtraAvgLength"],
                      rr=w["rarity"], bonus=d["breatherBonus"], lo=d["minDI"], hi=d["maxDI"])
        self.par = dict(b=p["base"], l=p["linear"], q=p["quadratic"], cap=p["capSeconds"])
        self.ec = dict(b=ec["base"], pw=ec["perSolutionWord"], pbw=ec["perBonusWord"],
                       bcap=ec["bonusWordCap"], flowstep=f["stepPerCleanFind"], flowcap=f["capMultiplier"],
                       hb=ec["hintCostBase"], hs=ec["hintCostStep"], maxh=ec["maxHintsPerForge"],
                       s2=ec["starBonus"]["2"], s3=ec["starBonus"]["3"])
        self.heat = dict(b=h["gainBase"], di=h["gainPerDI"], over=h["gainPerParOverrun"],
                         perfect=h["coolOnPerfectForge"], mx=h["max"], maxf=h["maxForgesPerRun"])
        self.apol = dict(firstF=g["firstInterstitialForge"], firstS=g["firstInterstitialSession"],
                         every=g["interstitialEveryNForges"], gap=g["interstitialMinGapSeconds"],
                         maxday=g["interstitialMaxPerDay"], suppress=g["suppressInterstitialWithinSecondsOfRewarded"],
                         softcap=g["rewardedSoftCapPerUserPerDay"], aoMinS=g["appOpenMinSessions"],
                         aoGap=g["appOpenMinGapSeconds"], aoMax=g["appOpenMaxPerDay"])
        self.runes = [(r["id"], r["rarity"], r["coinDeltaPct"], r["diDelta"]) for r in cfg["runes"]]
        self.maxbuild = cfg["runeDraft"]["maxBuildCoinDeltaPct"]
        self.ecpm = (rev["rewardedEcpmUsd"], rev["interstitialEcpmUsd"], rev["appOpenEcpmUsd"])
        self.optin = rev["rewardedOptInRate"]
        self.iap = (rev["forgePassConversion"], rev["forgePassPriceUsd"])
        self.sessions = k["sessionsPerDay"]
        self.forges_per_session = k["medianForgesPerSession"]
        self.target_hints = k["hintsPerForge"]
        self.atlas = (atlas["base"], atlas["step"], atlas["max"])
        self.version = cfg["configVersion"]

    def shape_for(self, idx):
        s = self.shape
        idx = max(1, idx)
        t = 1.0 - math.exp(-idx / s["decay"])
        words = int(round(s["wb"] + s["ws"] * t))
        bank = int(round(s["bb"] + s["bs"] * t ** s["be"]))
        avg = s["ab"] + s["as_"] * t
        rarity = s["rb"] + s["rs"] * t
        breather = idx % s["bn"] == 0
        if breather:
            words = max(3, words - s["bp"])
            rarity *= s["bm"]
        return words, bank, avg, rarity, breather

    def di(self, words, bank, avg, rarity, breather):
        w = self.w
        raw = (w["pw"] * (words - 3) + w["pb"] * (bank - 3)
               + w["pa"] * (avg - 3.0) + w["rr"] * rarity
               + (w["bonus"] if breather else 0.0))
        return clamp(raw, w["lo"], w["hi"])

    def par_time(self, di):
        p = self.par
        return min(p["cap"], p["b"] + p["l"] * di + p["q"] * di * di)

    def flow(self, streak):
        e = self.ec
        return 1.0 + min(e["flowcap"] - 1.0, max(0, streak) * e["flowstep"])

    def level_coins(self, words, bonus, flow, build):
        e = self.ec
        bonus = min(max(0, bonus), e["bcap"]) * e["pbw"]
        return max(0, round((e["b"] + e["pw"] * words + bonus) * clamp(flow, 1.0, e["flowcap"]) * (1.0 + build)))

    def hint_cost(self, n):
        e = self.ec
        return e["hb"] + e["hs"] * max(0, n - 1)

    @staticmethod
    def stars(hints, wrong, seconds, par):
        if hints == 0 and seconds <= par and wrong <= 2:
            return 3
        if hints <= 1 and seconds <= par * 1.5:
            return 2
        return 1

    def heat_next(self, cur, di, ratio, perfect):
        h = self.heat
        gain = h["b"] + h["di"] * di + h["over"] * min(1.0, max(0.0, ratio - 1.0)) - (h["perfect"] if perfect else 0.0)
        return clamp(cur + gain, 0.0, h["mx"])

    def can_interstitial(self, st, now):
        c = self.apol
        if st["pass"]:
            return "forge_pass"
        if st["forges"] < c["firstF"]:
            return "new_player_grace"
        if st["sessions"] < c["firstS"]:
            return "first_session"
        if st["in_run"]:
            return "during_run"
        if st["abandoned"]:
            return "after_abandon"
        if st["ints_today"] >= c["maxday"]:
            return "daily_cap"
        if st["forges_since_int"] < c["every"]:
            return "forge_frequency"
        if now - st["last_int"] < c["gap"]:
            return "cooldown"
        if now - st["last_rew"] < c["suppress"]:
            return "near_rewarded"
        return "ok"

    def can_app_open(self, st, now):
        c = self.apol
        if st["pass"] or st["sessions"] < c["aoMinS"] or st["in_run"]:
            return False
        if st["ao_today"] >= c["aoMax"] or now - st["last_ao"] < c["aoGap"]:
            return False
        return True


class Agg:
    def __init__(self):
        self.d = defaultdict(float)
        self.denials = defaultdict(int)

    def add(self, key, amount=1):
        self.d[key] += amount


def poisson(rng, lam):
    if lam <= 0:
        return 0
    limit, prod, k = math.exp(-lam), rng.random(), 0
    while prod > limit and k < 30:
        k += 1
        prod *= rng.random()
    return k


def gauss(rng, mean, sd):
    return mean + sd * math.sqrt(-2.0 * math.log(max(1e-9, rng.random()))) * math.cos(2.0 * math.pi * rng.random())


def draft(m, rng):
    offer = []
    for _ in range(3):
        roll = rng.random()
        rarity = "common" if roll < 0.60 else "uncommon" if roll < 0.90 else "rare"
        pool = [r for r in m.runes if r[1] == rarity]
        offer.append(pool[rng.randrange(len(pool))])
    if all(r[2] > 0 for r in offer):
        offer[2] = next(r for r in m.runes if r[2] <= 0)
    return max(offer, key=lambda r: r[2]) if rng.random() < 0.70 else offer[rng.randrange(3)]


def simulate(m, rng, days, agg):
    skill = clamp(gauss(rng, 0.5, 0.18), 0.05, 0.98)
    # Player-level segment: only rewardedOptInRate of DAU ever opts into ads.
    ad_affinity = rng.random() ** 0.8 if rng.random() < m.optin else 0.0
    st = dict(pass_=False, forges=0, sessions=0, last_int=-10**9, last_rew=-10**9, last_ao=-10**9,
              ints_today=0, rew_today=0, ao_today=0, marks_today=0,
              forges_since_int=0, in_run=False, abandoned=False)
    st["pass"] = rng.random() < 0.02
    coins, marks, atlas_i = 300, 0, 0
    now = 1_800_000_000
    heat, build_coin, build_di, forges_in_run = 0.0, 0.0, 0.0, 0
    C = m.apol

    def rewarded(key):
        """Every rewarded ad mints 1 Sponsor Mark (cap 6/day, +1 bonus on 3rd ad)."""
        nonlocal marks
        agg.add(key)
        st["rew_today"] += 1
        st["last_rew"] = now
        if st["marks_today"] < 6:
            marks += 1
            agg.add("marks_earned")
            st["marks_today"] += 1
            if st["rew_today"] == 3 and st["marks_today"] < 6:
                marks += 1
                agg.add("marks_earned")
                st["marks_today"] += 1

    for _day in range(days):
        st["ints_today"] = st["rew_today"] = st["ao_today"] = st["marks_today"] = 0
        n_sessions = clamp(round(gauss(rng, m.sessions, 0.9)), 1, 6)
        for _s in range(n_sessions):
            st["sessions"] += 1
            now += 4 * 3600
            if m.can_app_open(st, now):
                st["ao_today"] += 1
                st["last_ao"] = now
                agg.add("appopen")
            n_forges = clamp(round(gauss(rng, m.forges_per_session, 1.2)), 1, 8)
            for _f in range(n_forges):
                st["forges"] += 1
                st["forges_since_int"] += 1
                st["in_run"] = True
                forges_in_run += 1
                words, bank, avg, rarity, breather = m.shape_for(st["forges"])
                di = max(m.w["lo"], m.di(words, bank, avg, rarity, breather) + build_di)
                par = m.par_time(di)

                if forges_in_run in (1, 3, 5):
                    pick = draft(m, rng)
                    build_coin = min(m.maxbuild, build_coin + pick[2])
                    build_di = max(-12.0, build_di + pick[3])

                ratio = clamp(0.75 + 0.55 * (1 - skill) + 0.13 * gauss(rng, 0, 1)
                              - 0.02 * max(0.0, -build_di), 0.60, 2.20)
                solve = par * ratio
                now += int(round(solve + 18))
                wrong = min(6, poisson(rng, 0.35 + 0.018 * di * (1 - skill)))
                flow = m.flow(max(0, words - wrong))
                bonus_words = min(m.ec["bcap"], poisson(rng, 1.0 + 2.0 * skill))

                hint_p = clamp(0.06 + 0.006 * di + 0.30 * (1 - skill) - 0.015 * max(0.0, -build_di), 0, 0.80)
                hints = 1 + (1 if rng.random() < hint_p * 0.35 else 0) if rng.random() < hint_p else 0
                hints = min(hints, m.ec["maxh"])
                agg.add("hints", hints)

                hint_ads = 0
                for h in range(1, hints + 1):
                    if hint_ads < 2 and st["rew_today"] < C["softcap"] and rng.random() < ad_affinity * 0.60:
                        hint_ads += 1
                        rewarded("hint_ads")
                    else:
                        cost = m.hint_cost(h)
                        if coins >= cost:
                            coins -= cost
                            agg.add("spent", cost)

                stars = m.stars(hints, wrong, solve, par)
                agg.add("stars", stars)
                if stars == 3:
                    agg.add("perfect")
                perfect = stars == 3
                earned = m.level_coins(words, bonus_words, flow, build_coin) + (m.ec["s3"] if stars == 3 else m.ec["s2"] if stars == 2 else 0)

                if st["rew_today"] < C["softcap"] and rng.random() < ad_affinity * 0.22:
                    earned *= 2
                    rewarded("double_ads")

                coins += earned
                agg.add("earned", earned)
                agg.add("solve", solve)
                agg.add("ratio", ratio)
                agg.add("forges")
                # Voluntary quit mid-forge: a small base chance that rises with difficulty.
                quit_p = 0.015 * (1 + di / 50.0) + (0.20 if ratio > 2.0 else 0.0)
                if rng.random() < quit_p:
                    agg.add("abandoned")
                    st["abandoned"] = True
                else:
                    st["abandoned"] = False

                heat = m.heat_next(heat, di, ratio, perfect)
                run_ends = heat >= m.heat["mx"] or forges_in_run >= m.heat["maxf"]
                if run_ends:
                    agg.add("runs")
                    agg.add("forges_in_runs", forges_in_run)
                    agg.add("build", build_coin)
                    st["in_run"] = False

                    # Patron Board sinks: reroll, weekly Gilded Forge, cosmetics.
                    if marks >= 1 and rng.random() < 0.28:
                        marks -= 1
                        agg.add("marks_spent")
                    if marks >= 3 and _day % 7 == 6 and rng.random() < 0.55:
                        marks -= 3
                        agg.add("marks_spent", 3)
                    if marks >= 8 and rng.random() < 0.06:
                        marks -= 8
                        agg.add("marks_spent", 8)

                    if st["rew_today"] < C["softcap"] and st["marks_today"] < 6 and rng.random() < ad_affinity * 0.20:
                        rewarded("mark_ads")

                    cooled = False
                    if st["rew_today"] < C["softcap"] and rng.random() < ad_affinity * 0.10:
                        rewarded("cool_ads")
                        cooled = True
                    elif rng.random() < 0.12 and marks > 0:
                        marks -= 1
                        agg.add("marks_spent")
                        cooled = True
                    elif rng.random() < 0.10 and coins >= 150:
                        coins -= 150
                        agg.add("spent", 150)
                        cooled = True

                    why = m.can_interstitial(st, now)
                    if why == "ok":
                        agg.add("ints")
                        st["ints_today"] += 1
                        st["last_int"] = now
                        st["forges_since_int"] = 0
                    else:
                        agg.denials[why] += 1

                    heat = max(0, heat - 25) if cooled else 0
                    forges_in_run = 0
                    build_coin = build_di = 0.0

            if _day > 0 and st["rew_today"] < C["softcap"] and rng.random() < ad_affinity * 0.18:
                rewarded("freeze_gift_ads")
                coins += 180
                agg.add("earned", 180)

        bought = 0
        while coins > 900 and bought < 3:
            cost = min(m.atlas[2], m.atlas[0] + m.atlas[1] * (atlas_i % 9))
            if coins < cost:
                break
            coins -= cost
            agg.add("spent", cost)
            atlas_i += 1
            bought += 1
        now += 8 * 3600


def main():
    cfg_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CFG
    players = int(sys.argv[2]) if len(sys.argv) > 2 else 10000
    days = int(sys.argv[3]) if len(sys.argv) > 3 else 30
    with open(cfg_path, encoding="utf-8") as fh:
        cfg = json.load(fh)
    m = Model(cfg)
    rng = random.Random(20261111)
    agg = Agg()

    print(f"LexiForge balance verification | config={m.version} | players={players:,} | days={days} | seed=20261111")
    print("=" * 96)
    print("   Forge  Words   Bank   AvgLen   Rarity       DI      Par    Coins")
    for i in (1, 12, 22, 52, 102, 202, 302, 402):
        words, bank, avg, rarity, breather = m.shape_for(i)
        di = m.di(words, bank, avg, rarity, breather)
        print(f"{i:8d} {words:6d} {bank:5d} {avg:8.2f} {rarity:8.2f} {di:7.1f} {m.par_time(di):8.1f} {m.level_coins(words, 0, 1.6, 0):7d}")
    print("-" * 96)

    for _ in range(players):
        simulate(m, rng, days, agg)

    d, pd = agg.d, players * days
    rew = d["hint_ads"] + d["double_ads"] + d["mark_ads"] + d["cool_ads"] + d["freeze_gift_ads"]
    arpdau = (rew / pd * m.ecpm[0] / 1000 + d["ints"] / pd * m.ecpm[1] / 1000
              + d["appopen"] / pd * m.ecpm[2] / 1000 + m.iap[0] * m.iap[1] / 30)
    rows = [
        ("Forges / user / day", d["forges"] / pd, "3.6 per session (x 2.6 sessions)"),
        ("Solve time (s)", d["solve"] / d["forges"], "vs par"),
        ("Solve / par", d["ratio"] / d["forges"], "0.9 - 1.3 target"),
        ("Abandon rate", d["abandoned"] / d["forges"], "< 0.08 target"),
        ("Hints / forge", d["hints"] / d["forges"], f"{m.target_hints} target"),
        ("Avg stars", d["stars"] / d["forges"], "1 - 3"),
        ("Coins earned / day", d["earned"] / pd, "9.4 forges/day x ~200 coins"),
        ("Coins spent / day", d["spent"] / pd, "hints + Atlas"),
        ("Net coins / day", (d["earned"] - d["spent"]) / pd, "watch for inflation"),
        ("Rewarded ads / user / day", rew / pd, "per DAU"),
        ("Rewarded ads / engaged user", rew / (pd * m.optin), "3.2 target"),
        ("Interstitial ads / user / day", d["ints"] / pd, "<= 2.8 cap"),
        ("App-open ads / user / day", d["appopen"] / pd, "<= 1.0 cap"),
        ("Marks earned / day", d["marks_earned"] / pd, "rewarded + free"),
        ("Marks spent / day", d["marks_spent"] / pd, "sinks active"),
        ("Run length (forges)", d["forges_in_runs"] / d["runs"], "5 - 7 design"),
        ("Avg rune build delta", d["build"] / d["runs"], "<= 0.30 cap"),
        ("Projected ARPDAU (USD)", arpdau, "$0.020 - 0.040 base target"),
    ]
    print("Projected behaviour (per user per day unless noted)")
    print("-" * 96)
    for label, value, note in rows:
        print(f"  {label:<42} {value:12.3f}   {note}")
    print(f"    hint {d['hint_ads']/pd:.2f} | double {d['double_ads']/pd:.2f} | mark {d['mark_ads']/pd:.2f} | cool {d['cool_ads']/pd:.2f} | freeze/gift/refill {d['freeze_gift_ads']/pd:.2f}")
    print("-" * 96)
    print("Interstitial denial reasons (validates suppression rules)")
    for reason, count in sorted(agg.denials.items(), key=lambda x: -x[1]):
        print(f"  {reason:<24} {count:>10,}")
    print("-" * 96)
    print("Retention (D1/D7/D30) deliberately NOT simulated - measure in soft launch.")

    out = os.path.join(os.path.dirname(os.path.abspath(cfg_path)), "difficulty_curve.csv")
    with open(out, "w", encoding="utf-8") as fh:
        fh.write("forge,words,bank,avg_word_len,rarity,difficulty_index,par_seconds,coins_at_flow_1_6\n")
        for i in range(1, 401):
            words, bank, avg, rarity, breather = m.shape_for(i)
            di = m.di(words, bank, avg, rarity, breather)
            fh.write(f"{i},{words},{bank},{avg:.3f},{rarity:.3f},{di:.2f},{m.par_time(di):.1f},{m.level_coins(words, 0, 1.6, 0)}\n")
    print(f"Wrote 400-forge curve -> {out}")


if __name__ == "__main__":
    main()
