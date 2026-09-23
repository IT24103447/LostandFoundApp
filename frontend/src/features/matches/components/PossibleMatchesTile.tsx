import { useCallback } from "react";
import { Link } from "react-router-dom";
import { getMatchPage } from "../api/matchQueries";
import { useMatchRequest } from "../hooks/useMatchRequest";

export function PossibleMatchesTile() {
  const load = useCallback(
    (signal: AbortSignal) =>
      getMatchPage("active", 1, 1, signal),
    [],
  );

  const { state, retry } = useMatchRequest(load);

  return (
    <section className="mb-6 rounded-xl border border-gray-200 bg-white p-5 shadow-sm">
      <Link
        to="/matched-items"
        className="block rounded-lg focus-visible:outline focus-visible:outline-2 focus-visible:outline-indigo-600"
      >
        <h2 className="font-semibold text-gray-900">
          Possible Matches
        </h2>

        {state.status === "loading" ? (
          <p role="status" className="mt-2 text-sm text-gray-600">
            Loading count…
          </p>
        ) : state.status === "success" ? (
          <p className="mt-2 text-3xl font-bold text-indigo-600">
            {state.data.totalCount}
          </p>
        ) : (
          <p className="mt-2 text-sm text-gray-600">
            Count unavailable
          </p>
        )}

        <p className="mt-2 text-sm text-indigo-600">
          View Matched Items →
        </p>
      </Link>

      {state.status === "error" && (
        <div role="alert" className="mt-3">
          <p className="text-sm text-red-700">{state.error}</p>
          <button
            type="button"
            onClick={retry}
            className="mt-2 rounded-lg border px-3 py-2 text-sm"
          >
            Retry
          </button>
        </div>
      )}
    </section>
  );
}