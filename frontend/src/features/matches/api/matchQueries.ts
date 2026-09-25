import { apiGet } from "../../../lib/apiClient";
import type { ClaimItem, ReportType } from "./matches";

export type MatchSection =
  | "waiting-on-you"
  | "waiting-on-other"
  | "confirmed"
  | "rejected"
  | "deactivated";

export type MatchStatus =
  | "LOST_REPORTER_CONFIRMED"
  | "FINDER_CONFIRMED"
  | "CONFIRMED"
  | "REJECTED"
  | "DEACTIVATED";

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
  deactivatedAt?: string | null;
  deactivationReason?: string | null;
  deactivatedItemId?: string | null;
  deactivatedItemType?: ReportType | null;
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
    case "DEACTIVATED":
      return "Deactivated";
  }
}

export function deactivationLabel(match: MatchListEntry): string {
  switch (match.deactivationReason) {
    case "ITEM_DELETED":
      return "Report deleted";
    case "ITEM_RESOLVED":
      return "Report resolved";
    case "MATCH_CONFIRMED_ELSEWHERE":
      return "Another match confirmed";
    default:
      return "Match deactivated";
  }
}

export function deactivationMessage(match: MatchListEntry): string {
  const affectedItem = [match.lost, match.found].find(
    (item) =>
      item.id === match.deactivatedItemId &&
      (!match.deactivatedItemType ||
        item.type === match.deactivatedItemType),
  );

  const report = affectedItem
    ? `${affectedItem.type === match.yourRole ? "Your" : "Their"} report "${affectedItem.title}"`
    : "A report involved in this match";

  switch (match.deactivationReason) {
    case "ITEM_DELETED":
      return `${report} was deleted. This match is closed.`;
    case "ITEM_RESOLVED":
      return `${report} was marked as resolved. This match is closed.`;
    case "MATCH_CONFIRMED_ELSEWHERE":
      return `${report} was confirmed in another match. This claim is now closed.`;
    default:
      return "This match is no longer active. Its saved report details remain available.";
  }
}

export function isClosedMatch(match: MatchListEntry): boolean {
  return (
    match.status === "CONFIRMED" ||
    match.status === "REJECTED" ||
    match.status === "DEACTIVATED"
  );
}