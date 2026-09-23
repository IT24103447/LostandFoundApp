import { useId, useState } from "react";
import type { MatchListEntry } from "../api/matchQueries";
import { MatchItemCard } from "./MatchItemCard";

type Props = {
  match: MatchListEntry;
  showDescriptions?: boolean;
};

export function MatchReportComparison({
  match,
  showDescriptions = false,
}: Props) {
  const [showOwnReport, setShowOwnReport] = useState(false);
  const ownReportPanelId = useId();

  const ownItem =
    match.yourRole === "LOST" ? match.lost : match.found;

  const theirItem =
    match.yourRole === "LOST" ? match.found : match.lost;

  const isClosed =
    match.status === "CONFIRMED" ||
    match.status === "REJECTED";

  const ownTypeLabel =
    ownItem.type === "LOST" ? "lost" : "found";

  const theirTypeLabel =
    theirItem.type === "LOST" ? "lost" : "found";

  return (
    <div className="text-left">
      <div className="mb-4 space-y-4 rounded-xl bg-gray-50 p-4">
        <div>
          <p className="font-semibold leading-relaxed text-gray-900">
            {match.isClaimant
              ? "You started this claim"
              : "The other person started this claim"}
          </p>

          <p className="mt-1 text-sm leading-relaxed text-gray-600">
            {match.isClaimant
              ? "You selected their report as a possible match for yours."
              : "They selected your report as a possible match for theirs."}
          </p>
        </div>

        <dl className="space-y-3 text-sm">
          <div className="grid gap-1 sm:grid-cols-[140px_minmax(0,1fr)] sm:gap-4">
            <dt className="font-medium text-gray-500">
              Your {ownTypeLabel} report
            </dt>
            <dd className="min-w-0 break-words font-semibold text-gray-900">
              {ownItem.title}
            </dd>
          </div>

          <div className="grid gap-1 sm:grid-cols-[140px_minmax(0,1fr)] sm:gap-4">
            <dt className="font-medium text-gray-500">
              Their {theirTypeLabel} report
            </dt>
            <dd className="min-w-0 break-words font-semibold text-gray-900">
              {theirItem.title}
            </dd>
          </div>
        </dl>

        {!isClosed && (
          <p className="border-t border-gray-200 pt-3 text-sm font-medium leading-relaxed text-indigo-700">
            {match.isYourTurn
              ? "They have confirmed their claim. Your decision is next."
              : "You have confirmed your claim. Their decision is next."}
          </p>
        )}
      </div>

      {isClosed ? (
        <div className="grid items-stretch gap-4 sm:grid-cols-2">
          <MatchItemCard
            item={ownItem}
            ownerLabel="Your report"
            showDescription={showDescriptions}
          />
          <MatchItemCard
            item={theirItem}
            ownerLabel="Their report"
            showDescription={showDescriptions}
          />
        </div>
      ) : (
        <div className="space-y-4">
          <MatchItemCard
            item={theirItem}
            ownerLabel="Their report"
            showDescription={showDescriptions}
          />

          <button
            type="button"
            aria-expanded={showOwnReport}
            aria-controls={ownReportPanelId}
            onClick={() => setShowOwnReport((value) => !value)}
            className="block w-full rounded-xl border border-indigo-200 bg-indigo-50 px-4 py-3 text-left hover:bg-indigo-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-indigo-600"
          >
            <span className="block font-semibold text-indigo-700">
              {showOwnReport ? "Hide" : "Show"} your {ownTypeLabel} report
            </span>
            <span className="mt-1 block break-words text-sm leading-relaxed text-gray-700">
              {ownItem.title}
            </span>
          </button>

          <div id={ownReportPanelId} hidden={!showOwnReport}>
            {showOwnReport && (
              <MatchItemCard
                item={ownItem}
                ownerLabel="Your report"
                showDescription={showDescriptions}
              />
            )}
          </div>
        </div>
      )}
    </div>
  );
}