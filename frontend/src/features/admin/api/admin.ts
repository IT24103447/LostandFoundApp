import { apiGet, apiPost } from "../../../lib/apiClient";

export type AdminUser = {
  id: string;
  email: string;
  name: string;
  phoneNo: string;
  isAdmin: boolean;
  isEmailVerified: boolean;
  isKicked: boolean;
  createdAt: string;
};

export type UsersResponse = {
  users: AdminUser[];
  total: number;
  page: number;
  pageSize: number;
  totalPages: number;
};

export type UsersParams = {
  search?: string;
  isKicked?: boolean;
  isVerified?: boolean;
  page?: number;
  pageSize?: number;
};

export const getUsers = (params: UsersParams, signal?: AbortSignal) => {
  const searchParams = new URLSearchParams();
  if (params.search) searchParams.set("search", params.search);
  if (params.isKicked !== undefined) searchParams.set("isKicked", String(params.isKicked));
  if (params.isVerified !== undefined) searchParams.set("isVerified", String(params.isVerified));
  if (params.page) searchParams.set("page", String(params.page));
  if (params.pageSize) searchParams.set("pageSize", String(params.pageSize));
  const qs = searchParams.toString();
  const path = `/api/admin/users${qs ? `?${qs}` : ""}`;
  return apiGet<UsersResponse>("admin", path, signal);
};

export const kickUser = (id: string, signal?: AbortSignal) =>
  apiPost<{ confirm: boolean }, { success: boolean; message: string }>(
    "admin",
    `/api/admin/users/${id}/kick`,
    { confirm: true },
    signal,
  );

export const unkickUser = (id: string, signal?: AbortSignal) =>
  apiPost<{ confirm: boolean }, { success: boolean; message: string }>(
    "admin",
    `/api/admin/users/${id}/unkick`,
    { confirm: true },
    signal,
  );
