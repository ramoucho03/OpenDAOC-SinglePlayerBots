#!/usr/bin/env python3
"""
Preprocess Eve-of-Darkness/db-public mysqldump for safe 'fill the gaps' import.

Source: https://github.com/Eve-of-Darkness/db-public/releases/download/<TAG>/public-db.mysql.sql.7z
        (extracted to public-db.mysql.sql, ~85 MB)

Goal: turn the upstream dump into an idempotent migration that ONLY adds rows
absent from our DB — OpenDAoC remains authoritative on every row it already
has.

Transforms applied:
- Strip every DROP TABLE / CREATE TABLE / ALTER TABLE block (would clobber the
  schema OpenDAoC's combined.sql + DOL's auto-create already established).
- Strip `LOCK TABLES`/`UNLOCK TABLES` (per-table locks pointless inside a single
  client session, can mask conflicts).
- Rewrite `INSERT INTO` -> `INSERT IGNORE INTO` so PK collisions silently keep
  our row (no overwrite — "fill the gaps" mode).
- Bracket with FOREIGN_KEY_CHECKS=0 / UNIQUE_CHECKS=0 so foreign-key dependency
  order across tables doesn't matter.
- Keep MySQL session pragmas (/*!40101 SET ... */) — harmless.
- **Region filter** on the Mob table: drop rows whose Region matches the
  saturation list (classic zones already heavily populated by OpenDAoC, where
  adding EoD's spawns just doubles them positionally without adding new
  content). Restricted to Mob — every other table is per-template or per-NPC,
  not per-spawn, so EoD's rows there are pure adds.

Caveat: if an Eve-of-Darkness table has columns we don't (schema drift), the
named-column INSERT IGNORE will fail at the statement level. We apply the SQL
with `mariadb --force` so the failing statement is logged and the rest keeps
going — best-effort fill.

Usage:
    python3 preprocess_eod_sql.py <input.sql> <output.sql>
"""

import re
import sys
from pathlib import Path


# Regions where OpenDAoC's mob.sql already has > 4 000 spawns. Re-importing
# EoD's Mob rows for these would just stack a second mob layer at near-identical
# coordinates — visible as duplicated wolves/goblins. The trade-off here is
# explicit: we lose ~50k EoD rows on classic ground, we keep tens of thousands
# on TOA / Catacombs / NF / LotM where OpenDAoC is empty.
SATURATED_REGIONS = {
    1,    # Albion classic        (OpenDAoC 19 045 mobs)
    100,  # Midgard classic       (OpenDAoC 20 224)
    200,  # Hibernia classic      (OpenDAoC 17 567)
    51,   # Avalon SI (Alb)       (OpenDAoC  6 562)
    151,  # Aegir SI (Mid)        (OpenDAoC  9 541)
    181,  # HyBrasil SI (Hib)     (OpenDAoC  5 836)
    249,  # Darkness Falls        (OpenDAoC  2 493 — already complete)
}

# Mob CREATE TABLE in EoD orders Region as the 13th column (0-based index 12).
# This is stable across releases; verified against release 85.
MOB_REGION_COL_INDEX = 12


INSERT_RE = re.compile(r"^INSERT INTO\b", re.IGNORECASE)
MOB_INSERT_RE = re.compile(r"^INSERT\s+(IGNORE\s+)?INTO\s+`Mob`\s*\(", re.IGNORECASE)
INSERT_TABLE_RE = re.compile(r"^INSERT\s+(?:IGNORE\s+)?INTO\s+`([^`]+)`", re.IGNORECASE)

# Tables whose entire INSERT block we intentionally drop. EoD ships some
# tables whose schema and conventions diverge from OpenDAoC's enough that
# importing them creates either schema errors or unusable rows.
#
# ClassXRealmAbility: OpenDAoC ships ClassXRealmAbility_Atlas (5 columns,
# different PK) and uses Atlas-only ability keys (AtlasOF_*). EoD ships the
# legacy 4-column table with classic ability keys (Augmented Strength etc).
# Even if we rewrote the table name, the abilities themselves wouldn't be
# wired in our Atlas RA system. Cleaner to drop the whole block.
SKIP_TABLES = {
    "ClassXRealmAbility",
}


