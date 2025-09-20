#!/usr/bin/env python3
# abysscli.py
"""
Abyss CLI — Python 3 实现
Commands:
  open <baseUrl> <user> <privateKeyBase64>
  destroy <baseUrl> <token>
  valid <baseUrl> <token>
  create <baseUrl> <user> <privateKeyBase64> <newUsername> <privilege>
"""
from __future__ import annotations
import sys
import argparse
import base64
import json
import typing as t
from urllib.parse import quote
import requests
from requests import Session
from nacl.signing import SigningKey
from nacl.exceptions import BadSignatureError

# ---- Utilities for Ed25519 handling ----
def generate_keypair_base64() -> t.Tuple[str, str]:
    """
    Generate Ed25519 keypair.
    Returns (private_base64, public_base64).
    private is encoded as 64 bytes: seed(32) || pub(32) to align with many raw-private formats.
    public is 32 bytes.
    """
    sk = SigningKey.generate()
    seed = sk.encode()  # 32 bytes seed
    vk = sk.verify_key.encode()  # 32 bytes pubkey
    priv_raw = seed + vk  # 64 bytes
    return base64.b64encode(priv_raw).decode('ascii'), base64.b64encode(vk).decode('ascii')

def sign_with_private_base64(private_base64: str, data: bytes) -> str:
    """
    Accept private key as base64. Supports:
      - 32-byte seed (seed only)
      - 64-byte raw private (seed + pub)
    Returns base64(signature).
    """
    try:
        raw = base64.b64decode(private_base64)
    except Exception as e:
        raise ValueError(f"privateKeyBase64 is not valid base64: {e}")
    if len(raw) == 32:
        seed = raw
    elif len(raw) == 64:
        seed = raw[:32]
    else:
        raise ValueError(f"Unsupported private key length: {len(raw)} bytes (expected 32 or 64)")
    sk = SigningKey(seed)
    sig = sk.sign(data).signature  # 64 bytes
    return base64.b64encode(sig).decode('ascii')

# ---- HTTP helpers ----
def create_session(base_url: str) -> Session:
    s = requests.Session()
    base = base_url.rstrip('/')
    s.headers.update({'User-Agent': 'AbyssCli-Python/1.0'})
    s.base_url = base + '/'  # attach attribute for convenience
    return s

def _full_url(session: Session, path: str) -> str:
    base = getattr(session, 'base_url', '')
    # ensure no double slashes issues
    return base + path.lstrip('/')

def try_read_response_text(resp: requests.Response) -> str:
    try:
        return resp.text or ""
    except Exception:
        return ""

def parse_possibly_json_string(text: str) -> str:
    """
    Server sometimes returns a JSON-encoded string like: "username"
    Try json.loads first, fall back to trimming quotes.
    """
    if text is None:
        return ""
    text = text.strip()
    if not text:
        return ""
    try:
        parsed = json.loads(text)
        # If parsed is a string, return it; otherwise return original trimmed
        if isinstance(parsed, str):
            return parsed
        # otherwise return textual representation
        return str(parsed)
    except Exception:
        # fallback trim quotes only at ends if present
        if text.startswith('"') and text.endswith('"') and len(text) >= 2:
            return text[1:-1]
        return text

# ---- Command implementations ----
def cmd_open(args: argparse.Namespace) -> int:
    base = args.baseUrl
    user = args.user
    priv_base64 = args.privateKeyBase64

    sess = create_session(base)

    # 1. GET challenge
    url = _full_url(sess, f"api/user/{quote(user, safe='')}")
    try:
        r = sess.get(url, timeout=15)
    except Exception as e:
        print(f"Failed to GET challenge: {e}", file=sys.stderr)
        return 1
    if not r.ok:
        print(f"Failed to get challenge: HTTP {r.status_code}", file=sys.stderr)
        txt = try_read_response_text(r)
        if txt:
            print(txt, file=sys.stderr)
        return 1

    challenge_text = try_read_response_text(r)
    challenge = parse_possibly_json_string(challenge_text)
    # challenge is expected to be base64-encoded bytes
    try:
        challenge_bytes = base64.b64decode(challenge)
    except Exception:
        print("Challenge is not valid base64.", file=sys.stderr)
        return 1

    # 2. Sign
    try:
        signature_base64 = sign_with_private_base64(priv_base64, challenge_bytes)
    except Exception as e:
        print(f"Signing failed: {e}", file=sys.stderr)
        return 1

    # 3. POST response to get token
    post_url = _full_url(sess, f"api/user/{quote(user, safe='')}")
    payload = {"Response": signature_base64}
    try:
        r2 = sess.post(post_url, json=payload, timeout=15)
    except Exception as e:
        print(f"Failed to POST response: {e}", file=sys.stderr)
        return 1
    if not r2.ok:
        print(f"Authentication failed: HTTP {r2.status_code}", file=sys.stderr)
        txt = try_read_response_text(r2)
        if txt:
            print(txt, file=sys.stderr)
        return 1

    token_text = try_read_response_text(r2)
    token = parse_possibly_json_string(token_text)
    if not token:
        print("Authentication failed or server returned no token.", file=sys.stderr)
        return 1

    print(token)
    return 0

