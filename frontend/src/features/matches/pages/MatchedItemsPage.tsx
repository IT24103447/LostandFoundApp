import { useCallback, useState } from "react";
import { AppHeader } from "../../items/layout/AppHeader";
import { getMatchPage } from "../api/matchQueries";
import MatchSectionPanel from "../components/MatchSectionPanel";
import { useMatchRequest } from "../hooks/useMatchRequest";

export function MatchedItemsPage() {
  const [revision, setRevision] = useState(0);

  const load = useCallback(
    (signal: AbortSignal) => getMatchPage("all", 1, 1, signal),
    [],
  );

  const { state, retry } = useMatchRequest(load);

  const refresh = () => {
    setRevision((value) => value + 1);
    retry();
  };

  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />

      <main className="mx-auto max-w-5xl px-6 py-10">
        <div className="mb-6 flex items-center justify-between gap-4">
          <div>
            <h1 className="text-2xl font-bold text-gray-900">
              Matched Items
            </h1>
            <p className="mt-2 text-sm text-gray-600">
              Review claims linked to your lost and found reports.
            </p>
          </div>

          <button
            type="button"
            disabled={state.status === "loading"}
            onClick={refresh}
            className="rounded-xl border bg-white px-4 py-2 disabled:opacity-40"
          >
            Refresh
          </button>
        </div>

        {state.status === "loading" ? (
          <p role="status">Loading matches…</p>
        ) : state.status === "error" ? (
          <div role="alert" className="rounded-xl bg-red-50 p-5">
            <p className="text-red-700">{state.error}</p>
            <button
              type="button"
              onClick={refresh}
              className="mt-3 rounded-lg border bg-white px-4 py-2"
            >
              Retry
            </button>
          </div>
        ) : state.data.totalCount === 0 ? (
          <p className="rounded-xl border bg-white p-6 text-gray-600">
            No potential matches yet
          </p>
        ) : (
          <div key={revision} className="space-y-5">
            <MatchSectionPanel
              section="waiting-on-you"
              title="Waiting On You"
              initiallyOpen
            />
            <MatchSectionPanel
              section="waiting-on-other"
              title="Waiting On The Other Party"
              initiallyOpen
            />
            <MatchSectionPanel
              section="confirmed"
              title="Confirmed"
              initiallyOpen
            />
            <MatchSectionPanel
              section="rejected"
              title="Rejected"
              initiallyOpen={false}
            />
          </div>
        )}
      </main>
    </div>
  );
}