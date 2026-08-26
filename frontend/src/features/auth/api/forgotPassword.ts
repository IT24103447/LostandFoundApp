import { apiPost } from "../../../lib/apiClient";

export type ForgotPasswordRequest = {
  email: string;
};

export type ForgotPasswordResponse = {
  sessionToken: string | null;
};

export type ResetPasswordRequest = {
  sessionToken: string;
  code: string;
  newPassword: string;
};

export type ResetPasswordResponse = {
  success: boolean;
};

export const forgotPassword = (body: ForgotPasswordRequest, signal?: AbortSignal) =>
  apiPost<ForgotPasswordRequest, ForgotPasswordResponse>(
    "auth",
    "/api/auth/forgot-password",
    body,
    signal,
  );

export const resetPassword = (body: ResetPasswordRequest, signal?: AbortSignal) =>
  apiPost<ResetPasswordRequest, ResetPasswordResponse>(
    "auth",
    "/api/auth/reset-password",
    body,
    signal,
  );
