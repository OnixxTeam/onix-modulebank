"""HMAC-SHA256 over exact bytes, keyed by the secret's raw UTF-8 bytes.

The key is never trimmed, base64- or hex-decoded (spec.md "Ключом являются
точные UTF-8 bytes `PROVIDER_HMAC_SECRET` без trim, Base64 или hex
decoding") -- ``config.load_adapter_config`` hands this module the secret
exactly as ``str.encode("utf-8")`` produced it.
"""

from __future__ import annotations

import hashlib
import hmac


def sign_hex(secret: bytes, body: bytes) -> str:
    """Lowercase hex digest -- ``hexdigest()`` already returns lowercase."""
    return hmac.new(secret, body, hashlib.sha256).hexdigest()
