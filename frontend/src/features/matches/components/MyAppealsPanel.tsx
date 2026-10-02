import { useCallback, useEffect, useState } from "react";
import { matchingError } from "../api/matches";
import { getMyAppeals, type AppealStatus, type MyAppeal } from "../api/appeals";
import { MatchItemCard } from "./MatchItemCard";

const PAGE_SIZE = 20;

const STATUS_STYLES: Record<AppealStatus, { label: string; className: string }> = {
  PENDING: { label: "Pending", className: "bg-amber-100 text-amber-800" },
  VERIFIED: { label: "Verified", className: "bg-emerald-100 text-emerald-800" },
  REJECTED: { label: "Rejected", className: "bg-rose-100 text-rose-800" },
};

function formatDate(value: string): string {
  return new Date(value).toLocaleString();
}

export function MyAppealsPanel() {
  const [appeals, setAppeals] = useState<MyAppeal[]>([]);
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const load = useCallback((pageToLoad: number, signal?: AbortSignal) => {
    setLoading(true);
    setError("");

    getMyAppeals(pageToLoad, signal)
      .then((items) => {
        if (signal?.aborted) return;
        setAppeals((current) => (pageToLoad === 1 ? items : [...current, ...items]));
        setHasMore(items.length === PAGE_SIZE);
        setPage(pageToLoad);
      })
      .catch((reason) => {
        if (!signal?.aborted) {
          setError(matchingError(reason));
        }
      })
      .finally(() => {
        if (!signal?.aborted) {
          setLoading(false);
        }
      });
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    load(1, controller.signal);
    return () => controller.abort();
  }, [load]);

  if (error) {
    return (
      <div className="rounded-xl bg-rose-50 p-5">
        <p className="text-rose-700">{error}</p>
        <button
          type="button"
          onClick={() => load(1)}
          className="mt-3 rounded-lg border bg-white px-4 py-2"
        >
          Try again
        </button>
      </div>
    );
  }

  if (loading && appeals.length === 0) {
    return <p role="status">Loading appeals...</p>;
  }

  if (appeals.length === 0) {
    return (
      <p className="rounded-xl border bg-white p-6 text-slate-600">
        You haven't sent any appeals yet.
      </p>
    );
  }

  return (
    <div className="space-y-5">
      {appeals.map((appeal) => {
        const status = STATUS_STYLES[appeal.status];

        return (
          <article
            key={appeal.id}
            className="rounded-2xl border bg-white p-5 shadow-sm"
          >
            <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
              <div className="text-sm text-slate-600">
                <p>Sent {formatDate(appeal.createdAt)}</p>
                {appeal.decidedAt && (
                  <p>Decided {formatDate(appeal.decidedAt)}</p>
                )}
              </div>

              <div className="flex items-center gap-3">
                <span className="text-sm font-semibold text-slate-700">
                  Score {appeal.score.toFixed(2)}%
                </span>
                <span
                  className={`rounded-full px-3 py-1 text-xs font-bold ${status.className}`}
                >
                  {status.label}
                </span>
              </div>
            </div>

            <div className="grid gap-4 sm:grid-cols-2">
              <MatchItemCard item={appeal.lost} />
              <MatchItemCard item={appeal.found} />
            </div>

            {appeal.note && (
              <div className="mt-4 rounded-xl bg-slate-50 p-4">
                <p className="text-sm font-medium text-slate-500">
                  Your note
                </p>
                <p className="mt-1 whitespace-pre-wrap text-sm text-slate-700">
                  {appeal.note}
                </p>
              </div>
            )}
          </article>
        );
      })}

      {hasMore && (
        <button
          type="button"
          disabled={loading}
          onClick={() => load(page + 1)}
          className="rounded-xl border bg-white px-4 py-2 disabled:opacity-40"
        >
          {loading ? "Loading..." : "Load more"}
        </button>
      )}
    </div>
  );
}
