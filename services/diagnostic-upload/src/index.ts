const CONTENT_TYPE = "application/vnd.baballonia.diagnostic+encrypted";
const FORMAT_VERSION = "1";
const CONSENT_VERSION = "eye-images-v1";
const MIN_UPLOAD_BYTES = 512;
const MAGIC = new TextEncoder().encode("BABDIAG1");
const REPORT_ID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const SHA256_PATTERN = /^[0-9a-f]{64}$/i;

type ApiPayload = Record<string, string | number | boolean>;

function json(payload: ApiPayload, status = 200, extraHeaders?: HeadersInit): Response {
  const headers = new Headers(extraHeaders);
  headers.set("Content-Type", "application/json; charset=utf-8");
  headers.set("Cache-Control", "no-store");
  headers.set("X-Content-Type-Options", "nosniff");
  headers.set("Referrer-Policy", "no-referrer");
  headers.set("Content-Security-Policy", "default-src 'none'");
  headers.set("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
  return Response.json(payload, { status, headers });
}

function error(code: string, message: string, status: number, extraHeaders?: HeadersInit): Response {
  return json({ ok: false, code, message }, status, extraHeaders);
}

function hexToBytes(value: string): Uint8Array {
  const bytes = new Uint8Array(value.length / 2);
  for (let index = 0; index < bytes.length; index++) {
    const byte = Number.parseInt(value.slice(index * 2, index * 2 + 2), 16);
    if (!Number.isFinite(byte)) throw new TypeError("Invalid hexadecimal value");
    bytes[index] = byte;
  }
  return bytes;
}

function bytesToHex(value: ArrayBuffer | ArrayBufferView): string {
  const bytes = value instanceof ArrayBuffer
    ? new Uint8Array(value)
    : new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return bytesToHex(digest);
}

function randomToken(): string {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return bytesToHex(bytes);
}

async function streamWithValidatedMagic(
  body: ReadableStream<Uint8Array>,
): Promise<ReadableStream<Uint8Array>> {
  const reader = body.getReader();
  const prefix = new Uint8Array(MAGIC.length);
  let prefixLength = 0;
  let remainder: Uint8Array | undefined;

  while (prefixLength < MAGIC.length) {
    const { done, value } = await reader.read();
    if (done || !value) throw new TypeError("Incomplete diagnostic magic");
    const take = Math.min(value.length, MAGIC.length - prefixLength);
    prefix.set(value.subarray(0, take), prefixLength);
    prefixLength += take;
    if (take < value.length) remainder = value.subarray(take);
  }
  for (let index = 0; index < MAGIC.length; index++) {
    if (prefix[index] !== MAGIC[index]) {
      await reader.cancel("invalid diagnostic magic");
      throw new TypeError("Invalid diagnostic magic");
    }
  }

  let prefixPending = true;
  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      if (prefixPending) {
        prefixPending = false;
        controller.enqueue(prefix);
        if (remainder && remainder.length > 0) {
          controller.enqueue(remainder);
          remainder = undefined;
        }
        return;
      }
      const { done, value } = await reader.read();
      if (done) controller.close();
      else if (value) controller.enqueue(value);
    },
    async cancel(reason) {
      await reader.cancel(reason);
    },
  });
}

async function rateLimitKey(request: Request, purpose: string): Promise<string> {
  const address = request.headers.get("CF-Connecting-IP") ?? "unknown";
  return sha256Hex(`${purpose}:${address}`);
}

