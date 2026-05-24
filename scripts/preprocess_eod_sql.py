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


# `INSERT INTO ` or `INSERT INTO\t` etc., case-sensitive (mysqldump is uppercase).
INSERT_RE = re.compile(r"^INSERT INTO\b")


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
    in_ddl_block = False  # True while inside a multi-line CREATE/DROP/ALTER

    with inp.open("r", encoding="utf-8", errors="replace") as f, \
         out.open("w", encoding="utf-8", newline="\n") as o:

        o.write("-- =====================================================================\n")
        o.write("-- Eve-of-Darkness public DB — preprocessed for 'fill the gaps' import\n")
        o.write("-- =====================================================================\n")
        o.write("-- DDL stripped, INSERT -> INSERT IGNORE so existing rows are preserved.\n")
        o.write("-- Apply with `mariadb --force` to continue past column-count drift.\n")
        o.write("\n")
        o.write("SET FOREIGN_KEY_CHECKS=0;\n")
        o.write("SET UNIQUE_CHECKS=0;\n")
        o.write("\n")

        for raw_line in f:
            line = raw_line
            stripped = line.lstrip()
            upper = stripped.upper()

            # End of multi-line DDL block? line ends with ; (after rstrip newline).
            if in_ddl_block:
                if line.rstrip().endswith(";"):
                    in_ddl_block = False
                continue

            # Open DDL block (or single-line DDL)?
            if upper.startswith("DROP TABLE") or upper.startswith("CREATE TABLE") or upper.startswith("ALTER TABLE"):
                stripped_ddl += 1
                if not line.rstrip().endswith(";"):
                    in_ddl_block = True
                continue

            # Per-table lock noise — drop.
            if upper.startswith("LOCK TABLES") or upper.startswith("UNLOCK TABLES"):
                continue

            # First line of INSERT block: rewrite to INSERT IGNORE. Continuation
            # lines (multi-row VALUES with leading whitespace+'(' ) are passed
            # through untouched.
            if INSERT_RE.match(stripped):
                line = line.replace("INSERT INTO", "INSERT IGNORE INTO", 1)
                rewrote_inserts += 1

            o.write(line)

        o.write("\nSET UNIQUE_CHECKS=1;\n")
        o.write("SET FOREIGN_KEY_CHECKS=1;\n")

    out_size_mb = out.stat().st_size / 1024 / 1024
    print(f"[eod] stripped {stripped_ddl} DDL statements, rewrote {rewrote_inserts} INSERTs")
    print(f"[eod] wrote {out} ({out_size_mb:.1f} MB)")


if __name__ == "__main__":
    main()
