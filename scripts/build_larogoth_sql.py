#!/usr/bin/env python3
"""
Build-time transformer: Larogoth/DAoCDatabase JSON -> 3 idempotent SQL files.

Outputs (under <out_dir>). Numbering reflects the **apply order** wired in the entrypoint:
  10_larogoth_items.sql  Strategy 3 — INSERT IGNORE missing items, restricted to
                         unambiguous Object_Type (Shield=42, Magical=41). Weapons & body
                         armor are skipped (mapping too risky without weapon_type/material
                         class info). PackageID='Larogoth_Import' enables trivial rollback.
                         RUNS FIRST so the next steps can join on these new rows.
  20_larogoth_ext.sql    Strategy 1 — extended item metadata (delve_text, utility,
                         material, ability_tags) in a NEW table. Match on (Name, Realm).
                         NEVER touches ItemTemplate.Description (would duplicate tooltips).
  30_larogoth_loot.sql   Strategy 2 — ItemLootSource table (who drops what, where to buy,
                         which quests reward). Match on (Name, Realm).

The 4th migration (40_larogoth_loot_wiring.sql) is a hand-written static SQL file
that converts ItemLootSource → LootTemplate + MobXLootTemplate + LootOTD so the
gameserver's LootGeneratorTemplate actually drops the items in-game.

All three SQLs are idempotent: CREATE TABLE IF NOT EXISTS, REPLACE INTO / INSERT IGNORE,
and a per-script row in MigrationState with sha256 checksum (set by the entrypoint).

Usage:
    python3 build_larogoth_sql.py <input.json> <out_dir>
"""

import json
import sys
from pathlib import Path

# ---------- slot/category mapping ----------
# DOL eInventorySlot (IGameInventory.cs) — only equipable slots we'll emit.
SLOT_TO_ITEM_TYPE = {
    "Main Hand":      10,  # RightHandWeapon
    "Right Hand":     10,
    "Off Hand":       11,  # LeftHandWeapon
    "Left Hand":      11,
    "Two Handed":     12,  # TwoHandWeapon
    "Two-Handed":     12,
    "Ranged":         13,  # DistanceWeapon
    "Distance":       13,
    "Head":           21,
    "Hands":          22,
    "Feet":           23,
    "Jewelry":        24,
    "Jewel":          24,
    "Torso":          25,
    "Chest":          25,
    "Cloak":          26,
    "Legs":           27,
    "Arms":           28,
    "Neck":           29,
    "Waist":          32,
    "Belt":           32,
    "Left Wrist":     33,
    "Right Wrist":    34,
    "Wrist":          33,    # fallback for ambiguous bracer
    "Bracer":         33,
    "Left Ring":      35,
    "Right Ring":     36,
    "Ring":           35,
    "Mythirian":      37,
    "Mythical":       37,
}

# eObjectType — only the unambiguous targets used by strategy 3.
OBJ_SHIELD  = 42
OBJ_MAGICAL = 41

# Category in Larogoth JSON observed: 1=weapon 2=armor 3=shield 4=instrument? 5=magical 8=?

def sql_escape(s):
    if s is None:
        return "NULL"
    return "'" + s.replace("\\", "\\\\").replace("'", "''") + "'"


