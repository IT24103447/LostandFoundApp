import { apiPost } from "../../../lib/apiClient";

export type RegisterRequest = {
  name: string;
  email: string;
  phoneNo: string;
  password: string;
};

export type RegisterResponse = {
  id: string;
  email: string;
  name: string;
  phoneNo: string;
  isAdmin: boolean;
  isEmailVerified: boolean;
  createdAt: string;
  verificationSessionToken: string;
};

export const register = (
  payload: RegisterRequest,
  signal?: AbortSignal,
): Promise<RegisterResponse> =>
  apiPost<RegisterRequest, RegisterResponse>(
    "auth",
    "/api/auth/register",
    payload,
    signal,
  );
