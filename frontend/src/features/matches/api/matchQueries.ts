import { apiGet } from "../../../lib/apiClient";
import type { ClaimItem, ReportType } from "./matches";

export type MatchSection =
  | "waiting-on-you"
  | "waiting-on-other"
  | "confirmed"
  | "rejected";

export type MatchStatus =
  | "LOST_REPORTER_CONFIRMED"
  | "FINDER_CONFIRMED"
  | "CONFIRMED"
  | "REJECTED";

export type MatchListEntry = {
  id: string;
  status: MatchStatus;
  score: number;
  createdAt: string;
  yourRole: ReportType;
  isClaimant: boolean;
  roleLabel: string;
  section: MatchSection;
  isYourTurn: boolean;
  otherItem: ClaimItem;
  lost: ClaimItem;
  found: ClaimItem;
};

export type MatchPage = {
  items: MatchListEntry[];
  page: number;
  size: number;
  totalCount: number;
};

export function getMatchPage(
  section: MatchSection | "active" | "closed" | "all",
  page = 1,
  size = 10,
  signal?: AbortSignal,
): Promise<MatchPage> {
  const query = new URLSearchParams({
    section,
    page: String(page),
    size: String(size),
  });

  return apiGet<MatchPage>(
    "matching",
    `/api/matches?${query.toString()}`,
    signal,
  );
}

export function getMatch(
  matchId: string,
  signal?: AbortSignal,
): Promise<MatchListEntry> {
  return apiGet<MatchListEntry>(
    "matching",
    `/api/matches/${encodeURIComponent(matchId)}`,
    signal,
  );
}

export function matchStatusLabel(status: MatchStatus): string {
  switch (status) {
    case "LOST_REPORTER_CONFIRMED":
      return "Awaiting finder";
    case "FINDER_CONFIRMED":
      return "Awaiting lost reporter";
    case "CONFIRMED":
      return "Confirmed";
    case "REJECTED":
      return "Rejected";
  }
}