def split_tuple(line):
    """Parse a `(...)` SQL row tuple into column strings. Returns None when the
    line isn't a tuple (header, comment, blank line)."""
    s = line.lstrip()
    if not s.startswith("("):
        return None
    s = s[1:]
    out = []
    cur = ""
    in_str = False
    i = 0
    while i < len(s):
        c = s[i]
        if in_str:
            if c == "\\" and i + 1 < len(s):
                cur += c + s[i+1]
                i += 2
                continue
            if c == "'" and i + 1 < len(s) and s[i+1] == "'":
                cur += "''"
                i += 2
                continue
            if c == "'":
                in_str = False
                cur += c
                i += 1
                continue
            cur += c
            i += 1
            continue
        if c == "'":
            in_str = True
            cur += c
            i += 1
            continue
        if c == ",":
            out.append(cur)
            cur = ""
            i += 1
            continue
        if c == ")":
            out.append(cur)
            return out
        cur += c
        i += 1
    return None


def main():
    if len(sys.argv) != 3:
        print("usage: preprocess_eod_sql.py <input.sql> <output.sql>", file=sys.stderr)
        sys.exit(2)

    inp = Path(sys.argv[1])
    out = Path(sys.argv[2])
    out.parent.mkdir(parents=True, exist_ok=True)

    in_size_mb = inp.stat().st_size / 1024 / 1024
    print(f"[eod] preprocessing {inp} ({in_size_mb:.1f} MB)...")

    stripped_ddl = 0
    rewrote_inserts = 0
    skipped_table_blocks = 0
    in_ddl_block = False  # inside a multi-line CREATE/DROP/ALTER
    in_skip_block = False  # inside an INSERT for a table in SKIP_TABLES

    # Mob-block state. The buffer holds the most recently kept Mob row whose
    # final punctuation (`,` vs `;`) we haven't decided yet — we know it only
    # when we see the next kept row (comma) or the end of the block (semicolon).
    in_mob_block = False
    mob_header_pending = None   # deferred header line; written when first kept row arrives
    mob_buffered_row = None     # the previous kept row, awaiting its trailing punct
    mob_skipped = 0
    mob_kept = 0

    def flush_mob_buffer(o_handle, ending_punct):
        """Emit the buffered Mob row with explicit ending punctuation."""
        nonlocal mob_buffered_row
        if mob_buffered_row is None:
            return
        cleaned = mob_buffered_row.rstrip()
        # Strip whatever punctuation the source had (`,` or `;`) so we control it.
        if cleaned.endswith(",") or cleaned.endswith(";"):
            cleaned = cleaned[:-1]
        o_handle.write(cleaned + ending_punct + "\n")
        mob_buffered_row = None

    def close_mob_block(o_handle):
        """Called when we leave the Mob INSERT statement. Terminates the
        buffered row with `;` (or writes nothing if zero rows survived the
        filter — header was deferred precisely for this case)."""
        nonlocal in_mob_block, mob_header_pending
        if mob_buffered_row is not None:
            flush_mob_buffer(o_handle, ";")
        # If header was never written (zero rows kept), drop it silently.
        mob_header_pending = None
        in_mob_block = False

    with inp.open("r", encoding="utf-8", errors="replace") as f, \
         out.open("w", encoding="utf-8", newline="\n") as o:

        o.write("-- =====================================================================\n")
        o.write("-- Eve-of-Darkness public DB — preprocessed for 'fill the gaps' import\n")
        o.write("-- =====================================================================\n")
        o.write("-- DDL stripped, INSERT -> INSERT IGNORE so existing rows are preserved.\n")
        o.write(f"-- Mob rows filtered for regions {sorted(SATURATED_REGIONS)} (classic\n")
        o.write("-- zones where OpenDAoC's mob.sql is already saturated — re-importing\n")
        o.write("-- EoD's spawns there would just stack duplicates positionally).\n")
        o.write("-- Apply with `mariadb --force` to continue past column-count drift.\n")
        o.write("\n")
        o.write("SET FOREIGN_KEY_CHECKS=0;\n")
        o.write("SET UNIQUE_CHECKS=0;\n")
        o.write("\n")

        for raw_line in f:
            line = raw_line
            stripped = line.lstrip()
            upper = stripped.upper()

            # ---- Skip DDL blocks --------------------------------------
            if in_ddl_block:
                if line.rstrip().endswith(";"):
                    in_ddl_block = False
                continue
            if upper.startswith("DROP TABLE") or upper.startswith("CREATE TABLE") or upper.startswith("ALTER TABLE"):
                stripped_ddl += 1
                if not line.rstrip().endswith(";"):
                    in_ddl_block = True
                # If we were mid-Mob-block, close it before moving on.
                if in_mob_block:
                    close_mob_block(o)
                continue

            # ---- Drop LOCK/UNLOCK noise -------------------------------
            if upper.startswith("LOCK TABLES") or upper.startswith("UNLOCK TABLES"):
                if in_mob_block:
                    close_mob_block(o)
                continue

            # ---- INSERT statement boundary ----------------------------
            if INSERT_RE.match(stripped):
                # Leaving any previous Mob block we were in.
                if in_mob_block:
                    close_mob_block(o)
                # Also leaving any prior skip-table block.
                in_skip_block = False

                # Identify the target table for skip / mob-filter / passthrough.
                m_table = INSERT_TABLE_RE.match(stripped)
                target_table = m_table.group(1) if m_table else ""

                if target_table in SKIP_TABLES:
                    # Drop the entire INSERT (header + all rows) until the next
                    # statement boundary. Logged so the operator sees it.
                    in_skip_block = True
                    skipped_table_blocks += 1
                    continue

                rewritten = line.replace("INSERT INTO", "INSERT IGNORE INTO", 1)
                rewrote_inserts += 1

                if MOB_INSERT_RE.match(stripped):
                    # Defer the header; only write it if at least one row survives
                    # the region filter. Stays in Mob mode until next INSERT/DDL.
                    in_mob_block = True
                    mob_header_pending = rewritten
                else:
                    o.write(rewritten)
                continue

            # ---- Inside a skipped table's INSERT block ----------------
            if in_skip_block:
                # Drop every continuation row (and any trailing punctuation
                # line) until the next statement boundary handled above.
                continue

            # ---- Inside a Mob INSERT block ----------------------------
            if in_mob_block:
                parts = split_tuple(line)
                if parts is None:
                    # Non-row line inside the block (rare — possibly blank or
                    # comment). Treat as block terminator out of caution and
                    # then re-emit the line normally.
                    close_mob_block(o)
                    o.write(line)
                    continue

                # Parse region from column index 12. Defensive: if the schema
                # ever changes we just keep the row (safer than dropping by
                # mistake on a wrong column).
                region = None
                if len(parts) > MOB_REGION_COL_INDEX:
                    try:
                        region = int(parts[MOB_REGION_COL_INDEX].strip())
                    except ValueError:
                        region = None

                if region is not None and region in SATURATED_REGIONS:
                    mob_skipped += 1
                    continue

                # Keep this row. Lazily write the header if this is the first
                # surviving row in the block.
                if mob_header_pending is not None:
                    o.write(mob_header_pending)
                    mob_header_pending = None
                # Flush the previous kept row with a comma (since another is
                # coming). The current row becomes the new buffer; its final
                # punctuation is decided when we leave the block.
                flush_mob_buffer(o, ",")
                mob_buffered_row = line
                mob_kept += 1
                continue

            # ---- Default: pass through --------------------------------
            o.write(line)

        # End of file: close any open Mob block.
        if in_mob_block:
            close_mob_block(o)

        o.write("\nSET UNIQUE_CHECKS=1;\n")
        o.write("SET FOREIGN_KEY_CHECKS=1;\n")

    out_size_mb = out.stat().st_size / 1024 / 1024
    print(f"[eod] stripped {stripped_ddl} DDL statements, rewrote {rewrote_inserts} INSERTs")
    print(f"[eod] Mob filter: kept {mob_kept}, skipped {mob_skipped} (saturated regions {sorted(SATURATED_REGIONS)})")
    print(f"[eod] dropped {skipped_table_blocks} INSERT blocks for tables in SKIP_TABLES={sorted(SKIP_TABLES)}")
    print(f"[eod] wrote {out} ({out_size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
