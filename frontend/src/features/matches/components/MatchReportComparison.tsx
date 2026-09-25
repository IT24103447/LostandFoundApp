import { useId, useState } from "react";
import { isClosedMatch, type MatchListEntry } from "../api/matchQueries";
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
  const ownPanelId = useId();

  const ownItem = match.yourRole === "LOST" ? match.lost : match.found;
  const theirItem = match.yourRole === "LOST" ? match.found : match.lost;
  const closed = isClosedMatch(match);

  return (
    <div className="space-y-4 text-left">
      <div className="rounded-xl bg-slate-50 p-4">
        <p className="text-sm font-semibold text-slate-900">
          {match.isClaimant
            ? "You submitted this claim"
            : "They submitted this claim"}
        </p>

        {!closed && (
          <p className="mt-2 text-sm text-slate-600">
            {match.isYourTurn
              ? "Review both reports before deciding."
              : "Waiting for the other person's decision."}
          </p>
        )}
      </div>

      {closed ? (
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
            aria-controls={ownPanelId}
            onClick={() => setShowOwnReport((value) => !value)}
            className="w-full rounded-xl border border-violet-200 bg-violet-50 p-4 text-left hover:bg-violet-100"
          >
            <span className="block text-sm font-semibold text-violet-800">
              {showOwnReport ? "Hide your report" : "Show your report"}
            </span>
            <span className="mt-1 block text-sm text-slate-700">
              {ownItem.title}
            </span>
          </button>

          <div id={ownPanelId} hidden={!showOwnReport}>
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