import { apiGet, apiPost } from "../../../lib/apiClient";
import type { ClaimItem, PairRequest, ReportType } from "./matches";

export const APPEAL_NOTE_MAX = 300;

export type AppealStatus = "PENDING" | "VERIFIED" | "REJECTED";

export type MyAppeal = {
  id: string;
  status: AppealStatus;
  role: ReportType;
  score: number;
  lost: ClaimItem;
  found: ClaimItem;
  note?: string | null;
  createdAt: string;
  decidedAt?: string | null;
  rejectionReason?: string | null;
};

export const sendAppeal = (
  pair: PairRequest,
  previewVersion: string,
  note: string,
): Promise<MyAppeal> =>
  apiPost<
    PairRequest & { previewVersion: string; note: string | null },
    MyAppeal
  >(
    "matching",
    "/api/matches/appeals",
    {
      ...pair,
      previewVersion,
      note: note.trim() === "" ? null : note.trim(),
    },
  );

export const getMyAppeals = (
  page: number,
  signal?: AbortSignal,
): Promise<MyAppeal[]> =>
  apiGet<MyAppeal[]>(
    "matching",
    `/api/matches/appeals/mine?page=${page}`,
    signal,
  );

export const getPairAppealStatus = (
  pair: PairRequest,
  signal?: AbortSignal,
): Promise<{ appealed: boolean }> =>
  apiGet<{ appealed: boolean }>(
    "matching",
    `/api/matches/appeals/pair-status?lostItemId=${pair.lostItemId}&foundItemId=${pair.foundItemId}`,
    signal,
  );

export const getAppealEditWarning = (
  type: "lost" | "found",
  id: string,
  signal?: AbortSignal,
): Promise<{ warn: boolean }> =>
  apiGet<{ warn: boolean }>(
    "matching",
    `/api/matches/appeals/edit-warning?type=${type}&id=${id}`,
    signal,
  );
