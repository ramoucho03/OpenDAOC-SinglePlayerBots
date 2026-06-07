#!/usr/bin/env python3
"""Generate digit-free, realm-flavoured "gamer handle" pseudonyms for mimics.

Produces N unique handles per realm (Albion / Hibernia / Midgard) that look
like a live PvP server population (xXReaperXx, GrimGawain, OdinPro, ...) but
contain NO digits, then emits them as ready-to-paste C# array entries matching
the formatting of MimicManager.cs (12-space indent, 6 entries per line).

Existing entries already present in MimicManager.cs are parsed out and excluded
so we never emit an exact duplicate.

Usage:
    python scripts/gen_gamer_names.py            # writes scripts/gamer_names_<realm>.txt
"""

import os
import random
import re

HERE = os.path.dirname(os.path.abspath(__file__))
CS_FILE = os.path.join(HERE, "..", "GameServer", "custom", "MimicNPC", "MimicManager.cs")

PER_REALM = 500
MAX_LEN = 15          # keep handles short enough for the name field
SEED = 20260606       # deterministic output

# --- Generic, realm-agnostic building blocks (no digits) --------------------

GEN_PREFIX = [
    "x", "i", "Da", "The", "Mr", "Lil", "Big", "Real", "Only", "Just", "Its",
    "Mega", "Ultra", "Epic", "Wild", "Savage", "Grim", "Dark", "Holy", "Noble",
    "Sacred", "Iron", "Golden", "Frost", "Crimson", "Shadow", "Lethal", "Fatal",
    "Brutal", "Rabid", "Hyper", "Cold", "Storm", "Lunar", "Solar", "Void",
    "Obsidian", "Sable", "Vile", "Toxic", "Numb", "Mossy", "Misty", "Frozen",
    "Verdant", "Silent", "Rapid", "Feral", "Ashen", "Onyx",
]

GEN_CORE = [
    "Reaper", "Slayer", "Striker", "Hawk", "Wolf", "Fang", "Talon", "Serpent",
    "Viper", "Dragon", "Hydra", "Kraken", "Phoenix", "Cobra", "Golem", "Ghost",
    "Wraith", "Specter", "Phantom", "Shade", "Reaver", "Blade", "Edge", "Razor",
    "Bolt", "Surge", "Flux", "Pulse", "Nova", "Eclipse", "Zenith", "Vortex",
    "Comet", "Cinder", "Ember", "Flame", "Blaze", "Tempest", "Riptide",
    "Maelstrom", "Doom", "Bane", "Rage", "Fury", "Havoc", "Mauler", "Breaker",
    "Crawler", "Stalker", "Hunter", "Sniper", "Assassin", "Ninja", "Saber",
    "Skull", "Abyss", "Plasma", "Cyber", "Glitch", "Echo", "Wisp", "Helix",
    "Titan", "Beast", "Lion", "Tiger", "Shark", "Raven", "Fox", "Drifter",
    "Rogue", "Warden", "Lancer", "Smasher", "Render",
]

GEN_TAG = ["GG", "OG", "HD", "TTV", "YT", "Pro", "God", "Lord", "King", "OP", "X", "Z"]

# --- Realm-flavoured cores --------------------------------------------------

ALB_CORE = [
    "Camelot", "Avalon", "Logres", "Pendragon", "Excalibur", "Gawain", "Galahad",
    "Percival", "Lancelot", "Mordred", "Merlin", "Arthur", "Bedivere", "Pellinore",
    "Tristan", "Grail", "Camlann", "Albion", "Briton", "Saxon", "Templar",
    "Paladin", "Crusader", "Knight", "Cleric", "Sorcerer", "Theurg", "Armsman",
    "Reaver", "Friar", "Crown", "Throne", "Lionheart",
]

HIB_CORE = [
    "Tara", "Tuatha", "Danu", "Dagda", "Sidhe", "Banshee", "Brehon", "Gael",
    "Celt", "Fianna", "Cuchu", "Lugh", "Erin", "Niamh", "Clover", "Shamrock",
    "Grove", "Forest", "Oak", "Willow", "Briar", "Thorn", "Leaf", "Faerie",
    "Fae", "Emerald", "Verdant", "Druid", "Bard", "Animist", "Mentalist",
    "Eldritch", "Stag", "Nightshade", "Mistral",
]

MID_CORE = [
    "Odin", "Thor", "Loki", "Tyr", "Vidar", "Heimdall", "Ymir", "Surtr",
    "Fenrir", "Garm", "Sleipnir", "Mjolnir", "Bifrost", "Valkyr", "Niflheim",
    "Jotun", "Draugr", "Einherjar", "Ragnar", "Viking", "Norse", "Midgard",
    "Thane", "Skald", "Runemaster", "Bonedancer", "Spiritmaster", "Warrior",
    "Berserk", "Rune", "Frostbite", "Valhalla", "Asgard", "Aesir",
]

REALMS = {
    "alb": ("_albGamer", ALB_CORE),
    "hib": ("_hibGamer", HIB_CORE),
    "mid": ("_midGamer", MID_CORE),
}


def parse_existing(text, array_field):
    """Return the set of string literals already in `array_field` of the C# file."""
    m = re.search(re.escape(array_field) + r"\s*=\s*\{(.*?)\};", text, re.S)
    if not m:
        return set()
    return set(re.findall(r'"([^"]+)"', m.group(1)))


def gen_for_realm(realm_core, exclude, rng):
    cores = GEN_CORE + realm_core
    candidates = set()

    def add(name):
        if len(name) <= MAX_LEN and not any(c.isdigit() for c in name):
            candidates.add(name)

    # Pattern 1: prefix + core
    for p in GEN_PREFIX:
        for c in cores:
            add(p + c)
    # Pattern 2: realm core + generic core
    for rc in realm_core:
        for c in GEN_CORE:
            add(rc + c)
    # Pattern 3: realm/generic core + tag
    for c in cores:
        for t in GEN_TAG:
            add(c + t)
    # Pattern 4: xX...Xx wrap
    for c in cores:
        add("xX" + c + "Xx")
    # Pattern 5: prefix + core + tag (a sampled subset, this space is huge)
    combos = [(p, c, t) for p in GEN_PREFIX for c in cores for t in GEN_TAG]
    rng.shuffle(combos)
    for p, c, t in combos[:4000]:
        add(p + c + t)

    pool = sorted(candidates - exclude)
    rng.shuffle(pool)
    return pool[:PER_REALM]


def fmt_block(names, per_line=6, indent=12):
    pad = " " * indent
    lines = []
    for i in range(0, len(names), per_line):
        chunk = names[i:i + per_line]
        lines.append(pad + ", ".join(f'"{n}"' for n in chunk) + ",")
    return "\n".join(lines)


def main():
    with open(CS_FILE, encoding="utf-8") as fh:
        text = fh.read()

    rng = random.Random(SEED)
    for realm, (field, core) in REALMS.items():
        exclude = parse_existing(text, field)
        # also exclude names already chosen for other realms to keep them distinct
        names = gen_for_realm(core, exclude, rng)
        exclude.update(names)
        out = os.path.join(HERE, f"gamer_names_{realm}.txt")
        with open(out, "w", encoding="utf-8") as fh:
            fh.write(fmt_block(names) + "\n")
        print(f"{realm}: wrote {len(names)} names -> {out}")


if __name__ == "__main__":
    main()
