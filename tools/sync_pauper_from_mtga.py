#!/usr/bin/env python3
"""
Reads the MTGA card database (SQLite) and uploads the list of commons
to the Firebase Realtime Database for a pauper format.

Usage:
    python sync_pauper_from_mtga.py [options]

Options:
    --format FORMAT     Firebase format key (default: historicpauper)
    --db PATH           Path to MTGA card database .mtga file
                        (auto-detected if not specified)
    --dry-run           Show what would be uploaded without writing to Firebase
    --list-commons      Just print all commons and exit
    --search NAME       Search for a card by name and show all printings
    --firebase-url URL  Firebase Realtime Database URL
                        (default: reads from .env or uses hardcoded)
    --auth-token TOKEN  Firebase auth token (optional, for authenticated writes)

MTGA Rarity values: 1=Basic Land, 2=Common, 3=Uncommon, 4=Rare, 5=Mythic

The script:
1. Opens the MTGA SQLite card database
2. Finds all cards with Rarity=2 (Common) that are primary, non-token cards
3. Resolves English card names from the Localizations_enUS table
4. Uploads grpId -> true to /formats/{format}/legalArenaIds in Firebase
5. Uploads encoded card names to /formats/{format}/legalNames in Firebase
"""

import argparse
import glob
import json
import os
import re
import sqlite3
import sys
import urllib.request
import urllib.error

MTGA_DEFAULT_PATHS = [
    r"C:\Program Files\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
    r"C:\Program Files (x86)\Wizards of the Coast\MTGA\MTGA_Data\Downloads\Raw",
    r"C:\Program Files (x86)\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
    r"C:\Program Files\Steam\steamapps\common\MTGA\MTGA_Data\Downloads\Raw",
]

FIREBASE_URL = "https://mtga-enhancement-suite-default-rtdb.firebaseio.com"

# Cards explicitly banned in Historic Pauper
HISTORIC_PAUPER_BANS = {
    "ancestral mask",
    "cranial ram",
    "galvanic blast",
    "persistent petitioners",
    "refurbished familiar",
    "sneaky snacker",
    "kuldotha rebirth",
}


def find_card_db():
    """Auto-detect the MTGA card database file."""
    for base in MTGA_DEFAULT_PATHS:
        pattern = os.path.join(base, "Raw_CardDatabase_*.mtga")
        matches = glob.glob(pattern)
        if matches:
            return matches[0]
    return None


def encode_firebase_key(key):
    """Encode a string for use as a Firebase Realtime Database key."""
    return (key
        .replace("%", "%25")
        .replace(".", "%2E")
        .replace("$", "%24")
        .replace("#", "%23")
        .replace("[", "%5B")
        .replace("]", "%5D")
        .replace("/", "%2F"))


def get_card_data(db_path):
    """Read all cards from the MTGA database, returning (cards, name_lookup)."""
    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()

    # Build name lookup from Localizations_enUS
    # The Formatted column might be INT (LocId pointer) or contain the text
    # The Loc column contains the actual text
    cursor.execute("SELECT LocId, Loc FROM Localizations_enUS")
    name_lookup = {}
    for row in cursor.fetchall():
        name_lookup[row["LocId"]] = row["Loc"]

    # Get all cards
    cursor.execute("""
        SELECT GrpId, TitleId, AltTitleId, Rarity, ExpansionCode,
               DigitalReleaseSet, IsToken, IsPrimaryCard, IsRebalanced,
               RebalancedCardGrpId, LinkedFaceGrpIds, Order_Title
        FROM Cards
        WHERE IsToken = 0
    """)
    cards = [dict(row) for row in cursor.fetchall()]

    conn.close()
    return cards, name_lookup