def build_ext_sql(items, out_path):
    """Strategy 1: extended metadata table populated from staging."""
    lines = []
    lines.append("-- Strategy 1: Larogoth extended item metadata")
    lines.append("-- New table — does NOT touch ItemTemplate.Description (avoids tooltip duplication).")
    lines.append("-- Match on (Name, Realm). Skips items whose Name+Realm doesn't exist in ItemTemplate.")
    lines.append("")
    lines.append("CREATE TABLE IF NOT EXISTS ItemTemplate_LarogothExt (")
    lines.append("    Id_nb         VARCHAR(255) NOT NULL,")
    lines.append("    LarogothId    VARCHAR(64)  NOT NULL,")
    lines.append("    Realm         INT          NOT NULL,")
    lines.append("    DelveText     TEXT,")
    lines.append("    Utility       DECIMAL(8,2) DEFAULT 0,")
    lines.append("    Material      INT          DEFAULT 0,")
    lines.append("    AbilityTags   TEXT,")
    lines.append("    PRIMARY KEY (Id_nb),")
    lines.append("    KEY idx_larogoth_id (LarogothId)")
    lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;")
    lines.append("")
    # Staging table for matching. TEMPORARY would be ideal but breaks across separate
    # statements through some clients — use a regular table with TRUNCATE up front.
    lines.append("CREATE TABLE IF NOT EXISTS _LarogothExt_Staging (")
    lines.append("    LarogothId    VARCHAR(64)  NOT NULL,")
    lines.append("    Name          VARCHAR(255) NOT NULL,")
    lines.append("    Realm         INT          NOT NULL,")
    lines.append("    DelveText     TEXT,")
    lines.append("    Utility       DECIMAL(8,2) DEFAULT 0,")
    lines.append("    Material      INT          DEFAULT 0,")
    lines.append("    AbilityTags   TEXT,")
    lines.append("    KEY idx_name_realm (Name, Realm)")
    lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;")
    lines.append("TRUNCATE TABLE _LarogothExt_Staging;")
    lines.append("")

    BATCH = 500
    cur = []
    written = 0
    for it in items:
        name = it.get("name") or ""
        if not name:
            continue
        delve = it.get("delve_text") or ""
        # Cap delve text size — observed up to 4-5 KB. TEXT limit is 64 KB, leave margin.
        if len(delve) > 8000:
            delve = delve[:8000]
        tags = ";".join(it.get("ability_tags") or [])
        if len(tags) > 4000:
            tags = tags[:4000]
        cur.append("({lid},{nm},{rm},{dv},{ut},{mt},{tg})".format(
            lid=sql_escape(str(it.get("id") or "")),
            nm=sql_escape(name),
            rm=int(it.get("realm") or 0),
            dv=sql_escape(delve) if delve else "NULL",
            ut=float(it.get("utility") or 0),
            mt=int(it.get("material") or 0),
            tg=sql_escape(tags) if tags else "NULL",
        ))
        if len(cur) >= BATCH:
            lines.append("INSERT INTO _LarogothExt_Staging (LarogothId,Name,Realm,DelveText,Utility,Material,AbilityTags) VALUES")
            lines.append(",\n".join(cur) + ";")
            written += len(cur)
            cur = []
    if cur:
        lines.append("INSERT INTO _LarogothExt_Staging (LarogothId,Name,Realm,DelveText,Utility,Material,AbilityTags) VALUES")
        lines.append(",\n".join(cur) + ";")
        written += len(cur)

    # Populate the real table by joining staging with ItemTemplate. REPLACE so re-running
    # the file picks up updated delve_text/utility on later imports.
    lines.append("")
    lines.append("REPLACE INTO ItemTemplate_LarogothExt (Id_nb, LarogothId, Realm, DelveText, Utility, Material, AbilityTags)")
    lines.append("SELECT it.Id_nb, s.LarogothId, s.Realm, s.DelveText, s.Utility, s.Material, s.AbilityTags")
    lines.append("FROM _LarogothExt_Staging s")
    lines.append("INNER JOIN ItemTemplate it ON it.Name = s.Name AND it.Realm = s.Realm;")
    lines.append("")
    lines.append("DROP TABLE _LarogothExt_Staging;")

    out_path.write_text("\n".join(lines), encoding="utf-8")
    return written


