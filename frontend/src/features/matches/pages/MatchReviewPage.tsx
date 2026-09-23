import { useCallback } from "react";
import { Link, useParams } from "react-router-dom";
import { AppHeader } from "../../items/layout/AppHeader";
import { getMatch } from "../api/matchQueries";
import MatchBanner from "../components/MatchBanner";
import { MatchReportComparison } from "../components/MatchReportComparison";
import { LostReporterDecisionPanel } from "../components/LostReporterDecisionPanel";
import { ArrangeReturnPanel } from "../components/ArrangeReturnPanel";
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
          className="text-sm font-semibold text-violet-700 hover:text-violet-900"
        >
          Back to Matched Items
        </Link>

        <div className="mb-6 mt-4 flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-2xl font-bold text-slate-900">
            Review match
          </h1>

          <button
            type="button"
            disabled={state.status === "loading"}
            onClick={retry}
            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:opacity-50"
          >
            Refresh
          </button>
        </div>

        {state.status === "loading" ? (
          <p role="status" className="text-sm text-slate-600">
            Loading match...
          </p>
        ) : state.status === "error" ? (
          <div
            role="alert"
            className="rounded-xl border border-rose-200 bg-rose-50 p-5"
          >
            <p className="text-sm text-rose-800">
              {state.error}
            </p>

            <button
              type="button"
              onClick={retry}
              className="mt-3 rounded-lg border border-rose-200 bg-white px-4 py-2 text-sm font-semibold text-rose-800"
            >
              Try again
            </button>
          </div>
        ) : (
          <>
            <div className="mb-5 rounded-2xl border border-slate-200 bg-white p-5">
              <MatchBanner match={state.data} />

              <p className="mt-3 text-xs text-slate-500">
                Similarity does not prove ownership.
              </p>
            </div>

            <div className="rounded-2xl border border-slate-200 bg-white p-4 sm:p-5">
              <MatchReportComparison
                key={state.data.id}
                match={state.data}
                showDescriptions
              />
            </div>

            <p className="mt-3 text-xs leading-5 text-slate-500">
              Report text was saved when the claim was submitted.
              Photos are loaded from the currently available reports.
            </p>

            <div className="mt-6 space-y-4">
              {state.data.status === "REJECTED" ? (
                <div className="rounded-2xl border border-rose-200 bg-rose-50 p-5">
                  <p className="font-semibold text-rose-900">
                    Match closed: Rejected
                  </p>
                </div>
              ) : state.data.status === "CONFIRMED" ? (
                <>
                  <div className="rounded-2xl border border-emerald-200 bg-emerald-50 p-5">
                    <p className="font-semibold text-emerald-900">
                      Match confirmed by both parties
                    </p>
                  </div>

                  {state.data.yourRole === "LOST" && (
                    <ArrangeReturnPanel
                      key={state.data.id}
                      matchId={state.data.id}
                    />
                  )}
                </>
              ) : state.data.yourRole === "LOST" &&
                state.data.status === "FINDER_CONFIRMED" ? (
                <LostReporterDecisionPanel
                  key={state.data.id}
                  matchId={state.data.id}
                  onDecisionSaved={retry}
                  onRefresh={retry}
                />
              ) : state.data.isYourTurn ? (
                <div className="rounded-2xl border border-amber-200 bg-amber-50 p-5">
                  <p className="font-semibold text-amber-900">
                    Awaiting your decision
                  </p>

                  <p className="mt-2 text-sm text-amber-800">
                    Finder confirmation and rejection controls
                    are not available yet.
                  </p>
                </div>
              ) : (
                <div className="rounded-2xl border border-violet-200 bg-violet-50 p-5">
                  <p className="font-semibold text-violet-900">
                    Waiting for the other person
                  </p>

                  <p className="mt-2 text-sm text-violet-800">
                    You confirmed your claim. The other person
                    needs to review it and decide.
                  </p>
                </div>
              )}
            </div>
          </>
        )}
      </main>
    </div>
  );
}