import { apiPost, apiGet, apiPut } from "../../../lib/apiClient";

export type LoginRequest = {
  email: string;
  password: string;
};

export type UpdateProfileRequest = {
  name: string;
  phoneNo: string;
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

export const updateProfile = (body: UpdateProfileRequest, signal?: AbortSignal) =>
  apiPut<UpdateProfileRequest, UserProfile>("auth", "/api/auth/me", body, signal);