def build_loot_sql(items, out_path):
    """Strategy 2: ItemLootSource table — who drops, who sells, which quest."""
    lines = []
    lines.append("-- Strategy 2: Larogoth loot/source info")
    lines.append("-- New table — purely additive.")
    lines.append("")
    lines.append("CREATE TABLE IF NOT EXISTS ItemLootSource (")
    lines.append("    Id_nb       VARCHAR(255) NOT NULL,")
    lines.append("    SourceType  VARCHAR(32)  NOT NULL,")
    lines.append("    SourceName  VARCHAR(255) NOT NULL,")
    lines.append("    PRIMARY KEY (Id_nb, SourceType, SourceName),")
    lines.append("    KEY idx_id_nb (Id_nb),")
    lines.append("    KEY idx_source_name (SourceName)")
    lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;")
    lines.append("")
    lines.append("CREATE TABLE IF NOT EXISTS _LarogothLoot_Staging (")
    lines.append("    Name        VARCHAR(255) NOT NULL,")
    lines.append("    Realm       INT          NOT NULL,")
    lines.append("    SourceType  VARCHAR(32)  NOT NULL,")
    lines.append("    SourceName  VARCHAR(255) NOT NULL,")
    lines.append("    KEY idx_name_realm (Name, Realm)")
    lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;")
    lines.append("TRUNCATE TABLE _LarogothLoot_Staging;")
    lines.append("")

    rows = []
    BATCH = 800

    def flush():
        if not rows:
            return
        lines.append("INSERT INTO _LarogothLoot_Staging (Name,Realm,SourceType,SourceName) VALUES")
        lines.append(",\n".join(rows) + ";")
        rows.clear()

    for it in items:
        name = it.get("name") or ""
        if not name:
            continue
        realm = int(it.get("realm") or 0)
        srcs = it.get("sources") or {}
        # monsters can be normal_drop / one_time_drop / etc.
        monsters = srcs.get("monsters") or {}
        for mtype, mlist in monsters.items():
            for m in (mlist or []):
                if not m:
                    continue
                stype = ("monster_" + str(mtype))[:32]
                rows.append("({n},{r},{st},{sn})".format(
                    n=sql_escape(name), r=realm,
                    st=sql_escape(stype),
                    sn=sql_escape(str(m)[:255]),
                ))
                if len(rows) >= BATCH:
                    flush()
        for q in (srcs.get("quests") or []):
            if not q:
                continue
            rows.append("({n},{r},'quest',{sn})".format(
                n=sql_escape(name), r=realm, sn=sql_escape(str(q)[:255])))
            if len(rows) >= BATCH:
                flush()
        for s in (srcs.get("stores") or []):
            if not s:
                continue
            rows.append("({n},{r},'store',{sn})".format(
                n=sql_escape(name), r=realm, sn=sql_escape(str(s)[:255])))
            if len(rows) >= BATCH:
                flush()
    flush()

    lines.append("")
    lines.append("INSERT IGNORE INTO ItemLootSource (Id_nb, SourceType, SourceName)")
    lines.append("SELECT it.Id_nb, s.SourceType, s.SourceName")
    lines.append("FROM _LarogothLoot_Staging s")
    lines.append("INNER JOIN ItemTemplate it ON it.Name = s.Name AND it.Realm = s.Realm;")
    lines.append("")
    lines.append("DROP TABLE _LarogothLoot_Staging;")

    out_path.write_text("\n".join(lines), encoding="utf-8")


def safe_slug(name, larogoth_id):
    # Build a deterministic Id_nb prefixed to avoid any clash with existing ones.
    s = "".join(c if c.isalnum() else "_" for c in (name or "").lower())
    s = s.strip("_")[:120]
    return "larogoth_" + (larogoth_id or "x") + "_" + s


