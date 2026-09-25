import {
  deactivationLabel,
  deactivationMessage,
  matchStatusLabel,
  type MatchListEntry,
  type MatchStatus,
} from "../api/matchQueries";

const STYLES: Record<MatchStatus, string> = {
  LOST_REPORTER_CONFIRMED: "border-amber-200 bg-amber-50 text-amber-900",
  FINDER_CONFIRMED: "border-violet-200 bg-violet-50 text-violet-800",
  CONFIRMED: "border-emerald-200 bg-emerald-50 text-emerald-800",
  REJECTED: "border-rose-200 bg-rose-50 text-rose-800",
  DEACTIVATED: "border-slate-300 bg-slate-100 text-slate-800",
};

export default function MatchBanner({
  match,
}: {
  match: MatchListEntry;
}) {
  const deactivated = match.status === "DEACTIVATED";

  return (
    <div>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <span
          className={`rounded-lg border px-3 py-2 text-sm font-semibold ${STYLES[match.status]}`}
        >
          {deactivated
            ? deactivationLabel(match)
            : matchStatusLabel(match.status)}
        </span>

        <div className="rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
          <span className="mr-2 text-xs text-slate-500">Similarity</span>
          <span className="text-sm font-bold tabular-nums text-slate-900">
            {match.score.toFixed(2)}%
          </span>
        </div>
      </div>

      {deactivated && (
        <div className="mt-3 rounded-xl border border-slate-200 bg-slate-50 p-3">
          <p className="text-sm leading-6 text-slate-700">
            {deactivationMessage(match)}
          </p>

          {match.deactivatedAt && (
            <p className="mt-1 text-xs text-slate-500">
              Deactivated {new Date(match.deactivatedAt).toLocaleString()}
            </p>
          )}
        </div>
      )}
    </div>
  );
}