def get_commons(cards, name_lookup, banlist=None):
    """Find all card names with at least one common/basic printing,
    then include ALL GrpIds for those names (any printing is legal)."""

    # Pass 1: find names that have at least one common or basic printing
    names_with_common = set()
    card_name_map = {}  # grpId -> name

    for card in cards:
        title_id = card["TitleId"]
        name = name_lookup.get(title_id) or card.get("Order_Title") or f"Unknown_{card['GrpId']}"
        card_name_map[card["GrpId"]] = name

        if card["Rarity"] in (1, 2):  # 1=Basic Land, 2=Common
            name_lower = name.lower()
            if not (banlist and name_lower in banlist):
                names_with_common.add(name_lower)

    # Pass 2: include ALL printings of qualifying card names
    commons = []
    seen_names = set()

    for card in cards:
        if card["IsToken"]:
            continue

        name = card_name_map.get(card["GrpId"], f"Unknown_{card['GrpId']}")
        name_lower = name.lower()

        if name_lower not in names_with_common:
            continue

        rarity_names = {0: "token", 1: "basic", 2: "common", 3: "uncommon", 4: "rare", 5: "mythic"}
        commons.append({
            "grpId": card["GrpId"],
            "name": name,
            "rarity": rarity_names.get(card["Rarity"], str(card["Rarity"])),
            "set": card["ExpansionCode"],
            "digitalSet": card["DigitalReleaseSet"],
            "isPrimary": card["IsPrimaryCard"],
            "isRebalanced": card["IsRebalanced"],
            "orderTitle": card["Order_Title"],
        })
        seen_names.add(name_lower)

    return commons, seen_names


def search_card(cards, name_lookup, search_term):
    """Search for a card and show all printings."""
    search_lower = search_term.lower()
    results = []

    for card in cards:
        title_id = card["TitleId"]
        name = name_lookup.get(title_id) or card.get("Order_Title") or ""
        order_title = card.get("Order_Title") or ""

        if not name and not order_title:
            continue
        if search_lower in name.lower() or search_lower in order_title.lower():
            rarity_names = {0: "Token?", 1: "Basic", 2: "Common", 3: "Uncommon", 4: "Rare", 5: "Mythic"}
            results.append({
                "grpId": card["GrpId"],
                "name": name,
                "rarity": rarity_names.get(card["Rarity"], str(card["Rarity"])),
                "rarityNum": card["Rarity"],
                "set": card["ExpansionCode"],
                "digitalSet": card["DigitalReleaseSet"],
                "isPrimary": card["IsPrimaryCard"],
                "isRebalanced": card["IsRebalanced"],
            })

    return results


