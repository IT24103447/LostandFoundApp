import { useEffect, useState } from "react";
import { AppHeader } from "../../items/layout/AppHeader";
import {
  getMyMatches,
  matchingError,
  type SavedMatch,
} from "../api/matches";
import { MatchItemCard } from "../components/MatchItemCard";

function statusLabel(status: string): string {
  switch (status) {
    case "LOST_REPORTER_CONFIRMED":
      return "Lost reporter confirmed — awaiting finder";

    case "FINDER_CONFIRMED":
      return "Finder confirmed — awaiting lost reporter";

    case "CONFIRMED":
      return "Confirmed by both parties";

    case "REJECTED":
      return "Rejected";

    default:
      return status.replaceAll("_", " ");
  }
}

export function MatchedItemsPage() {
  const [matches, setMatches] =
    useState<SavedMatch[]>([]);

  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [reload, setReload] = useState(0);

  useEffect(() => {
    const controller = new AbortController();

    setLoading(true);
    setError("");
    setMatches([]);

    getMyMatches(page, controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) {
          setMatches(result);
        }
      })
      .catch((reason) => {
        if (!controller.signal.aborted) {
          setError(matchingError(reason));
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      });

    return () => controller.abort();
  }, [page, reload]);

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
              Submitted claims involving your lost and
              found reports.
            </p>
          </div>

          <button
            type="button"
            disabled={loading}
            onClick={() =>
              setReload((value) => value + 1)
            }
            className="rounded-xl border bg-white px-4 py-2 disabled:opacity-50"
          >
            Refresh
          </button>
        </div>

        {loading ? (
          <p role="status">Loading matches…</p>
        ) : error ? (
          <p
            role="alert"
            className="rounded-xl bg-red-50 p-4 text-red-700"
          >
            {error}
          </p>
        ) : matches.length === 0 ? (
          <p className="rounded-xl border bg-white p-6 text-gray-600">
            {page === 1
              ? "You don’t have any submitted matches yet."
              : "There are no more matches."}
          </p>
        ) : (
          <div className="space-y-6">
            {matches.map((match) => (
              <article
                key={match.id}
                className="rounded-2xl border border-gray-200 bg-white p-5"
              >
                <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
                  <span className="rounded-full bg-indigo-50 px-3 py-2 text-sm font-semibold text-indigo-700">
                    {statusLabel(match.status)}
                  </span>

                  <span className="font-semibold">
                    Similarity:{" "}
                    {match.score.toFixed(2)}%
                  </span>
                </div>

                <div className="grid gap-4 sm:grid-cols-2">
                  <MatchItemCard item={match.lost} />
                  <MatchItemCard item={match.found} />
                </div>

                <p className="mt-4 text-xs text-gray-500">
                  Submitted{" "}
                  {new Date(
                    match.createdAt,
                  ).toLocaleString()}
                  .
                </p>
              </article>
            ))}
          </div>
        )}

        <div className="mt-6 flex items-center justify-end gap-3">
          <button
            type="button"
            disabled={page === 1 || loading}
            onClick={() =>
              setPage((value) => value - 1)
            }
            className="rounded-xl border px-4 py-2 disabled:opacity-40"
          >
            Previous
          </button>

          <span className="text-sm text-gray-600">
            Page {page}
          </span>

          <button
            type="button"
            disabled={
              loading ||
              Boolean(error) ||
              matches.length < 20
            }
            onClick={() =>
              setPage((value) => value + 1)
            }
            className="rounded-xl border px-4 py-2 disabled:opacity-40"
          >
            Next
          </button>
        </div>
      </main>
    </div>
  );
}