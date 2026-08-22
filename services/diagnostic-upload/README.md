# Baballonia Diagnostic Upload

Private upload service for end-to-end encrypted eye-diagnostic reports.

Security boundaries:

- the desktop client must encrypt every report before upload;
- the service must never receive the diagnostic decryption private key;
- the R2 bucket must remain private and use automatic retention limits;
- uploads must be explicit and must never run automatically;
- diagnostic storage and GitHub application updates remain separate systems.

The service accepts `POST /v1/reports`, returns a random report ID plus a one-time deletion token,
and supports early deletion through `DELETE /v1/reports/{reportId}`. It deliberately has no report
download route. Maintainers retrieve an encrypted object through authenticated R2 tooling and
decrypt it locally with the DPAPI-protected private key.

The Worker stores no name, account, plaintext diagnostic field, or source IP in R2. Its rate-limit
key is a one-way hash of the source address, and Worker observability is disabled. Cloudflare still
necessarily processes ordinary network metadata while delivering the HTTPS request.

Production configuration uses a private EU-jurisdiction R2 bucket and a 14-day object lifecycle.
Lifecycle deletion is asynchronous, so Cloudflare may take additional time to remove an expired
object. The uploader receives a deletion token that can remove it sooner.

The Worker enforces an 8 GB bucket-wide storage ceiling, leaving headroom below R2's 10 GB-month
free tier. A single Durable Object serializes the low-volume upload admission check and R2 write,
so concurrent uploads cannot overrun the ceiling. The check lists the actual bucket contents, which
means existing objects, manual deletions, and lifecycle deletions are reflected automatically.

Checks:

```powershell
npm ci
npm run check
npm run deploy:dry
```

Deployment requires R2 to be enabled on the target Cloudflare account.
