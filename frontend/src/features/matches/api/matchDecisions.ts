import { apiGet, apiPost } from "../../../lib/apiClient";

export type LostReporterDecision = "confirm" | "reject";

export type MatchDecisionResult = {
  matchId: string;
  status: "CONFIRMED" | "REJECTED";
  decidedAt: string;
};

export type FinderReturnContact = {
  email: string;
  phone: string;
};

export function decideAsLostReporter(
  matchId: string,
  decision: LostReporterDecision,
): Promise<MatchDecisionResult> {
  return apiPost<Record<string, never>, MatchDecisionResult>(
    "matching",
    `/api/matches/${encodeURIComponent(matchId)}/lost-reporter/${decision}`,
    {},
  );
}

export function getFinderReturnContact(
  matchId: string,
  signal?: AbortSignal,
): Promise<FinderReturnContact> {
  return apiGet<FinderReturnContact>(
    "matching",
    `/api/matches/${encodeURIComponent(matchId)}/lost-reporter/return-contact`,
    signal,
  );
}