import type { MatchListEntry } from "../api/matchQueries";

type Props = {
  match: MatchListEntry;
};

function getStatusPresentation(match: MatchListEntry) {
  switch (match.status) {
    case "CONFIRMED":
      return {
        label: "Confirmed",
        container: "border-emerald-200 bg-emerald-50 text-emerald-800",
        dot: "bg-emerald-500",
      };

    case "REJECTED":
      return {
        label: "Rejected",
        container: "border-rose-200 bg-rose-50 text-rose-800",
        dot: "bg-rose-500",
      };

    case "LOST_REPORTER_CONFIRMED":
      return {
        label: "Awaiting finder",
        container: "border-amber-200 bg-amber-50 text-amber-900",
        dot: "bg-amber-500",
      };

    case "FINDER_CONFIRMED":
      return {
        label: "Awaiting lost reporter",
        container: "border-violet-200 bg-violet-50 text-violet-800",
        dot: "bg-violet-500",
      };

    default:
      return {
        label: "Awaiting decision",
        container: "border-slate-200 bg-slate-50 text-slate-700",
        dot: "bg-slate-500",
      };
  }
}

export default function MatchBanner({ match }: Props) {
  const status = getStatusPresentation(match);

  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <span
        className={`inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm font-semibold ${status.container}`}
      >
        <span
          aria-hidden="true"
          className={`h-2 w-2 shrink-0 rounded-full ${status.dot}`}
        />
        {status.label}
      </span>

      <div className="inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
        <span className="text-xs font-medium text-slate-500">
          Similarity
        </span>
        <span className="text-sm font-bold tabular-nums text-slate-900">
          {match.score.toFixed(2)}%
        </span>
      </div>
    </div>
  );
}