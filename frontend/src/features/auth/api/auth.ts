import { apiPost, apiGet } from "../../../lib/apiClient";

export type LoginRequest = {
  email: string;
  password: string;
};

export type UserProfile = {
  id: string;
  email: string;
  name: string;
  phoneNo: string;
  isAdmin: boolean;
  isEmailVerified: boolean;
  createdAt: string;
};

export const login = (body: LoginRequest, signal?: AbortSignal) =>
  apiPost<LoginRequest, UserProfile>("auth", "/api/auth/login", body, signal);

export const logout = (signal?: AbortSignal) =>
  apiPost<undefined, { success: boolean }>("auth", "/api/auth/logout", undefined, signal);

export const getMe = (signal?: AbortSignal) =>
  apiGet<UserProfile>("auth", "/api/auth/me", signal);
