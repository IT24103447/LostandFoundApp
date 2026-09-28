import { useCallback, useState } from "react";
import { getMatchPage, type MatchSection } from "../api/matchQueries";
import { useMatchRequest } from "../hooks/useMatchRequest";
import MatchListCard from "./MatchListCard";

type Props = {
  section: MatchSection;
  title: string;
  initiallyOpen: boolean;
};

const STYLES: Record<MatchSection, string> = {
  "waiting-on-you": "bg-amber-100 text-amber-800",
  "waiting-on-other": "bg-violet-100 text-violet-800",
  confirmed: "bg-emerald-100 text-emerald-800",
  rejected: "bg-rose-100 text-rose-800",
  deactivated: "bg-slate-200 text-slate-700",
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

  const totalPages =
    state.status === "success"
      ? Math.max(1, Math.ceil(state.data.totalCount / state.data.size))
      : 1;

  return (
    <section className="overflow-hidden rounded-2xl border border-slate-200 bg-white">
      <h2>
        <button
          type="button"
          aria-expanded={open}
          aria-controls={panelId}
          onClick={() => setOpen((value) => !value)}
          className="flex w-full items-center justify-between gap-3 p-5 text-left hover:bg-slate-50"
        >
          <span className="flex items-center gap-3 font-semibold text-slate-900">
            {title}
            {state.status === "success" && (
              <span
                className={`rounded-full px-2.5 py-1 text-xs ${STYLES[section]}`}
              >
                {state.data.totalCount}
              </span>
            )}
          </span>

          <span aria-hidden="true">{open ? "−" : "+"}</span>
        </button>
      </h2>

      <div
        id={panelId}
        hidden={!open}
        className="border-t border-slate-100 p-5"
      >
        {state.status === "loading" && (
          <p role="status" className="text-sm text-slate-600">
            Loading matches...
          </p>
        )}

        {state.status === "error" && (
          <div
            role="alert"
            className="rounded-xl bg-rose-50 p-4 text-rose-800"
          >
            <p>{state.error}</p>
            <button
              type="button"
              onClick={retry}
              className="mt-3 font-semibold"
            >
              Try again
            </button>
          </div>
        )}

        {state.status === "success" && (
          <>
            {state.data.items.length === 0 ? (
              <div className="text-sm text-slate-500">
                <p>No matches in this section.</p>
                {page > 1 && (
                  <button
                    type="button"
                    onClick={() => setPage(1)}
                    className="mt-3 font-semibold text-violet-700"
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

            {totalPages > 1 && (
              <nav
                aria-label={`${title} pagination`}
                className="mt-5 flex items-center justify-between gap-3 border-t pt-4 text-sm"
              >
                <span>Page {page} of {totalPages}</span>
                <div className="flex gap-3">
                  <button
                    type="button"
                    disabled={page <= 1}
                    onClick={() => setPage((value) => value - 1)}
                    className="rounded-lg border px-3 py-2 disabled:opacity-40"
                  >
                    Previous
                  </button>
                  <button
                    type="button"
                    disabled={page >= totalPages}
                    onClick={() => setPage((value) => value + 1)}
                    className="rounded-lg border px-3 py-2 disabled:opacity-40"
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