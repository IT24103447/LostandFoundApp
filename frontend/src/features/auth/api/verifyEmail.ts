import { apiPost } from "../../../lib/apiClient";

export type VerifyEmailRequest = {
  sessionToken: string;
  code: string;
};

export type RegisterResponse = {
  id: string;
  email: string;
  name: string;
  phoneNo: string;
  isAdmin: boolean;
  isEmailVerified: boolean;
  createdAt: string;
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




