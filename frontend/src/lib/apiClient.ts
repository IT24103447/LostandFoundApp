import { API_BASE_URLS, type ApiService } from "../config/env";

export type ApiError = {
  status: number;
  body: unknown;
};

let onAuthFailure: (() => void) | null = null;

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
    headers: { "Content-Type": "application/json" },
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

export async function apiGet<TRes>(
  service: ApiService,
  path: string,
  signal?: AbortSignal,
): Promise<TRes> {
  const url = `${API_BASE_URLS[service]}${path}`;
  const res = await fetch(url, { method: "GET", signal, credentials: "include" });
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
    headers: { "Content-Type": "application/json" },
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
    headers: { "Content-Type": "application/json" },
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
