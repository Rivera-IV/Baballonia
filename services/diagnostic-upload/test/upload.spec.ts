import { env, exports } from "cloudflare:workers";
import { reset } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";

const CONTENT_TYPE = "application/vnd.baballonia.diagnostic+encrypted";

interface UploadResponse {
  ok: boolean;
  reportId: string;
  deleteToken: string;
  expiresAt: string;
}

function diagnosticBytes(validMagic = true): Uint8Array {
  const bytes = new Uint8Array(768);
  const magic = new TextEncoder().encode(validMagic ? "BABDIAG1" : "NOTDIAG1");
  bytes.set(magic);
  for (let index = magic.length; index < bytes.length; index++) bytes[index] = index % 251;
  return bytes;
}

function toHex(value: ArrayBuffer): string {
  return Array.from(new Uint8Array(value), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function checksum(bytes: Uint8Array): Promise<string> {
  return toHex(await crypto.subtle.digest("SHA-256", bytes));
}

async function uploadRequest(bytes: Uint8Array, ip: string, checksumOverride?: string): Promise<Request> {
  return new Request("https://diagnostics.example/v1/reports", {
    method: "POST",
    headers: {
      "Content-Type": CONTENT_TYPE,
      "Content-Length": bytes.length.toString(),
      "X-Baballonia-Format": "1",
      "X-Baballonia-Consent": "eye-images-v1",
      "X-Content-SHA256": checksumOverride ?? await checksum(bytes),
      "CF-Connecting-IP": ip,
    },
    body: bytes,
  });
}

describe.sequential("diagnostic upload service", () => {
  afterEach(async () => {
    await reset();
  });

  it("returns a private, non-cacheable health response", async () => {
    const response = await exports.default.fetch("https://diagnostics.example/health");
    expect(response.status).toBe(200);
    expect(response.headers.get("Cache-Control")).toBe("no-store");
    expect(response.headers.get("X-Content-Type-Options")).toBe("nosniff");
    await expect(response.json()).resolves.toMatchObject({ ok: true, formatVersion: 1 });
  });

  it("rejects a report without explicit eye-image consent", async () => {
    const bytes = diagnosticBytes();
    const request = await uploadRequest(bytes, "198.51.100.10");
    request.headers.delete("X-Baballonia-Consent");
    const response = await exports.default.fetch(request);
    expect(response.status).toBe(400);
    await expect(response.json()).resolves.toMatchObject({ code: "consent_required" });
  });

  it("rejects an invalid encrypted-container magic prefix", async () => {
    const bytes = diagnosticBytes(false);
    const response = await exports.default.fetch(await uploadRequest(bytes, "198.51.100.11"));
    expect(response.status).toBe(400);
    await expect(response.json()).resolves.toMatchObject({ code: "invalid_container" });
  });

  it("rejects a checksum mismatch without retaining an object", async () => {
    const bytes = diagnosticBytes();
    const response = await exports.default.fetch(
      await uploadRequest(bytes, "198.51.100.12", "0".repeat(64)),
    );
    expect(response.status).toBe(422);
    await expect(response.json()).resolves.toMatchObject({ code: "integrity_check_failed" });
  });

  it("stores encrypted bytes and permits deletion only with the returned token", async () => {
    const bytes = diagnosticBytes();
    const upload = await exports.default.fetch(await uploadRequest(bytes, "198.51.100.13"));
    expect(upload.status).toBe(201);
    const receipt = await upload.json<UploadResponse>();
    expect(receipt.ok).toBe(true);
    expect(receipt.reportId).toMatch(/^[0-9a-f-]{36}$/i);
    expect(receipt.deleteToken).toMatch(/^[0-9a-f]{64}$/i);

    const key = `reports/${receipt.reportId}.babdiag`;
    const stored = await env.REPORTS.head(key);
    expect(stored?.size).toBe(bytes.length);
    expect(stored?.customMetadata?.deleteTokenHash).toMatch(/^[0-9a-f]{64}$/i);

    const denied = await exports.default.fetch(new Request(
      `https://diagnostics.example/v1/reports/${receipt.reportId}`,
      { method: "DELETE", headers: { Authorization: `Bearer ${"f".repeat(64)}` } },
    ));
    expect(denied.status).toBe(403);
    expect(await env.REPORTS.head(key)).not.toBeNull();

    const removed = await exports.default.fetch(new Request(
      `https://diagnostics.example/v1/reports/${receipt.reportId}`,
      { method: "DELETE", headers: { Authorization: `Bearer ${receipt.deleteToken}` } },
    ));
    expect(removed.status).toBe(200);
    expect(await env.REPORTS.head(key)).toBeNull();
  });

  it("counts every existing bucket object toward the storage ceiling", async () => {
    await env.REPORTS.put("existing-object", new Uint8Array(700));

    const response = await exports.default.fetch(
      await uploadRequest(diagnosticBytes(), "198.51.100.20"),
    );

    expect(response.status).toBe(507);
    expect(response.headers.get("Retry-After")).toBe("3600");
    await expect(response.json()).resolves.toMatchObject({ code: "storage_limit_reached" });
  });

  it("makes capacity available immediately after a report is deleted", async () => {
    const bytes = diagnosticBytes();
    const upload = await exports.default.fetch(await uploadRequest(bytes, "198.51.100.21"));
    const receipt = await upload.json<UploadResponse>();
    expect(upload.status).toBe(201);

    const full = await exports.default.fetch(await uploadRequest(bytes, "198.51.100.22"));
    expect(full.status).toBe(507);
    await full.json();

    const removed = await exports.default.fetch(new Request(
      `https://diagnostics.example/v1/reports/${receipt.reportId}`,
      { method: "DELETE", headers: { Authorization: `Bearer ${receipt.deleteToken}` } },
    ));
    expect(removed.status).toBe(200);
    await removed.json();

    const retried = await exports.default.fetch(await uploadRequest(bytes, "198.51.100.23"));
    expect(retried.status).toBe(201);
    await retried.json();
  });

  it("serializes concurrent uploads so they cannot exceed the storage ceiling", async () => {
    const [first, second] = await Promise.all([
      exports.default.fetch(await uploadRequest(diagnosticBytes(), "198.51.100.24")),
      exports.default.fetch(await uploadRequest(diagnosticBytes(), "198.51.100.25")),
    ]);

    expect([first.status, second.status].sort()).toEqual([201, 507]);
    await Promise.all([first.json(), second.json()]);

    const listed = await env.REPORTS.list();
    expect(listed.objects).toHaveLength(1);
    expect(listed.objects[0]?.size).toBe(768);
  });

  it("rate limits repeated upload attempts without storing the address", async () => {
    const address = "198.51.100.14";
    for (let attempt = 0; attempt < 3; attempt++) {
      const bytes = diagnosticBytes();
      const request = await uploadRequest(bytes, address);
      request.headers.set("Content-Type", "application/octet-stream");
      const response = await exports.default.fetch(request);
      expect(response.status).toBe(415);
    }
    const response = await exports.default.fetch(await uploadRequest(diagnosticBytes(), address));
    expect(response.status).toBe(429);
    expect(response.headers.get("Retry-After")).toBe("60");
  });
});
