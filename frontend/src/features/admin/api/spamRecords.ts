import { apiGet } from "../../../lib/apiClient";

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
