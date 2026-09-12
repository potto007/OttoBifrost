"""Upload a built package zip to Hexium through its Thunderstore-compatible API.

Usage: hexium_upload.py <package.zip> [--dry-run]

Reads the token from HEXIUM_TOKEN. --dry-run uploads the parts and then aborts, so the
token and the upload path are proven without submitting a version, which is permanent.
"""
import json
import os
import sys
import urllib.error
import urllib.request

# Hexium takes the community from the subdomain, not from the request body.
COMMUNITY = "valheim"
BASE = f"https://{COMMUNITY}.hexium.gg/api/experimental/"
TEAM = os.environ.get("HEXIUM_TEAM") or "potto007"
CATEGORIES = [c.strip() for c in os.environ.get("HEXIUM_CATEGORIES", "").split(",") if c.strip()] or [
    "Transportation", "Quality of Life", "Valheim 1.0", "Open Source"]


def call(method, url, token=None, body=None, raw=None):
    headers = {}
    payload = raw
    if body is not None:
        payload = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    elif raw is not None:
        headers["Content-Type"] = "application/octet-stream"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, data=payload, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return response.headers, response.read()
    except urllib.error.HTTPError as error:
        detail = error.read().decode(errors="replace")[:500]
        # Presigned part URLs carry a signature in the query string, so it stays out of logs.
        raise RuntimeError(f"{method} {url.split('?')[0]} failed: HTTP {error.code}: {detail}") from None


def main(zip_path, dry_run):
    token = os.environ.get("HEXIUM_TOKEN", "").strip()
    if not token:
        raise RuntimeError("HEXIUM_TOKEN is not set")

    with open(zip_path, "rb") as package:
        data = package.read()

    _, body = call("POST", BASE + "usermedia/initiate-upload/", token,
                   {"filename": os.path.basename(zip_path), "file_size_bytes": len(data)})
    upload = json.loads(body)
    uuid = upload["user_media"]["uuid"]

    try:
        parts = []
        for part in sorted(upload["upload_urls"], key=lambda p: p["part_number"]):
            chunk = data[part["offset"]:part["offset"] + part["length"]]
            # The presigned URL is its own authorization. An extra header can break the signature.
            headers, _ = call("PUT", part["url"], raw=chunk)
            etag = headers.get("ETag")
            if not etag:
                raise RuntimeError(f"part {part['part_number']} returned no ETag")
            parts.append({"ETag": etag, "PartNumber": part["part_number"]})
        print(f"Uploaded {len(parts)} part(s), {len(data)} bytes")

        if dry_run:
            call("POST", BASE + f"usermedia/{uuid}/abort-upload/", token, {})
            print("Dry run: upload aborted, nothing submitted")
            return

        call("POST", BASE + f"usermedia/{uuid}/finish-upload/", token, {"parts": parts})
    except Exception:
        if not dry_run:
            try:
                call("POST", BASE + f"usermedia/{uuid}/abort-upload/", token, {})
            except RuntimeError:
                pass
        raise

    _, body = call("POST", BASE + "submission/submit/", token, {
        "author_name": TEAM,
        "communities": [COMMUNITY],
        "categories": CATEGORIES,
        "has_nsfw_content": False,
        "upload_uuid": uuid,
    })
    version = json.loads(body).get("package_version") or {}
    print(f"Published to Hexium: {version.get('full_name')} {version.get('version_number')}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    try:
        main(sys.argv[1], "--dry-run" in sys.argv[2:])
    except RuntimeError as error:
        print(f"::error::Hexium upload: {error}", file=sys.stderr)
        sys.exit(1)