async function uploadReport(request: Request, env: Env): Promise<Response> {
  const rateKey = await rateLimitKey(request, "upload");
  const rate = await env.REQUEST_RATE_LIMITER.limit({ key: rateKey });
  if (!rate.success) {
    return error("rate_limited", "Too many report uploads. Please wait and try again.", 429, {
      "Retry-After": "60",
    });
  }

  if (request.headers.get("Content-Type")?.toLowerCase() !== CONTENT_TYPE) {
    return error("unsupported_content_type", "The report content type is not supported.", 415);
  }
  if (request.headers.get("Content-Encoding") &&
      request.headers.get("Content-Encoding")?.toLowerCase() !== "identity") {
    return error("unsupported_content_encoding", "Compressed HTTP request bodies are not accepted.", 415);
  }
  if (request.headers.get("X-Baballonia-Format") !== FORMAT_VERSION) {
    return error("unsupported_format", "The encrypted report format is not supported.", 400);
  }
  if (request.headers.get("X-Baballonia-Consent") !== CONSENT_VERSION) {
    return error("consent_required", "Explicit consent for the eye-image report is required.", 400);
  }

  const contentLengthText = request.headers.get("Content-Length");
  if (!contentLengthText || !/^\d+$/.test(contentLengthText)) {
    return error("content_length_required", "A valid Content-Length header is required.", 411);
  }
  const contentLength = Number(contentLengthText);
  const maxUploadBytes = Number(env.MAX_UPLOAD_BYTES);
  if (!Number.isSafeInteger(contentLength) || contentLength < MIN_UPLOAD_BYTES) {
    return error("report_too_small", "The encrypted report is incomplete.", 400);
  }
  if (!Number.isSafeInteger(maxUploadBytes) || contentLength > maxUploadBytes) {
    return error("report_too_large", "The encrypted report exceeds the upload limit.", 413);
  }

  const checksum = request.headers.get("X-Content-SHA256") ?? "";
  if (!SHA256_PATTERN.test(checksum)) {
    return error("invalid_checksum", "A valid SHA-256 checksum is required.", 400);
  }
  if (!request.body) return error("body_required", "The encrypted report body is required.", 400);

  const reportId = crypto.randomUUID();
  const objectKey = `reports/${reportId}.babdiag`;
  const deleteToken = randomToken();
  const deleteTokenHash = await sha256Hex(deleteToken);
  const retentionDays = Number(env.RETENTION_DAYS);
  const expiresAt = new Date(Date.now() + retentionDays * 86_400_000);

  let stored: R2Object | null;
  try {
    const validatedBody = await streamWithValidatedMagic(request.body);
    const fixedLengthBody = new FixedLengthStream(contentLength);
    const transfer = validatedBody.pipeTo(fixedLengthBody.writable);
    const put = env.REPORTS.put(objectKey, fixedLengthBody.readable, {
      onlyIf: { etagDoesNotMatch: "*" },
      sha256: hexToBytes(checksum),
      httpMetadata: { contentType: CONTENT_TYPE },
      customMetadata: {
        format: FORMAT_VERSION,
        encryptedSha256: checksum.toLowerCase(),
        deleteTokenHash,
        expiresAt: expiresAt.toISOString(),
      },
    });
    const [transferResult, putResult] = await Promise.allSettled([transfer, put]);
    if (transferResult.status === "rejected") throw transferResult.reason;
    if (putResult.status === "rejected") throw putResult.reason;
    stored = putResult.value;
  } catch (exception) {
    const invalidFormat = exception instanceof TypeError &&
      exception.message.toLowerCase().includes("diagnostic magic");
    if (invalidFormat) return error("invalid_container", "The encrypted report container is invalid.", 400);
    return error("integrity_check_failed", "The report failed its encrypted integrity check.", 422);
  }

  if (!stored || stored.size !== contentLength) {
    await env.REPORTS.delete(objectKey);
    return error("length_mismatch", "The uploaded report length did not match its declaration.", 400);
  }

  return json({
    ok: true,
    reportId,
    deleteToken,
    expiresAt: expiresAt.toISOString(),
  }, 201);
}

function bearerToken(request: Request): string | null {
  const authorization = request.headers.get("Authorization") ?? "";
  const match = /^Bearer ([0-9a-f]{64})$/i.exec(authorization);
  return match?.[1] ?? null;
}

async function deleteReport(request: Request, env: Env, reportId: string): Promise<Response> {
  if (!REPORT_ID_PATTERN.test(reportId)) {
    return error("report_not_found", "The report does not exist or was already deleted.", 404);
  }
  const token = bearerToken(request);
  if (!token) return error("delete_token_required", "A valid deletion token is required.", 401);

  const rateKey = await rateLimitKey(request, "delete");
  const rate = await env.REQUEST_RATE_LIMITER.limit({ key: rateKey });
  if (!rate.success) {
    return error("rate_limited", "Too many report deletion attempts. Please wait and try again.", 429, {
      "Retry-After": "60",
    });
  }

  const objectKey = `reports/${reportId}.babdiag`;
  const object = await env.REPORTS.head(objectKey);
  const expectedHash = object?.customMetadata?.deleteTokenHash;
  if (!object || !expectedHash || !SHA256_PATTERN.test(expectedHash)) {
    return error("report_not_found", "The report does not exist or was already deleted.", 404);
  }

  const actualHash = await sha256Hex(token);
  const matches = crypto.subtle.timingSafeEqual(hexToBytes(actualHash), hexToBytes(expectedHash));
  if (!matches) return error("invalid_delete_token", "The deletion token is invalid.", 403);

  await env.REPORTS.delete(objectKey);
  return json({ ok: true, deleted: true, reportId });
}

async function route(request: Request, env: Env): Promise<Response> {
  const url = new URL(request.url);
  if (request.method === "GET" && url.pathname === "/health") {
    return json({ ok: true, service: "baballonia-diagnostic-upload", formatVersion: 1 });
  }
  if (request.method === "POST" && url.pathname === "/v1/reports") {
    return uploadReport(request, env);
  }
  const deleteMatch = /^\/v1\/reports\/([^/]+)$/.exec(url.pathname);
  if (request.method === "DELETE" && deleteMatch?.[1]) {
    return deleteReport(request, env, deleteMatch[1]);
  }
  if (url.pathname === "/v1/reports" || deleteMatch) {
    return error("method_not_allowed", "The request method is not supported.", 405, {
      Allow: url.pathname === "/v1/reports" ? "POST" : "DELETE",
    });
  }
  return error("not_found", "The requested endpoint does not exist.", 404);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      return await route(request, env);
    } catch {
      return error("internal_error", "The report service could not process the request.", 500);
    }
  },
} satisfies ExportedHandler<Env>;
