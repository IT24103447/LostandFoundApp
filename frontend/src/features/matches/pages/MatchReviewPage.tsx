import { useCallback } from "react";
import { Link, useParams } from "react-router-dom";
import { AppHeader } from "../../items/layout/AppHeader";
import { getMatch } from "../api/matchQueries";
import MatchBanner from "../components/MatchBanner";
import { MatchReportComparison } from "../components/MatchReportComparison";
import { useMatchRequest } from "../hooks/useMatchRequest";

export function MatchReviewPage() {
  const { matchId } = useParams<{ matchId: string }>();

  const load = useCallback(
    (signal: AbortSignal) => {
      if (!matchId) {
        return Promise.reject({
          body: { error: "Match not found." },
        });
      }

      return getMatch(matchId, signal);
    },
    [matchId],
  );

  const { state, retry } = useMatchRequest(load);

  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />

      <main className="mx-auto max-w-5xl px-6 py-10">
        <Link
          to="/matched-items"
          className="text-sm font-medium text-indigo-600"
        >
          ← Back to Matched Items
        </Link>

        <h1 className="mb-6 mt-4 text-2xl font-bold text-gray-900">
          Review match
        </h1>

        {state.status === "loading" ? (
          <p role="status">Loading match…</p>
        ) : state.status === "error" ? (
          <div role="alert" className="rounded-xl bg-red-50 p-5">
            <p className="text-red-700">{state.error}</p>
            <button
              type="button"
              onClick={retry}
              className="mt-3 rounded-lg border bg-white px-4 py-2"
            >
              Retry
            </button>
          </div>
        ) : (
          <>
            <div className="mb-6 rounded-2xl border bg-white p-5">
              <MatchBanner match={state.data} />
              <p className="mt-3 text-sm text-gray-600">
                Similarity does not prove ownership.
              </p>
            </div>

            <MatchReportComparison
              key={state.data.id}
              match={state.data}
              showDescriptions
            />

            <p className="mt-4 text-xs text-gray-500">
              Report text was saved when the claim was submitted.
              Photos are loaded from the currently available reports.
            </p>

            <section
              aria-label="Match decision"
              aria-live="polite"
              className="mt-6 rounded-2xl border bg-white p-5"
            >
              {state.data.status === "REJECTED" ? (
                <p className="rounded-xl bg-red-50 p-4 font-medium text-red-700">
                  Match closed - Rejected
                </p>
              ) : state.data.status === "CONFIRMED" ? (
                <p className="rounded-xl bg-emerald-50 p-4 font-medium text-emerald-700">
                  Match confirmed by both parties
                </p>
              ) : state.data.isYourTurn ? (
                <div className="rounded-xl bg-indigo-50 p-4">
                  <p className="font-medium text-indigo-700">
                    The other person confirmed this claim.
                    It is your turn to decide.
                  </p>
                  <p className="mt-2 text-sm text-gray-600">
                    Confirmation and rejection controls are
                    not available yet.
                  </p>
                </div>
              ) : (
                <p className="rounded-xl bg-gray-50 p-4 text-gray-700">
                  You confirmed this claim. Waiting for the
                  other person to review your report and decide.
                </p>
              )}
            </section>
          </>
        )}
      </main>
    </div>
  );
}