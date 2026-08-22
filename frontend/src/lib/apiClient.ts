import { API_BASE_URLS, type ApiService } from "../config/env";

export type ApiError = {
  status: number;
  body: unknown;
};

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
  });

  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;

  if (!res.ok) {
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
  const res = await fetch(url, { method: "GET", signal });
  const text = await res.text();
  const respBody = text ? safeParseJson(text) : null;
  if (!res.ok) {
    throw { status: res.status, body: respBody } as ApiError;
  }
  return respBody as TRes;
}

function safeParseJson(s: string): unknown {
  try {
    return JSON.parse(s);
  } catch {
    return s;
  }
}
