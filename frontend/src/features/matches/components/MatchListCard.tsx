import { useId, useState } from "react";
import { Link } from "react-router-dom";
import type { MatchListEntry } from "../api/matchQueries";
import MatchBanner from "./MatchBanner";
import { MatchReportComparison } from "./MatchReportComparison";

type Props = {
  match: MatchListEntry;
};

type ReportSummaryProps = {
  owner: "Your report" | "Their report";
  type: "LOST" | "FOUND";
  title: string;
};

function ReportSummary({ owner, type, title }: ReportSummaryProps) {
  const isLost = type === "LOST";

  return (
    <div
      className={`min-w-0 rounded-xl border p-3 ${
        isLost
          ? "border-violet-200 bg-violet-50/50"
          : "border-orange-200 bg-orange-50/50"
      }`}
    >
      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <span className="text-xs font-medium text-slate-600">
          {owner}
        </span>

        <span
          className={`rounded-md px-2 py-1 text-[11px] font-bold uppercase tracking-wide ${
            isLost
              ? "bg-violet-100 text-violet-800"
              : "bg-orange-100 text-orange-800"
          }`}
        >
          {type}
        </span>
      </div>

      <p
        className="break-words text-sm font-semibold leading-6 text-slate-900"
        title={title}
      >
        {title || "Untitled report"}
      </p>
    </div>
  );
}

export default function MatchListCard({ match }: Props) {
  const [expanded, setExpanded] = useState(false);
  const detailsId = useId();

  const yourItem = match.yourRole === "LOST" ? match.lost : match.found;
  const theirItem = match.yourRole === "LOST" ? match.found : match.lost;

  const yourType = match.yourRole;
  const theirType = match.yourRole === "LOST" ? "FOUND" : "LOST";

  const isClosed =
    match.status === "CONFIRMED" || match.status === "REJECTED";

  const reviewLabel =
    !isClosed && match.isYourTurn ? "Review match" : "Open match";

  return (
    <article className="overflow-hidden rounded-2xl border border-slate-200 bg-white">
      <div className="space-y-4 p-4 sm:p-5">
        <MatchBanner match={match} />

        <div className="grid gap-3 sm:grid-cols-2">
          <ReportSummary
            owner="Your report"
            type={yourType}
            title={yourItem.title}
          />

          <ReportSummary
            owner="Their report"
            type={theirType}
            title={theirItem.title}
          />
        </div>

        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-slate-100 pt-3">
          <p className="text-xs text-slate-500">
            {match.isClaimant
              ? "You submitted this claim"
              : "They submitted this claim"}
          </p>

          <button
            type="button"
            aria-expanded={expanded}
            aria-controls={detailsId}
            onClick={() => setExpanded((current) => !current)}
            className="inline-flex items-center gap-2 rounded-lg px-3 py-2 text-sm font-semibold text-violet-700 transition hover:bg-violet-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 focus-visible:ring-offset-2"
          >
            {expanded ? "Hide details" : "View details"}

            <svg
              aria-hidden="true"
              viewBox="0 0 20 20"
              fill="none"
              className={`h-4 w-4 transition-transform ${
                expanded ? "rotate-180" : ""
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
        </div>
      </div>

      <div id={detailsId} hidden={!expanded}>
        {expanded && (
          <div className="border-t border-slate-200 bg-slate-50 p-4 sm:p-5">
            <div className="rounded-xl border border-slate-200 bg-white p-4 sm:p-5">
              <h3 className="mb-4 text-sm font-semibold text-slate-900">
                Report comparison
              </h3>

              <MatchReportComparison match={match} />
            </div>

            <div className="mt-4 flex flex-wrap items-center justify-between gap-3">
              <p className="text-xs text-slate-500">
                {match.status === "CONFIRMED"
                  ? "Both parties confirmed this match."
                  : match.status === "REJECTED"
                    ? "This match is closed."
                    : match.isYourTurn
                      ? "Your decision is next."
                      : "Waiting for the other person's decision."}
              </p>

              <Link
                to={`/matched-items/${encodeURIComponent(match.id)}`}
                className="inline-flex items-center justify-center rounded-lg bg-violet-600 px-4 py-2.5 text-sm font-semibold text-white transition hover:bg-violet-700 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-violet-500 focus-visible:ring-offset-2"
              >
                {reviewLabel}
              </Link>
            </div>
          </div>
        )}
      </div>
    </article>
  );
}