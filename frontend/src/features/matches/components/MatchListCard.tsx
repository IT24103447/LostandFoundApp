import { useId, useState } from "react";
import { Link } from "react-router-dom";
import type { MatchListEntry } from "../api/matchQueries";
import MatchBanner from "./MatchBanner";
import { MatchReportComparison } from "./MatchReportComparison";

function ReportSummary({
  owner,
  type,
  title,
}: {
  owner: string;
  type: "LOST" | "FOUND";
  title: string;
}) {
  const lost = type === "LOST";

  return (
    <div
      className={`min-w-0 rounded-xl border p-3 ${
        lost
          ? "border-violet-200 bg-violet-50/50"
          : "border-orange-200 bg-orange-50/50"
      }`}
    >
      <div className="mb-2 flex items-center justify-between gap-2">
        <span className="text-xs font-medium text-slate-600">{owner}</span>
        <span
          className={`rounded-md px-2 py-1 text-xs font-bold ${
            lost
              ? "bg-violet-100 text-violet-800"
              : "bg-orange-100 text-orange-800"
          }`}
        >
          {type}
        </span>
      </div>

      <p className="break-words text-sm font-semibold text-slate-900">
        {title || "Untitled report"}
      </p>
    </div>
  );
}

export default function MatchListCard({
  match,
}: {
  match: MatchListEntry;
}) {
  const [expanded, setExpanded] = useState(false);
  const detailsId = useId();

  const yourItem = match.yourRole === "LOST" ? match.lost : match.found;
  const theirItem = match.yourRole === "LOST" ? match.found : match.lost;
  const deactivated = match.status === "DEACTIVATED";

  const message = deactivated
    ? "Read-only history. No further decisions are available."
    : match.status === "CONFIRMED"
      ? "Both parties confirmed this match."
      : match.status === "REJECTED"
        ? "This match is closed."
        : match.isYourTurn
          ? "Your decision is next."
          : "Waiting for the other person's decision.";

  return (
    <article className="overflow-hidden rounded-2xl border border-slate-200 bg-white">
      <div className="space-y-4 p-5">
        <MatchBanner match={match} />

        <div className="grid gap-3 sm:grid-cols-2">
          <ReportSummary
            owner="Your report"
            type={yourItem.type}
            title={yourItem.title}
          />
          <ReportSummary
            owner="Their report"
            type={theirItem.type}
            title={theirItem.title}
          />
        </div>

        <div className="flex flex-wrap items-center justify-between gap-3 border-t pt-3">
          <p className="text-xs text-slate-500">
            {match.isClaimant
              ? "You submitted this claim"
              : "They submitted this claim"}
          </p>

          <button
            type="button"
            aria-expanded={expanded}
            aria-controls={detailsId}
            onClick={() => setExpanded((value) => !value)}
            className="rounded-lg px-3 py-2 text-sm font-semibold text-violet-700 hover:bg-violet-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-violet-600"
          >
            {expanded ? "Hide details" : "View details"}
          </button>
        </div>
      </div>

      <div id={detailsId} hidden={!expanded}>
        {expanded && (
          <div className="border-t bg-slate-50 p-5">
            <div className="rounded-xl border bg-white p-4">
              <MatchReportComparison match={match} />
            </div>

            <div className="mt-4 flex flex-wrap items-center justify-between gap-3">
              <p className="text-xs text-slate-600">{message}</p>

              <Link
                to={`/matched-items/${encodeURIComponent(match.id)}`}
                className="rounded-lg bg-violet-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-violet-700"
              >
                {deactivated
                  ? "View saved match"
                  : match.isYourTurn
                    ? "Review match"
                    : "Open match"}
              </Link>
            </div>
          </div>
        )}
      </div>
    </article>
  );
}