def cmd_destroy(args: argparse.Namespace) -> int:
    base = args.baseUrl
    token = args.token
    sess = create_session(base)
    url = _full_url(sess, f"api/user/destroy?token={quote(token, safe='')}")
    try:
        r = sess.post(url, timeout=15)
    except Exception as e:
        print(f"Destroy request failed: {e}", file=sys.stderr)
        return 1
    if not r.ok:
        print(f"Destroy failed: HTTP {r.status_code}", file=sys.stderr)
        txt = try_read_response_text(r)
        if txt:
            print(txt, file=sys.stderr)
        return 1
    # some servers return body, but original prints "Success"
    print("Success")
    return 0

def cmd_valid(args: argparse.Namespace) -> int:
    base = args.baseUrl
    token = args.token
    sess = create_session(base)
    url = _full_url(sess, f"api/user/validate?token={quote(token, safe='')}")
    try:
        r = sess.post(url, timeout=15)
    except Exception as e:
        print(f"Validate request failed: {e}", file=sys.stderr)
        return 1
    if not r.ok:
        print("Invalid")
        return 1
    content = try_read_response_text(r)
    username = parse_possibly_json_string(content)
    if not username:
        print("Invalid")
        return 1
    print(username)
    return 0

def cmd_create(args: argparse.Namespace) -> int:
    base = args.baseUrl
    user = args.user
    priv_base64 = args.privateKeyBase64
    new_username = args.newUsername
    privilege = args.privilege

    sess = create_session(base)

    # 1. Get challenge for creator
    url = _full_url(sess, f"api/user/{quote(user, safe='')}")
    try:
        r = sess.get(url, timeout=15)
    except Exception as e:
        print(f"Failed to GET challenge for creator: {e}", file=sys.stderr)
        return 1
    if not r.ok:
        print(f"Failed to get challenge for creator: HTTP {r.status_code}", file=sys.stderr)
        txt = try_read_response_text(r)
        if txt:
            print(txt, file=sys.stderr)
        return 1
    challenge_text = try_read_response_text(r)
    challenge = parse_possibly_json_string(challenge_text)
    try:
        challenge_bytes = base64.b64decode(challenge)
    except Exception:
        print("Challenge is not valid base64.", file=sys.stderr)
        return 1

    # 2. Sign challenge with creator private key
    try:
        signature_base64 = sign_with_private_base64(priv_base64, challenge_bytes)
    except Exception as e:
        print(f"Signing failed: {e}", file=sys.stderr)
        return 1

    # 3. Generate key pair for new user
    new_priv_b64, new_pub_b64 = generate_keypair_base64()

    # 4. Build create payload and PATCH
    payload = {
        "Response": signature_base64,
        "Name": new_username,
        "Parent": user,
        "Privilege": int(privilege),
        "PublicKey": new_pub_b64
    }
    patch_url = _full_url(sess, f"api/user/{quote(user, safe='')}")
    try:
        r2 = sess.request("PATCH", patch_url, json=payload, timeout=15)
    except Exception as e:
        print(f"Create request failed: {e}", file=sys.stderr)
        return 1
    resp_text = try_read_response_text(r2)
    if not r2.ok:
        print(f"Create failed: HTTP {r2.status_code}", file=sys.stderr)
        if resp_text:
            print(resp_text, file=sys.stderr)
        return 1

    print("Success")
    print("NewUserPrivateKeyBase64:")
    print(new_priv_b64)
    print("NewUserPublicKeyBase64:")
    print(new_pub_b64)
    return 0

# ---- CLI entrypoint ----
def main(argv: t.Optional[t.List[str]] = None) -> int:
    if argv is None:
        argv = sys.argv[1:]
    parser = argparse.ArgumentParser(prog="AbyssCli", description="Abyss CLI (Python)")
    sub = parser.add_subparsers(dest="cmd")

    p_open = sub.add_parser("open", help="open <baseUrl> <user> <privateKeyBase64>")
    p_open.add_argument("baseUrl")
    p_open.add_argument("user")
    p_open.add_argument("privateKeyBase64")

    p_destroy = sub.add_parser("destroy", help="destroy <baseUrl> <token>")
    p_destroy.add_argument("baseUrl")
    p_destroy.add_argument("token")

    p_valid = sub.add_parser("valid", help="valid <baseUrl> <token>")
    p_valid.add_argument("baseUrl")
    p_valid.add_argument("token")

    p_create = sub.add_parser("create", help="create <baseUrl> <user> <privateKeyBase64> <newUsername> <privilege>")
    p_create.add_argument("baseUrl")
    p_create.add_argument("user")
    p_create.add_argument("privateKeyBase64")
    p_create.add_argument("newUsername")
    p_create.add_argument("privilege", type=int)

    if not argv:
        parser.print_help()
        return 1
    args = parser.parse_args(argv)

    try:
        if args.cmd == "open":
            return cmd_open(args)
        elif args.cmd == "destroy":
            return cmd_destroy(args)
        elif args.cmd == "valid":
            return cmd_valid(args)
        elif args.cmd == "create":
            return cmd_create(args)
        else:
            print("Unknown command.", file=sys.stderr)
            parser.print_help()
            return 2
    except Exception as ex:
        print(f"Error: {ex}", file=sys.stderr)
        return 3

if __name__ == "__main__":
    sys.exit(main())
