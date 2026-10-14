"""Generate synthetic client-side SRP-v2 transcripts independently of C# server.

Uses the pinned protocol equations and RFC 5054 2048-bit group. All test nonces
are public deterministic fixture data, never live session material.
"""
import hashlib
import json
from pathlib import Path

N_HEX = ('AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A'
         '37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E'
         '8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F'
         '97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA9'
         '7B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544'
         '523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF8'
         '74E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B'
         '5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73')
N = int(N_HEX, 16)
assert N.bit_length() == 2048
pad = lambda number: number.to_bytes(256, 'big')
evidence = lambda number: number.to_bytes((number.bit_length() + 8) // 8, 'big')
H = lambda *parts: hashlib.sha256(b''.join(parts)).digest()
k = int.from_bytes(H(pad(N), pad(2)), 'big')
username = hashlib.sha256(b'ADMIN').hexdigest().upper()
vectors = []
signs = set()
for seed in range(32):
    salt = bytes((i + seed) % 256 for i in range(32))
    password_x = hashlib.pbkdf2_hmac('sha512', (username + ':admin').encode(), salt, 15000, 64)
    negative = bool(password_x[0] & 128)
    if negative in signs:
        continue
    signs.add(negative)
    x = int.from_bytes(password_x, 'big', signed=True) % (N - 1)
    v = pow(2, x, N)
    b = int.from_bytes(bytes((i + seed + 1) % 256 for i in range(256)), 'big') % (N - 1) or 1
    B = (k * v + pow(2, b, N)) % N
    a = int.from_bytes(bytes((255 - i + seed) % 256 for i in range(256)), 'big') % (N - 1) or 1
    A = pow(2, a, N)
    u = int.from_bytes(H(pad(A), pad(B)), 'big')
    # Client equation, intentionally different from the server's (A*v**u)**b.
    S = pow((B - k * pow(2, x, N)) % N, a + u * x, N)
    M1 = H(evidence(A), evidence(B), evidence(S))
    M2 = H(evidence(A), evidence(int.from_bytes(M1, 'big')), evidence(S))
    vectors.append(dict(seed=seed, negativeExponentInput=negative, username=username,
                        salt=salt.hex().upper(), publicB=f'{B:X}', publicA=f'{A:X}',
                        clientM1=M1.hex().upper(), serverM2=M2.hex().upper().lstrip('0') or '0'))
    if len(signs) == 2:
        break
assert len(vectors) == 2
path = Path(__file__).with_name('srp-v2-vectors.json')
path.write_text(json.dumps(dict(synthetic=True, source='independent Python client equations', vectors=vectors), indent=2), encoding='utf-8')
print(f'Wrote {len(vectors)} synthetic transcripts covering positive/negative PBKDF output.')
