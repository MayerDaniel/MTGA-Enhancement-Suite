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
import socket
import sys
import time
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
    # Cloud Run API gateway forces a 60s response timeout, but the function
    # itself can run for up to 540s. We fire the request with a short timeout
    # and treat the timeout as "still running" — we'll verify with a poll
    # against /listGameModes after all submissions are sent.
    try:
        with urllib.request.urlopen(req, timeout=55) as resp:
            data = json.loads(resp.read())
            print(f"  OK {mode['id']}: {data.get('totalCards', '?')} legal cards")
    except (socket.timeout, TimeoutError):
        print(f"  ... {mode['id']}: request timed out (function still running in background)")
    except urllib.error.HTTPError as e:
        # 504 Gateway Timeout from Cloud Run — same as a socket timeout
        if e.code == 504:
            print(f"  ... {mode['id']}: 504 timeout (function still running in background)")
        else:
            body = e.read().decode("utf-8", errors="replace")
            print(f"  FAIL {mode['id']}: HTTP {e.code} {body}", file=sys.stderr)
    except urllib.error.URLError as e:
        # urlopen wraps socket timeouts in URLError too
        if isinstance(e.reason, (socket.timeout, TimeoutError)):
            print(f"  ... {mode['id']}: read timed out (function still running in background)")
        else:
            print(f"  FAIL {mode['id']}: {e.reason}", file=sys.stderr)


def verify_seed(env: str, expected_ids: list) -> None:
    """Polls /listGameModes after submission to confirm modes landed."""
    url = f"{FUNCTIONS_URL}/listGameModes?env={env}"
    print(f"\nVerifying seed by polling {url}...")
    for attempt in range(1, 13):  # up to 6 minutes
        try:
            with urllib.request.urlopen(url, timeout=20) as resp:
                data = json.loads(resp.read())
                modes = data.get("modes") or {}
                present = [mid for mid in expected_ids if mid in modes]
                missing = [mid for mid in expected_ids if mid not in modes]
                print(f"  attempt {attempt}: present={present}, missing={missing}")
                if not missing:
                    print("  All expected modes are present.")
                    return
        except Exception as ex:
            print(f"  attempt {attempt}: poll failed: {ex}")
        time.sleep(30)
    print("  Some modes did not land in time. Re-run individual seeds with --only.")


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

    # After submitting all modes, poll listGameModes to confirm they landed.
    # Cloud Functions can keep running for up to 9 minutes after the HTTP
    # client times out, so we give them time to finish.
    verify_seed(args.env, [m["id"] for m in targets])

    print("\nDone.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
