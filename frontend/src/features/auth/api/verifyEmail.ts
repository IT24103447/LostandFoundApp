import { apiGet, apiPost } from "../../../lib/apiClient";

export type VerifyEmailRequest = {
  sessionToken: string;
  code: string;
};

export type ResendVerificationRequest = {
  sessionToken: string;
  email: string;
};

export type VerificationStatusResponse = {
  isEmailVerified: boolean;
};

export const verifyEmail = (
  payload: VerifyEmailRequest,
  signal?: AbortSignal,
): Promise<{ verified: boolean; email: string }> =>
  apiPost<VerifyEmailRequest, { verified: boolean; email: string }>(
    "auth",
    "/api/auth/verify-email",
    payload,
    signal,
  );

export const resendVerification = (
  payload: ResendVerificationRequest,
  signal?: AbortSignal,
): Promise<{ sent: boolean }> =>
  apiPost<ResendVerificationRequest, { sent: boolean }>(
    "auth",
    "/api/auth/resend-verification",
    payload,
    signal,
  );

export const getVerificationStatus = (
  sessionToken: string,
  signal?: AbortSignal,
): Promise<VerificationStatusResponse> =>
  apiGet<VerificationStatusResponse>(
    "auth",
    `/api/auth/verification-status?sessionToken=${encodeURIComponent(sessionToken)}`,
    signal,
  );




