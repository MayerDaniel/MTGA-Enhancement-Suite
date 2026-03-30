#!/usr/bin/env python3
"""
Signs release files and produces a manifest.json with SHA-256 hashes
and an Ed25519 signature. Upload manifest.json alongside release assets.

Usage:
    python sign_release.py <version> [--key signing_key.pem]

Example:
    python sign_release.py 0.8.0
    # Produces release/manifest.json signed with signing_key.pem
"""

import argparse
import hashlib
import json
import base64
import os
import sys

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives import serialization


RELEASE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "release")
DEFAULT_KEY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "signing_key.pem")

RELEASE_FILES = [
    "MTGAEnhancementSuite.dll",
    "MTGAESBootstrapper.dll",
    "config.json",
]


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            h.update(chunk)
    return h.hexdigest()


def load_private_key(key_path):
    with open(key_path, "rb") as f:
        return serialization.load_pem_private_key(f.read(), password=None)


def main():
    parser = argparse.ArgumentParser(description="Sign release files")
    parser.add_argument("version", help="Release version (e.g. 0.8.0)")
    parser.add_argument("--key", default=DEFAULT_KEY, help="Path to Ed25519 private key PEM")
    args = parser.parse_args()

    if not os.path.exists(args.key):
        print(f"ERROR: Private key not found at {args.key}")
        sys.exit(1)

    # Compute hashes
    files = {}
    for filename in RELEASE_FILES:
        filepath = os.path.join(RELEASE_DIR, filename)
        if not os.path.exists(filepath):
            print(f"WARNING: {filename} not found in {RELEASE_DIR}, skipping")
            continue
        files[filename] = sha256_file(filepath)
        print(f"  {filename}: {files[filename]}")

    if not files:
        print("ERROR: No release files found")
        sys.exit(1)

    # Build the manifest content to sign (deterministic JSON, no signature field)
    manifest_content = {
        "version": args.version,
        "files": files,
    }
    # Canonical JSON for signing (sorted keys, no extra whitespace)
    content_to_sign = json.dumps(manifest_content, sort_keys=True, separators=(",", ":"))

    # Sign
    private_key = load_private_key(args.key)
    signature = private_key.sign(content_to_sign.encode("utf-8"))
    signature_b64 = base64.b64encode(signature).decode("utf-8")

    # Write final manifest with signature
    manifest = {
        "version": args.version,
        "files": files,
        "signature": signature_b64,
    }
    manifest_path = os.path.join(RELEASE_DIR, "manifest.json")
    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)

    print(f"\nManifest written to {manifest_path}")
    print(f"Signature: {signature_b64[:40]}...")
    print(f"\nUpload manifest.json alongside release assets.")


if __name__ == "__main__":
    main()
