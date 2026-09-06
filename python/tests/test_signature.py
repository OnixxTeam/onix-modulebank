import hashlib
import hmac

from perimeter.signature import sign_hex


def test_signature_matches_a_manually_computed_hmac():
    secret = b"test-secret"
    body = b'{"a":1}'

    expected = hmac.new(secret, body, hashlib.sha256).hexdigest()

    assert sign_hex(secret, body) == expected


def test_signature_is_lowercase_hex():
    signature = sign_hex(b"secret", b"body")

    assert signature == signature.lower()
    assert all(char in "0123456789abcdef" for char in signature)


def test_different_bodies_produce_different_signatures():
    secret = b"secret"

    assert sign_hex(secret, b"body-a") != sign_hex(secret, b"body-b")


def test_different_keys_produce_different_signatures():
    body = b"same-body"

    assert sign_hex(b"key-a", body) != sign_hex(b"key-b", body)


def test_the_key_is_used_as_exact_bytes_not_trimmed():
    body = b"body"

    assert sign_hex(b"secret", body) != sign_hex(b"secret ", body)
