import { apiGet, apiPost } from "../../../lib/apiClient";
import type { AppealStatus } from "../../matches/api/appeals";
import type { ClaimItem, ClaimPreview, ReportType } from "../../matches/api/matches";

export type ScoreBreakdown = ClaimPreview["breakdown"];

export type AdminAppeal = {
  id: string;
  status: AppealStatus;
  appellantId: string;
  appellantRole: ReportType;
  lostReporterId: string;
  finderId: string;
  score: number;
  breakdown: ScoreBreakdown;
  lost: ClaimItem;
  found: ClaimItem;
  note?: string | null;
  createdAt: string;
  decidedBy?: string | null;
  decidedAt?: string | null;
  rejectionReason?: string | null;
};

export type AdminAppealDetail = {
  appeal: AdminAppeal;
  current?: {
    score: number;
    breakdown: ScoreBreakdown;
    lost: ClaimItem;
    found: ClaimItem;
  } | null;
  currentUnavailableReason?: "REPORT_INACTIVE" | "SCORE_UNAVAILABLE" | null;
};

export type UserContact = {
  name: string;
  email: string;
  phoneNo: string;
};

export const ADMIN_APPEAL_PAGE_SIZE = 20;

export const getAdminAppeals = (
  status: AppealStatus,
  page: number,
  signal?: AbortSignal,
) =>
  apiGet<AdminAppeal[]>(
    "matching",
    `/api/admin/match-appeals?status=${status}&page=${page}`,
    signal,
  );

export const openAdminAppeal = (id: string, signal?: AbortSignal) =>
  apiGet<AdminAppealDetail>(
    "matching",
    `/api/admin/match-appeals/${id}`,
    signal,
  );

export const verifyAdminAppeal = (id: string) =>
  apiPost<Record<string, never>, AdminAppeal>(
    "matching",
    `/api/admin/match-appeals/${id}/verify`,
    {},
  );

export const rejectAdminAppeal = (id: string, reason: string) =>
  apiPost<{ reason: string | null }, AdminAppeal>(
    "matching",
    `/api/admin/match-appeals/${id}/reject`,
    { reason: reason.trim() === "" ? null : reason.trim() },
  );

export const getUserContact = (id: string, signal?: AbortSignal) =>
  apiGet<UserContact>("auth", `/api/admin/users/${id}`, signal);
