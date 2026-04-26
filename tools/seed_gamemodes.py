#!/usr/bin/env python3
"""
Seeds the /gameModes collection in staging (or prod) with the 5 existing
formats (Pauper, Historic Pauper, Standard Pauper, Planar Standard, Modern)
expressed in the new game-mode schema.

Each call hits the createGameMode Cloud Function with the SHA-256-of-password
auth header. The Cloud Function resolves the legalitySource (Scryfall query
or sqlite filter) and writes both /gameModes/{id} and /legalityCache/{id}.

Usage:
    python tools/seed_gamemodes.py --env staging
    python tools/seed_gamemodes.py --env prod
"""

import argparse
import hashlib
import json
import sys
import urllib.request
import urllib.error

FUNCTIONS_URL = "https://us-central1-mtga-enhancement-suite.cloudfunctions.net"
PASSWORD = "TrustedGameModeCre@t0r!"
PASSWORD_HASH = hashlib.sha256(PASSWORD.encode()).hexdigest()

# The five built-in modes expressed in the new schema.
# - "scryfall" type: legalitySource.query is run by the Cloud Function.
# - "sqlite" type: legalitySource.{rarities,sets,excludeNames} is filtered against /cardMetadata/cards.
SEED_MODES = [
    {
        "id": "pauper",
        "displayName": "Pauper (Paper Banlist)",
        "description": "Paper Pauper format — only commons, banlist managed by Wizards.",
        "matchType": "DirectGame",
        "isBestOf3Default": True,
        "legalitySource": {"type": "scryfall", "query": "legal:pauper"},
    },
    {
        "id": "historicpauper",
        "displayName": "Historic Pauper",
        "description": "All commons available on Arena, including Alchemy/digital sets.",
        "matchType": "DirectGameAlchemy",
        "isBestOf3Default": True,
        "legalitySource": {
            "type": "sqlite",
            "rarities": ["common", "basic"],
            "sets": [],   # all Arena sets
            "excludeNames": [
                "Ancestral Mask",
                "Cranial Ram",
                "Galvanic Blast",
                "Persistent Petitioners",
                "Refurbished Familiar",
                "Sneaky Snacker",
                "Kuldotha Rebirth",
            ],
        },
    },
    {
        "id": "standardpauper",
        "displayName": "Standard Pauper",
        "description": "Standard-legal commons.",
        "matchType": "DirectGame",
        "isBestOf3Default": True,
        "legalitySource": {
            "type": "scryfall",
            "query": "(game:arena) legal:standard",
            "filterCommonPrints": True,
        },
    },
    {
        "id": "planarstandard",
        "displayName": "Planar Standard",
        "description": "Cards from a curated Standard slice (ECL, EOE, TDM, DFT, FDN, SOS) minus a few bans.",
        "matchType": "DirectGame",
        "isBestOf3Default": True,
        "legalitySource": {
            "type": "scryfall",
            "query": 'game:paper (set:ecl or set:eoe or set:tdm or set:dft or set:fdn or set:sos) -name:"Cori-steel Cutter"',
        },
    },
    {
        "id": "modern",
        "displayName": "Modern",
        "description": "All Modern-legal cards (paper Modern + Arena printings).",
        "matchType": "DirectGame",
        "isBestOf3Default": True,
        "legalitySource": {"type": "scryfall", "query": "legal:modern"},
    },
]


def post_mode(env: str, mode: dict) -> None:
    url = f"{FUNCTIONS_URL}/createGameMode?env={env}"
    payload = json.dumps(mode).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=payload,
        headers={
            "Content-Type": "application/json",
            "X-MTGAES-Auth": PASSWORD_HASH,
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=300) as resp:
            data = json.loads(resp.read())
            print(f"  ✓ {mode['id']}: {data.get('totalCards', '?')} legal cards")
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", errors="replace")
        print(f"  ✗ {mode['id']}: HTTP {e.code} {body}", file=sys.stderr)
    except urllib.error.URLError as e:
        print(f"  ✗ {mode['id']}: {e.reason}", file=sys.stderr)


def main():
    parser = argparse.ArgumentParser(description="Seed gameModes in Firebase")
    parser.add_argument("--env", default="staging", choices=["prod", "staging"],
                        help="Environment to seed (default: staging)")
    parser.add_argument("--only", help="Only seed this mode id")
    args = parser.parse_args()

    print(f"Seeding game modes to env={args.env}")
    print(f"Functions URL: {FUNCTIONS_URL}")
    print(f"Auth hash:     {PASSWORD_HASH[:16]}…")

    targets = SEED_MODES
    if args.only:
        targets = [m for m in SEED_MODES if m["id"] == args.only]
        if not targets:
            print(f"No mode with id '{args.only}'", file=sys.stderr)
            return 1

    for mode in targets:
        print(f"\nSeeding {mode['id']} ({mode['legalitySource']['type']})...")
        post_mode(args.env, mode)

    print("\nDone.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
