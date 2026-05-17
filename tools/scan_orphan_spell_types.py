"""
Scan the OpenDAoC code for [SpellHandler(eSpellType.X)] attributes, then
list any `spell.Type` values present in the DB that have NO matching
handler. Suggests a remap to the closest existing handler.

Usage (on the server box):
  python3 scan_orphan_spell_types.py
  # Writes suggested remap SQL to /tmp/orphan_remap.sql
"""
import os, re, subprocess, sys

repo_root = os.environ.get('REPO', '/home/ramoucho/OpenDAOC-SinglePlayerBots')
spells_dir = os.path.join(repo_root, 'GameServer', 'spells')

# Container / DB credentials
CONTAINER = os.environ.get('CONTAINER', 'opendaoc-db')
DBUSER = os.environ.get('DBUSER', 'root')
DBPASS = os.environ.get('DBPASS', 'my-secret-pw')
DB = os.environ.get('DB', 'opendaoc')

# 1) Grab every eSpellType.X referenced in [SpellHandler(eSpellType.X)]
attr_re = re.compile(r'\[SpellHandler\(eSpellType\.([A-Za-z0-9_]+)\)\]')
handlers = set()
for dirpath, _, files in os.walk(spells_dir):
    for f in files:
        if not f.endswith('.cs'):
            continue
        for m in attr_re.finditer(open(os.path.join(dirpath, f), encoding='utf-8', errors='ignore').read()):
            handlers.add(m.group(1))

print(f"Found {len(handlers)} registered spell handlers in code.", file=sys.stderr)

# 2) Pull distinct Type values from the live DB
sql = "SELECT DISTINCT Type FROM spell WHERE Type IS NOT NULL AND Type <> '';"
cmd = ['sudo', 'docker', 'exec', '-i', CONTAINER, 'mariadb',
       f'-u{DBUSER}', f'-p{DBPASS}', '-N', '--silent', DB, '-e', sql]
proc = subprocess.run(cmd, capture_output=True, text=True)
if proc.returncode != 0:
    print(f"DB query failed: {proc.stderr}", file=sys.stderr)
    sys.exit(1)

db_types = {t.strip() for t in proc.stdout.splitlines() if t.strip()}
print(f"Found {len(db_types)} distinct spell types in DB.", file=sys.stderr)

orphans = sorted(db_types - handlers)
print(f"\nOrphan types (DB references handler that doesn't exist): {len(orphans)}\n", file=sys.stderr)

# 3) Suggest remaps based on simple name heuristics
def suggest(orphan):
    """Pick the closest existing handler. Strip LOP / OnPulse suffixes first."""
    candidates = [
        orphan.removesuffix('LOP').removesuffix('LostOnPulse').removesuffix('OnPulse'),
        orphan.replace('LostOnPulse', '').replace('OnPulse', '').replace('LOP', ''),
    ]
    for c in candidates:
        if c in handlers:
            return c
    # Fallback: substring match
    for h in handlers:
        if orphan.startswith(h) or h.startswith(orphan):
            return h
    return None

out = '/tmp/orphan_remap.sql'
with open(out, 'w', encoding='utf-8') as f:
    f.write("-- Auto-generated remap of orphan spell types -> closest existing handler.\n")
    f.write("-- Review BEFORE running; some suggestions may be wrong.\n\n")
    for o in orphans:
        sug = suggest(o)
        if sug:
            f.write(f"-- {o} -> {sug}\n")
            f.write(f"UPDATE spell SET Type = '{sug}' WHERE Type = '{o}';\n\n")
        else:
            f.write(f"-- {o} -> NO SUGGESTION (manual review needed)\n")
            f.write(f"-- UPDATE spell SET Type = '???' WHERE Type = '{o}';\n\n")

print(f"\nWrote suggestions to {out}", file=sys.stderr)
print("\nOrphans summary:")
for o in orphans:
    sug = suggest(o)
    cnt_sql = f"SELECT COUNT(*) FROM spell WHERE Type='{o}';"
    cnt = subprocess.run(['sudo','docker','exec','-i',CONTAINER,'mariadb',
        f'-u{DBUSER}', f'-p{DBPASS}', '-N', '--silent', DB, '-e', cnt_sql],
        capture_output=True, text=True).stdout.strip()
    print(f"  {o:<45s} -> {sug or '(no match)'} ({cnt} spells affected)")