def build_items_sql(items, out_path):
    """Strategy 3: insert missing items, restricted to unambiguous Object_Type.

    Only category=3 (shields, Object_Type=42) and category=5 (magical accessories,
    Object_Type=41) are inserted. Skips weapons & body armor.
    """
    lines = []
    lines.append("-- Strategy 3: insert missing Larogoth items (conservative subset)")
    lines.append("-- Only shields (Object_Type=42) and magical accessories (Object_Type=41).")
    lines.append("-- PackageID='Larogoth_Import' — rollback with: DELETE FROM ItemTemplate WHERE PackageID='Larogoth_Import';")
    lines.append("")
    lines.append("CREATE TABLE IF NOT EXISTS _LarogothItem_Staging (")
    lines.append("    Id_nb         VARCHAR(255) NOT NULL PRIMARY KEY,")
    lines.append("    Name          VARCHAR(255) NOT NULL,")
    lines.append("    Realm         INT          NOT NULL,")
    lines.append("    Level         INT          NOT NULL,")
    lines.append("    Object_Type   INT          NOT NULL,")
    lines.append("    Item_Type     INT          NOT NULL,")
    lines.append("    DPS_AF        INT          NOT NULL DEFAULT 0,")
    lines.append("    SPD_ABS       INT          NOT NULL DEFAULT 0,")
    lines.append("    Hand          INT          NOT NULL DEFAULT 0,")
    lines.append("    BonusLevel    INT          NOT NULL DEFAULT 0,")
    lines.append("    Bonus1 INT, Bonus1Type INT, Bonus2 INT, Bonus2Type INT,")
    lines.append("    Bonus3 INT, Bonus3Type INT, Bonus4 INT, Bonus4Type INT,")
    lines.append("    Bonus5 INT, Bonus5Type INT, Bonus6 INT, Bonus6Type INT,")
    lines.append("    Bonus7 INT, Bonus7Type INT, Bonus8 INT, Bonus8Type INT,")
    lines.append("    Bonus9 INT, Bonus9Type INT, Bonus10 INT, Bonus10Type INT,")
    lines.append("    KEY idx_name_realm (Name, Realm)")
    lines.append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb3;")
    lines.append("TRUNCATE TABLE _LarogothItem_Staging;")
    lines.append("")

    BATCH = 200
    rows = []
    inserted = 0

    def flush():
        nonlocal inserted
        if not rows:
            return
        lines.append("INSERT IGNORE INTO _LarogothItem_Staging (Id_nb,Name,Realm,Level,Object_Type,Item_Type,DPS_AF,SPD_ABS,Hand,BonusLevel," +
                     "Bonus1,Bonus1Type,Bonus2,Bonus2Type,Bonus3,Bonus3Type,Bonus4,Bonus4Type,Bonus5,Bonus5Type," +
                     "Bonus6,Bonus6Type,Bonus7,Bonus7Type,Bonus8,Bonus8Type,Bonus9,Bonus9Type,Bonus10,Bonus10Type) VALUES")
        lines.append(",\n".join(rows) + ";")
        inserted += len(rows)
        rows.clear()

    for it in items:
        cat = it.get("category")
        slot = it.get("slot") or ""
        item_type = SLOT_TO_ITEM_TYPE.get(slot)
        if item_type is None:
            continue
        if cat == 3:
            obj_type = OBJ_SHIELD
            td = it.get("type_data") or {}
            dps_af = int(round(float(td.get("dps") or 0) * 10))
            spd_abs = int(round(float(td.get("speed") or 0) * 10))
            hand = 1  # shield off-hand
        elif cat == 5:
            obj_type = OBJ_MAGICAL
            dps_af = 0
            spd_abs = int(it.get("absorption") or 0)
            hand = 0
        else:
            continue  # skip weapons & body armor (regression risk)

        name = it.get("name") or ""
        if not name:
            continue
        realm = int(it.get("realm") or 0)
        level = int(it.get("level") or 0)
        bonus_level = int(it.get("bonus_level") or 0)
        larogoth_id = str(it.get("id") or "x")
        id_nb = safe_slug(name, larogoth_id)

        # Bonuses: take first 10
        bonus_pairs = []
        for b in (it.get("bonuses") or [])[:10]:
            try:
                btype = int(b.get("type") or 0)
                bval  = int(b.get("value") or 0)
            except (TypeError, ValueError):
                btype, bval = 0, 0
            bonus_pairs.append((bval, btype))
        while len(bonus_pairs) < 10:
            bonus_pairs.append((0, 0))

        bonus_sql = ",".join(f"{v},{t}" for v, t in bonus_pairs)

        rows.append("({id_nb},{name},{realm},{level},{ot},{it_t},{dps},{spd},{hand},{bl},{bs})".format(
            id_nb=sql_escape(id_nb),
            name=sql_escape(name),
            realm=realm, level=level, ot=obj_type, it_t=item_type,
            dps=dps_af, spd=spd_abs, hand=hand, bl=bonus_level,
            bs=bonus_sql,
        ))
        if len(rows) >= BATCH:
            flush()
    flush()

    # Insert into ItemTemplate ONLY rows whose (Name, Realm) is not already present.
    # PackageID marks them for trivial rollback. Model defaults to a reasonable item type
    # bag for cat=5; shields use Model 1280 (a generic shield) — both kept simple, the
    # core need is bots being able to enumerate available items, not visual fidelity.
    lines.append("")
    lines.append("INSERT IGNORE INTO ItemTemplate (")
    lines.append("    Id_nb, Name, Realm, Level, Object_Type, Item_Type, DPS_AF, SPD_ABS, Hand,")
    lines.append("    Model, Quality, Condition, MaxCondition, Durability, MaxDurability,")
    lines.append("    IsDropable, IsPickable, IsTradable, CanDropAsLoot, MaxCount, PackSize,")
    lines.append("    BonusLevel, PackageID,")
    lines.append("    Bonus1, Bonus1Type, Bonus2, Bonus2Type, Bonus3, Bonus3Type,")
    lines.append("    Bonus4, Bonus4Type, Bonus5, Bonus5Type, Bonus6, Bonus6Type,")
    lines.append("    Bonus7, Bonus7Type, Bonus8, Bonus8Type, Bonus9, Bonus9Type,")
    lines.append("    Bonus10, Bonus10Type")
    lines.append(")")
    lines.append("SELECT")
    lines.append("    s.Id_nb, s.Name, s.Realm, s.Level, s.Object_Type, s.Item_Type, s.DPS_AF, s.SPD_ABS, s.Hand,")
    lines.append("    CASE WHEN s.Object_Type = 42 THEN 1280 ELSE 488 END,")
    lines.append("    100, 50000, 50000, 50000, 50000,")
    lines.append("    1, 1, 1, 1, 1, 1,")
    lines.append("    s.BonusLevel, 'Larogoth_Import',")
    lines.append("    s.Bonus1, s.Bonus1Type, s.Bonus2, s.Bonus2Type, s.Bonus3, s.Bonus3Type,")
    lines.append("    s.Bonus4, s.Bonus4Type, s.Bonus5, s.Bonus5Type, s.Bonus6, s.Bonus6Type,")
    lines.append("    s.Bonus7, s.Bonus7Type, s.Bonus8, s.Bonus8Type, s.Bonus9, s.Bonus9Type,")
    lines.append("    s.Bonus10, s.Bonus10Type")
    lines.append("FROM _LarogothItem_Staging s")
    lines.append("LEFT JOIN ItemTemplate it ON it.Name = s.Name AND it.Realm = s.Realm")
    lines.append("WHERE it.Id_nb IS NULL;")
    lines.append("")
    lines.append("DROP TABLE _LarogothItem_Staging;")

    out_path.write_text("\n".join(lines), encoding="utf-8")
    return inserted


def main():
    if len(sys.argv) != 3:
        print("usage: build_larogoth_sql.py <input.json> <out_dir>", file=sys.stderr)
        sys.exit(2)
    inp = Path(sys.argv[1])
    out = Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)

    print(f"[larogoth] reading {inp} ({inp.stat().st_size/1024/1024:.1f} MB)...")
    with inp.open("r", encoding="utf-8") as f:
        items = json.load(f)
    print(f"[larogoth] loaded {len(items)} items")

    inserted = build_items_sql(items, out / "10_larogoth_items.sql")
    print(f"[larogoth] wrote 10_larogoth_items.sql ({inserted} candidate inserts)")

    ext_count = build_ext_sql(items, out / "20_larogoth_ext.sql")
    print(f"[larogoth] wrote 20_larogoth_ext.sql   ({ext_count} staging rows)")

    build_loot_sql(items, out / "30_larogoth_loot.sql")
    print(f"[larogoth] wrote 30_larogoth_loot.sql")


if __name__ == "__main__":
    main()
