import { apiDelete, apiGet, apiPost, apiPut } from "../../../lib/apiClient";

export type SpamReviewTab = "active" | "dismissed" | "solved";

export type SpamReviewSort = "score" | "date";

export type SpamRecordStatus =
  | "NEEDS_REVIEW"
  | "UNDER_REVIEW"
  | "PENDING_SOLVE"
  | "SOLVED"
  | "DISMISSED";

export type SpamRecord = {
  id: string;
  userId: string;
  scoreA: number;
  listingCount: number;
  status: SpamRecordStatus;
  flaggedAt: string;
};

export type SpamRecordPage = {
  items: SpamRecord[];
  hasMore: boolean;
};

export type SpamRecordQuery = {
  tab: SpamReviewTab;
  sort: SpamReviewSort;
  from?: string;
  to?: string;
  userIds?: string[];
  page: number;
};

export const MAX_FILTER_USERS = 100;

export const getSpamRecords = (query: SpamRecordQuery, signal?: AbortSignal) => {
  const params = new URLSearchParams({
    tab: query.tab,
    sort: query.sort,
    page: String(query.page),
  });
  if (query.from) params.set("from", query.from);
  if (query.to) params.set("to", query.to);
  if (query.userIds?.length) params.set("userIds", query.userIds.join(","));

  return apiGet<SpamRecordPage>("admin", `/api/admin/spam-records?${params}`, signal);
};

export const ACTIVE_STATUSES: SpamRecordStatus[] = ["NEEDS_REVIEW", "UNDER_REVIEW", "PENDING_SOLVE"];

export type SolveResult = "DELETED" | "SKIPPED" | "FAILED";

export type SpamRecordListing = {
  listingId: string;
  listingType: "lost" | "found";
  postedAt: string;
  solveResult: SolveResult | null;
};

export type KickStatus = "NOT_REQUESTED" | "PENDING" | "KICKED" | "FAILED";

export type SpamRecordDetail = SpamRecord & {
  kickStatus: KickStatus;
  listings: SpamRecordListing[];
};

export type AdminItem = {
  id: string;
  type: string;
  userId: string;
  title: string;
  description: string;
  status: string;
  createdAt: string;
  deletedAt: string | null;
};

export const getSpamRecord = (id: string, signal?: AbortSignal) =>
  apiGet<SpamRecordDetail>("admin", `/api/admin/spam-records/${id}`, signal);

export const openSpamRecord = (id: string) =>
  apiPost<Record<string, never>, SpamRecordDetail>("admin", `/api/admin/spam-records/${id}/open`, {});

export const getAdminItem = (type: string, id: string, signal?: AbortSignal) =>
  apiGet<AdminItem>("items", `/api/admin/items/${type}/${id}`, signal);

export type AdminItemPhotos = {
  photoUrls: string[];
};

export const getAdminItemPhotos = (type: string, id: string, signal?: AbortSignal) =>
  apiGet<AdminItemPhotos>("items", `/api/admin/items/${type}/${id}/photos`, signal);

export const dismissSpamRecord = (id: string) =>
  apiPost<Record<string, never>, SpamRecordDetail>("admin", `/api/admin/spam-records/${id}/dismiss`, {});

export const startSolve = (id: string, kick: boolean, resume: boolean) =>
  apiPost<{ kick: boolean; resume: boolean }, SpamRecordDetail>("admin", `/api/admin/spam-records/${id}/solve`, { kick, resume });

export const saveListingResult = (id: string, listingId: string, result: SolveResult) =>
  apiPut<{ result: SolveResult }, null>("admin", `/api/admin/spam-records/${id}/listings/${listingId}/result`, { result });

export const saveKickResult = (id: string, result: "KICKED" | "FAILED") =>
  apiPut<{ result: string }, SpamRecordDetail>("admin", `/api/admin/spam-records/${id}/kick-result`, { result });

export const finishSolve = (id: string) =>
  apiPost<Record<string, never>, SpamRecordDetail>("admin", `/api/admin/spam-records/${id}/finish`, {});

export const deleteAdminItem = (type: string, id: string) =>
  apiDelete<Record<string, never>, { message: string }>("items", `/api/admin/items/${type}/${id}`, {});
