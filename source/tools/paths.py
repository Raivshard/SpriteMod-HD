r"""Project folder layout (user, 2026-11-24): every tool takes its folders from here.

  data\        REFERENCE only: outside sources (data\sources\) and catalogues made from the game files (data\catalogs\)
  Extracted\   the game's art as extracted (extract_sprites.py), with manifest.json and report.txt
  Modified\    the user's finished art
  Working\     the user's work, by category (the same categories as Extracted\ and Modified\)
  build\       generated output: can be rebuilt (build output, reviews and reports, the tools' own records)
  _archive\    superseded builds, backups, parked work
  Test results\  screenshots and logs from in-game tests
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

DATA = ROOT / "data"
SOURCES = DATA / "sources"
MOBILE_SRC = SOURCES / "FF5 Mobile"                      # mobile tilesets (Tilesets\), field objects (Field Objects\)
MOBILE_TILESETS = MOBILE_SRC / "Tilesets"
CATALOGS = DATA / "catalogs"

EXTRACTED = ROOT / "Extracted"
MANIFEST = EXTRACTED / "manifest.json"

WORKING = ROOT / "Working"
KARNAK = WORKING / "tilesets" / "Karnak"                 # the Karnak map project (project.json) and its decisions
MANUAL_CHOICES = KARNAK / "manual_choices.csv"
DECISIONS = KARNAK / "decisions.csv"

BUILD = ROOT / "build"
KARNAK_BUILD = BUILD / "Karnak"                          # mobile_v5\ (tile build), installed\, shadow_masks\
MOBILE_MATCHING = BUILD / "mobile matching"              # votes, pairs, coverage, variants (computed from the sources)
REVIEWS = BUILD / "reviews and reports"
TOOL_RECORDS = BUILD / "tool records"

ARCHIVE = ROOT / "_archive"
TEST_RESULTS = ROOT / "Test results"
SCREENSHOTS = TEST_RESULTS / "screenshots"


def catalog(game):
    """the game's tile catalogue (map_tile_catalog.py), shared by every map project of that game"""
    return CATALOGS / game.upper()
