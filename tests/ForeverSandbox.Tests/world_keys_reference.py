"""Independent Python derivation of the 70338 world session/encryption keys and the
EnterEncryptedMode signed digest for fixed fixture inputs. Prints JSON golden values
that WorldFixtures.cs embeds. Constants: pinned 5a125dfe WorldSocket.cpp and
AuthenticationPackets.cpp. Standard library only."""
import hashlib
import hmac
import json

SESSION_KEY_SEED = bytes.fromhex("E81E8B5927621EAA861518EAC0BF668C6DBF8393BCAA80525B1EDC23A012B750")
ENCRYPTION_KEY_SEED = bytes.fromhex("71C9ED5AA70E4DFF4C36A65A3E468A4A5DA148C830474ADEF60D6CBE6FE45573")
ENABLE_ENCRYPTION_SEED = bytes.fromhex("66BE2979EFF2D5B56153F65F45AE81CB32EC94EC75B35F446A63436717204434")


def sha512(*parts):
    return hashlib.sha512(b"".join(parts)).digest()


def session_key(key_data, server_challenge, local_challenge, size=40):
    seed = hmac.new(sha512(key_data), server_challenge + local_challenge + SESSION_KEY_SEED, hashlib.sha512).digest()
    o1, o2, o0 = sha512(seed[:32]), sha512(seed[32:]), bytes(64)
    out = b""
    while len(out) < size:
        o0 = sha512(o1, o0, o2)
        out += o0
    return out[:size]


key_data = bytes(range(64))
server_challenge = bytes(range(64, 96))
local_challenge = bytes(range(96, 128))
session = session_key(key_data, server_challenge, local_challenge)
encrypt = hmac.new(session, local_challenge + server_challenge + ENCRYPTION_KEY_SEED, hashlib.sha512).digest()[:32]
signed = hmac.new(encrypt, b"\x01" + ENABLE_ENCRYPTION_SEED, hashlib.sha512).digest()
print(json.dumps({"sessionKey": session.hex().upper(), "encryptKey": encrypt.hex().upper(), "signedDigest": signed.hex().upper()}, indent=2))
