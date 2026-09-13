import { API_BASE_URLS, type ApiService } from "../config/env";

export type ApiError = {
  status: number;
  body: unknown;
};

let onAuthFailure: (() => void) | null = null;

// In-memory cross-service JWT. Held ONLY in memory (never localStorage) so it keeps the
// same XSS exposure profile as HttpOnly cookies. AuthService's own calls stay cookie-based;
// other services get "Authorization: Bearer <token>" because they can't receive the
// host-only auth cookie on their own origins.
let apiAuthToken: string | null = null;

export function setApiAuthToken(token: string | null) {
  apiAuthToken = token;
}

function authHeaders(service: ApiService): Record<string, string> {
  if (service === "auth" || !apiAuthToken) return {};
  return { Authorization: `Bearer ${apiAuthToken}` };
}

export function setOnAuthFailure(cb: (() => void) | null) {
  onAuthFailure = cb;
}

export async function apiPost<TReq, TRes>(
  service: ApiService,
  path: string,
  body: TReq,
  signal?: AbortSignal,
): Promise<TRes> {
  const url = `${API_BASE_URLS[service]}${path}`;
  const res = await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders(service) },
    body: JSON.stringify(body),
    signal,
    credentials: "include",
  });

  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;

  if (!res.ok) {
    handleAuthFailure(res.status, respBody);
    throw { status: res.status, body: respBody } as ApiError;
  }
  return respBody as TRes;
}

/**
 * POST a multipart/form-data body (e.g. a form with optional file uploads).
 * Do NOT set a Content-Type header here — the browser sets the correct
 * multipart boundary automatically when the body is a FormData instance.
 */
export async function apiPostForm<TRes>(
  service: ApiService,
  path: string,
  formData: FormData,
  signal?: AbortSignal,
): Promise<TRes> {
  const url = `${API_BASE_URLS[service]}${path}`;
  const res = await fetch(url, {
    method: "POST",
    body: formData,
    headers: authHeaders(service),
    signal,
    credentials: "include",
  });

  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;

  if (!res.ok) {
    handleAuthFailure(res.status, respBody);
    throw { status: res.status, body: respBody } as ApiError;
  }
  return respBody as TRes;
}

/**
 * PUT a multipart/form-data body (e.g. replacing a photo on an existing item).
 * Do NOT set a Content-Type header here — the browser sets the correct
 * multipart boundary automatically when the body is a FormData instance.
 */
export async function apiPutForm<TRes>(
  service: ApiService,
  path: string,
  formData: FormData,
  signal?: AbortSignal,
): Promise<TRes> {
  const url = `${API_BASE_URLS[service]}${path}`;
  const res = await fetch(url, {
    method: "PUT",
    body: formData,
    headers: authHeaders(service),
    signal,
    credentials: "include",
  });

  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;

  if (!res.ok) {
    handleAuthFailure(res.status, respBody);
    throw { status: res.status, body: respBody } as ApiError;
  }
  return respBody as TRes;
}

export async function apiGet<TRes>(
  service: ApiService,
  path: string,
  signal?: AbortSignal,
): Promise<TRes> {
  const url = `${API_BASE_URLS[service]}${path}`;
  const res = await fetch(url, { method: "GET", headers: authHeaders(service), signal, credentials: "include" });
  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;
  if (!res.ok) {
    handleAuthFailure(res.status, respBody);
    throw { status: res.status, body: respBody } as ApiError;
  }
  return respBody as TRes;
}

export async function apiPut<TReq, TRes>(
  service: ApiService,
  path: string,
  body: TReq,
  signal?: AbortSignal,
): Promise<TRes> {
  const url = `${API_BASE_URLS[service]}${path}`;
  const res = await fetch(url, {
    method: "PUT",
    headers: { "Content-Type": "application/json", ...authHeaders(service) },
    body: JSON.stringify(body),
    signal,
    credentials: "include",
  });

  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;

  if (!res.ok) {
    handleAuthFailure(res.status, respBody);
    throw { status: res.status, body: respBody } as ApiError;
  }
  return respBody as TRes;
}

export async function apiDelete<TReq, TRes>(
  service: ApiService,
  path: string,
  body: TReq,
  signal?: AbortSignal,
): Promise<TRes> {
  const url = `${API_BASE_URLS[service]}${path}`;
  const res = await fetch(url, {
    method: "DELETE",
    headers: { "Content-Type": "application/json", ...authHeaders(service) },
    body: JSON.stringify(body),
    signal,
    credentials: "include",
  });

  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;

  if (!res.ok) {
    handleAuthFailure(res.status, respBody);
    throw { status: res.status, body: respBody } as ApiError;
  }
  return respBody as TRes;
}

function handleAuthFailure(status: number, body: unknown) {
  if (status === 403) {
    const msg = typeof body === "object" && body !== null && "error" in body
      ? String((body as { error: unknown }).error)
      : "";
    if (msg.includes("Account suspended")) onAuthFailure?.();
  }
}

function safeParseJson(s: string): unknown {
  try {
    return JSON.parse(s);
  } catch {
    return s;
  }
}