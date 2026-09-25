import { apiGet, apiPost } from "../../../lib/apiClient";

export type ReportType = "LOST" | "FOUND";

export type ClaimItem = {
  id: string;
  type: ReportType;
  title: string;
  category: string;
  description: string;
  date: string;
  location: string;
  photoUrl?: string | null;
  photoSnapshotCaptured?: boolean;
};

export type PairRequest = {
  lostItemId: string;
  foundItemId: string;
};

export type ClaimPreview = {
  lost: ClaimItem;
  found: ClaimItem;
  score: number;
  threshold: number;
  canClaim: boolean;
  claimantRole: ReportType;
  previewVersion: string;
  breakdown: {
    title: number;
    category: number;
    description: number;
    imageDescription: number;
    imageAttributes: number;
  };
};

export type SavedMatch = {
  id: string;
  status: string;
  claimantRole: ReportType;
  score: number;
  createdAt: string;
  lost: ClaimItem;
  found: ClaimItem;
};

export const getClaimCandidates = (
  type: ReportType,
  signal?: AbortSignal,
): Promise<ClaimItem[]> =>
  apiGet<ClaimItem[]>(
    "matching",
    `/api/matches/candidates?type=${type}`,
    signal,
  );

export const previewClaim = (
  pair: PairRequest,
  signal?: AbortSignal,
): Promise<ClaimPreview> =>
  apiPost<PairRequest, ClaimPreview>(
    "matching",
    "/api/matches/preview",
    pair,
    signal,
  );

export const submitClaim = (
  pair: PairRequest,
  previewVersion: string,
): Promise<SavedMatch> =>
  apiPost<
    PairRequest & { previewVersion: string },
    SavedMatch
  >(
    "matching",
    "/api/matches/claim",
    {
      ...pair,
      previewVersion,
    },
  );

export const getMyMatches = (
  page: number,
  signal?: AbortSignal,
): Promise<SavedMatch[]> =>
  apiGet<SavedMatch[]>(
    "matching",
    `/api/matches/mine?page=${page}`,
    signal,
  );

export function matchingError(error: unknown): string {
  const response = error as {
    status?: number;
    body?: {
      error?: string;
    };
  };

  if (response.status === 401) {
    return "Please sign in again.";
  }

  if (response.status === 403) {
    return response.body?.error ??
      "You must use a verified account.";
  }

  return response.body?.error ??
    "The request could not be completed. Please try again.";
}