def get_admin_access_token(service_account_path):
    """Get a Google OAuth2 access token from a service account key file.
    Uses the google-auth library if available, otherwise falls back to manual JWT."""
    try:
        from google.oauth2 import service_account as sa
        from google.auth.transport.requests import Request

        creds = sa.Credentials.from_service_account_file(
            service_account_path,
            scopes=["https://www.googleapis.com/auth/firebase.database",
                     "https://www.googleapis.com/auth/userinfo.email"]
        )
        creds.refresh(Request())
        return creds.token
    except ImportError:
        pass

    # Fallback: manual JWT signing with PyJWT or cryptography
    import time
    import base64
    import hashlib

    with open(service_account_path) as f:
        sa_info = json.load(f)

    from cryptography.hazmat.primitives import serialization, hashes
    from cryptography.hazmat.primitives.asymmetric import padding

    private_key = serialization.load_pem_private_key(
        sa_info["private_key"].encode(), password=None
    )

    now = int(time.time())
    header = base64.urlsafe_b64encode(json.dumps({"alg": "RS256", "typ": "JWT"}).encode()).rstrip(b"=")
    payload = base64.urlsafe_b64encode(json.dumps({
        "iss": sa_info["client_email"],
        "scope": "https://www.googleapis.com/auth/firebase.database https://www.googleapis.com/auth/userinfo.email",
        "aud": "https://oauth2.googleapis.com/token",
        "iat": now,
        "exp": now + 3600,
    }).encode()).rstrip(b"=")

    signing_input = header + b"." + payload
    signature = private_key.sign(signing_input, padding.PKCS1v15(), hashes.SHA256())
    signature_b64 = base64.urlsafe_b64encode(signature).rstrip(b"=")

    jwt_token = (signing_input + b"." + signature_b64).decode()

    # Exchange JWT for access token
    token_data = urllib.parse.urlencode({
        "grant_type": "urn:ietf:params:oauth:grant-type:jwt-bearer",
        "assertion": jwt_token,
    }).encode()
    req = urllib.request.Request("https://oauth2.googleapis.com/token", data=token_data,
                                 headers={"Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(req) as resp:
        result = json.loads(resp.read())
        return result["access_token"]


def upload_to_firebase(format_key, commons, firebase_url=FIREBASE_URL, auth_token=None, access_token=None):
    """Upload the commons list to Firebase."""
    legal_arena_ids = {}
    legal_names = {}

    for card in commons:
        legal_arena_ids[str(card["grpId"])] = True

        name = card["name"].lower()
        legal_names[encode_firebase_key(name)] = True

        # Handle double-faced cards
        if " // " in name:
            for face in name.split(" // "):
                legal_names[encode_firebase_key(face.strip())] = True

    update = {
        "legalArenaIds": legal_arena_ids,
        "legalNames": legal_names,
        "lastSync": {".sv": "timestamp"},
        "totalCards": len(commons),
        "totalArenaIds": len(legal_arena_ids),
        "totalNames": len(legal_names),
        "source": "mtga-local-db",
    }

    url = f"{firebase_url}/formats/{format_key}.json"
    headers = {"Content-Type": "application/json"}

    if access_token:
        # Service account auth — use Authorization header
        headers["Authorization"] = f"Bearer {access_token}"
    elif auth_token:
        url += f"?auth={auth_token}"

    data = json.dumps(update).encode("utf-8")
    req = urllib.request.Request(url, data=data, method="PUT", headers=headers)

    try:
        with urllib.request.urlopen(req) as resp:
            result = json.loads(resp.read())
            return True, result
    except urllib.error.HTTPError as e:
        return False, f"HTTP {e.code}: {e.read().decode()}"


def main():
    parser = argparse.ArgumentParser(description="Sync MTGA commons to Firebase for pauper formats")
    parser.add_argument("--format", default="historicpauper", help="Firebase format key")
    parser.add_argument("--db", help="Path to MTGA card database .mtga file")
    parser.add_argument("--dry-run", action="store_true", help="Show stats without uploading")
    parser.add_argument("--list-commons", action="store_true", help="List all commons and exit")
    parser.add_argument("--search", help="Search for a card by name")
    parser.add_argument("--firebase-url", default=FIREBASE_URL)
    parser.add_argument("--auth-token", help="Firebase auth token")
    parser.add_argument("--service-account", help="Path to Firebase service account JSON key file")
    parser.add_argument("--no-bans", action="store_true", help="Skip the banlist filter")
    args = parser.parse_args()

    # Find database
    db_path = args.db or find_card_db()
    if not db_path:
        print("ERROR: Could not find MTGA card database. Use --db to specify path.")
        print("Looked in:", MTGA_DEFAULT_PATHS)
        sys.exit(1)

    print(f"Using database: {db_path}")
    cards, name_lookup = get_card_data(db_path)
    print(f"Loaded {len(cards)} cards, {len(name_lookup)} localized names")

    # Search mode
    if args.search:
        results = search_card(cards, name_lookup, args.search)
        if not results:
            print(f"No cards found matching '{args.search}'")
        else:
            print(f"\n=== {len(results)} results for '{args.search}' ===")
            for r in sorted(results, key=lambda x: (x["name"], x["set"])):
                rebal = " [REBALANCED]" if r["isRebalanced"] else ""
                primary = "" if r["isPrimary"] else " [alt]"
                digital = f" (digital: {r['digitalSet']})" if r["digitalSet"] else ""
                print(f"  GrpId={r['grpId']:>6}  {r['rarity']:>8}  {r['set']:>5}{digital}  {r['name']}{rebal}{primary}")
        return

    # Get commons
    banlist = None if args.no_bans else HISTORIC_PAUPER_BANS
    commons, seen_names = get_commons(cards, name_lookup, banlist)
    print(f"Found {len(commons)} common/basic land cards ({len(seen_names)} unique names)")

    if banlist:
        print(f"Excluded {len(banlist)} banned cards: {', '.join(sorted(banlist))}")

    # List mode
    if args.list_commons:
        for card in sorted(commons, key=lambda x: x["name"]):
            print(f"  {card['grpId']:>6}  {card['rarity']:>6}  {card['set']:>5}  {card['name']}")
        return

    # Upload
    if args.dry_run:
        print(f"\n[DRY RUN] Would upload {len(commons)} cards to /formats/{args.format}")
        print(f"  Arena IDs: {len(set(str(c['grpId']) for c in commons))}")
        print(f"  Unique names: {len(seen_names)}")
    else:
        access_token = None
        if args.service_account:
            print(f"Authenticating with service account: {args.service_account}")
            access_token = get_admin_access_token(args.service_account)
            print("Got admin access token")

        print(f"\nUploading to /formats/{args.format}...")
        success, result = upload_to_firebase(args.format, commons, args.firebase_url, args.auth_token, access_token)
        if success:
            print(f"SUCCESS: Uploaded {len(commons)} cards")
        else:
            print(f"FAILED: {result}")
            sys.exit(1)


if __name__ == "__main__":
    main()
