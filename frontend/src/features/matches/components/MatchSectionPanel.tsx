import { useCallback, useState } from "react";
import {
  getMatchPage,
  type MatchSection,
} from "../api/matchQueries";
import { useMatchRequest } from "../hooks/useMatchRequest";
import MatchListCard from "./MatchListCard";

type Props = {
  section: MatchSection;
  title: string;
  initiallyOpen: boolean;
};

const SECTION_STYLES: Record<
  MatchSection,
  {
    dot: string;
    count: string;
  }
> = {
  "waiting-on-you": {
    dot: "bg-amber-500",
    count: "bg-amber-100 text-amber-800",
  },
  "waiting-on-other": {
    dot: "bg-violet-500",
    count: "bg-violet-100 text-violet-800",
  },
  confirmed: {
    dot: "bg-emerald-500",
    count: "bg-emerald-100 text-emerald-800",
  },
  rejected: {
    dot: "bg-rose-500",
    count: "bg-rose-100 text-rose-800",
  },
};

export default function MatchSectionPanel({
  section,
  title,
  initiallyOpen,
}: Props) {
  const [open, setOpen] = useState(initiallyOpen);
  const [page, setPage] = useState(1);

  const load = useCallback(
    (signal: AbortSignal) => getMatchPage(section, page, 10, signal),
    [section, page],
  );

  const { state, retry } = useMatchRequest(load);

  const panelId = `matches-${section}`;
  const headingId = `${panelId}-heading`;
  const styles = SECTION_STYLES[section];

  const totalPages =
    state.status === "success"
      ? Math.max(
          1,
          Math.ceil(
            state.data.totalCount / Math.max(1, state.data.size),
          ),
        )
      : 1;

  return (
    <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white">
      <h2 id={headingId}>
        <button
          type="button"
          aria-expanded={open}
          aria-controls={panelId}
          onClick={() => setOpen((current) => !current)}
          className="flex w-full items-center justify-between gap-4 px-4 py-5 text-left transition hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-violet-500 sm:px-5"
        >
          <span className="flex min-w-0 items-center gap-3">
            <span
              aria-hidden="true"
              className={`h-2.5 w-2.5 shrink-0 rounded-full ${styles.dot}`}
            />

            <span className="text-base font-semibold text-slate-900">
              {title}
            </span>

            {state.status === "success" && (
              <span
                className={`inline-flex min-w-6 items-center justify-center rounded-full px-2 py-0.5 text-xs font-semibold tabular-nums ${styles.count}`}
              >
                {state.data.totalCount}
              </span>
            )}
          </span>

          <svg
            aria-hidden="true"
            viewBox="0 0 20 20"
            fill="none"
            className={`h-5 w-5 shrink-0 text-slate-500 transition-transform ${
              open ? "rotate-180" : ""
            }`}
          >
            <path
              d="m5 7.5 5 5 5-5"
              stroke="currentColor"
              strokeWidth="1.75"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        </button>
      </h2>

      <div
        id={panelId}
        role="region"
        aria-labelledby={headingId}
        hidden={!open}
        className="border-t border-slate-100 px-4 pb-5 pt-4 sm:px-5"
      >
        {state.status === "loading" && (
          <div
            role="status"
            className="flex items-center gap-3 rounded-xl bg-slate-50 px-4 py-6 text-sm text-slate-600"
          >
            <span
              aria-hidden="true"
              className="h-4 w-4 animate-spin rounded-full border-2 border-slate-200 border-t-violet-600"
            />
            Loading matches...
          </div>
        )}

        {state.status === "error" && (
          <div
            role="alert"
            className="rounded-xl border border-rose-200 bg-rose-50 p-4"
          >
            <p className="text-sm text-rose-800">
              {state.error || "We couldn't load your matches."}
            </p>

            <button
              type="button"
              onClick={retry}
              className="mt-3 rounded-lg border border-rose-200 bg-white px-3 py-2 text-sm font-semibold text-rose-800 hover:bg-rose-100"
            >
              Try again
            </button>
          </div>
        )}

        {state.status === "success" && (
          <>
            {state.data.items.length === 0 ? (
              <div className="rounded-xl bg-slate-50 px-4 py-6 text-center">
                <p className="text-sm text-slate-500">
                  {state.data.totalCount === 0
                    ? "No matches in this section."
                    : "No matches on this page."}
                </p>

                {page > 1 && (
                  <button
                    type="button"
                    onClick={() => setPage(1)}
                    className="mt-3 text-sm font-semibold text-violet-700 hover:text-violet-900"
                  >
                    Return to first page
                  </button>
                )}
              </div>
            ) : (
              <ul className="space-y-3">
                {state.data.items.map((match) => (
                  <li key={match.id}>
                    <MatchListCard match={match} />
                  </li>
                ))}
              </ul>
            )}

            {state.data.totalCount > 0 && totalPages > 1 && (
              <nav
                aria-label={`${title} pagination`}
                className="mt-5 flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-4"
              >
                <p className="text-xs text-slate-500">
                  Page {page} of {totalPages}
                </p>

                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    disabled={page <= 1}
                    onClick={() =>
                      setPage((current) => Math.max(1, current - 1))
                    }
                    className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    Previous
                  </button>

                  <button
                    type="button"
                    disabled={page >= totalPages}
                    onClick={() => setPage((current) => current + 1)}
                    className="rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-40"
                  >
                    Next
                  </button>
                </div>
              </nav>
            )}
          </>
        )}
      </div>
    </section>
  );
}