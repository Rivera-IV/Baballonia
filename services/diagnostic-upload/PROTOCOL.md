# Baballonia encrypted diagnostic format

The upload service stores only an end-to-end encrypted binary container. The server never receives
the diagnostic private key and cannot inspect eye images, model data, notes, or calibration values.

## Container version 1

The `.babdiag` file consists of:

1. eight ASCII magic bytes: `BABDIAG1`;
2. a four-byte unsigned little-endian JSON-header length;
3. the UTF-8 JSON header;
4. the encrypted ZIP payload;
5. the 16-byte AEAD authentication tag.

The exact magic, header-length bytes, and JSON-header bytes are supplied as AEAD associated data.
The version-1 JSON header contains only cryptographic routing information:

- `version`: `1`;
- `keyId`: identifies the maintainer public key;
- `keyWrap`: `RSA-OAEP-SHA256`;
- `contentCipher`: `AES-256-GCM`;
- `wrappedKey`: base64-encoded randomly generated content key;
- `nonce`: base64-encoded 12-byte nonce;
- `payload`: `application/zip`.

Names, timestamps, application settings, eye images, notes, and model outputs remain inside the
encrypted ZIP. Each report uses a fresh 256-bit content key and nonce. The maintainer private key
must never be committed, deployed to Cloudflare, or included in Baballonia.

## Upload contract

`POST /v1/reports` requires:

- `Content-Type: application/vnd.baballonia.diagnostic+encrypted`;
- `Content-Length` between 512 bytes and 75 MiB;
- `X-Baballonia-Format: 1`;
- `X-Baballonia-Consent: eye-images-v1`;
- `X-Content-SHA256`: lowercase or uppercase hexadecimal SHA-256 of the complete `.babdiag` file.

The successful response returns a random report ID, an expiry timestamp, and a one-time deletion
token. Only the token hash is stored. There is no public report-download route. If accepting a
report would raise total bucket storage above 8 GB, the service returns HTTP 507 with the code
`storage_limit_reached` and does not retain any part of that report.
