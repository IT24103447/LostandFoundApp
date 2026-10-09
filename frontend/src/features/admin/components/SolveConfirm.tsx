import { useState } from "react";
import type { KickStatus, SpamRecordDetail } from "../api/spamRecords";
import type { ItemState } from "./SpamRecordDialog";

const SAVED_KICK_LABELS: Record<KickStatus, string> = {
  NOT_REQUESTED: "The user won't be kicked.",
  PENDING: "The user will be kicked when Solve finishes the listings.",
  KICKED: "The user has already been kicked.",
  FAILED: "The kick failed. Kick the user from User Management.",
};

type Props = {
  detail: SpamRecordDetail;
  items: Record<string, ItemState>;
  labels: Record<string, string>;
  resume: boolean;
  userDeleted: boolean;
  onConfirm: (kick: boolean) => void;
  onCancel: () => void;
};

function willDelete(state: ItemState | undefined) {
  return state?.kind === "loaded" && !state.item.deletedAt && state.item.status !== "RESOLVED";
}

export function SolveConfirm({ detail, items, labels, resume, userDeleted, onConfirm, onCancel }: Props) {
  const [kick, setKick] = useState(false);

  const remaining = detail.listings.filter(
    (listing) => listing.solveResult !== "DELETED" && listing.solveResult !== "SKIPPED",
  );
  const toDelete = remaining.filter((listing) => willDelete(items[listing.listingId]));
  const toSkip = remaining.filter((listing) => !willDelete(items[listing.listingId]));

  return (
    <div id="spam-solve-confirm-dialog" className="space-y-3 rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-gray-700">
      <p className="font-semibold text-gray-900">
        {resume ? "Resume Solve for this record?" : "Solve this record?"}
      </p>

      <div id="spam-solve-will-delete">
        <p className="font-medium">Will delete ({toDelete.length})</p>
        <ul className="ml-5 list-disc text-gray-600">
          {toDelete.map((listing) => (
            <li key={listing.listingId}>{labels[listing.listingId]}</li>
          ))}
        </ul>
      </div>

      {toSkip.length > 0 && (
        <div id="spam-solve-will-skip">
          <p className="font-medium">Will skip ({toSkip.length})</p>
          <ul className="ml-5 list-disc text-gray-600">
            {toSkip.map((listing) => (
              <li key={listing.listingId}>{labels[listing.listingId]}</li>
            ))}
          </ul>
        </div>
      )}

      {resume ? (
        <p id="spam-solve-kick-saved" className="text-gray-600">
          {SAVED_KICK_LABELS[detail.kickStatus]}
        </p>
      ) : (
        <label className={`flex items-center gap-2 ${userDeleted ? "text-gray-400" : ""}`}>
          <input
            id="spam-solve-kick"
            type="checkbox"
            checked={kick}
            disabled={userDeleted}
            onChange={(event) => setKick(event.target.checked)}
          />
          Kick user after the listings are handled
          {userDeleted && <span className="text-xs">(user is deleted)</span>}
        </label>
      )}

      <div className="flex justify-end gap-3">
        <button
          id="spam-solve-cancel"
          type="button"
          onClick={onCancel}
          className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
        >
          Cancel
        </button>
        <button
          id="spam-solve-confirm"
          type="button"
          onClick={() => onConfirm(kick)}
          className="rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700"
        >
          {resume ? "Resume Solve" : "Yes, solve"}
        </button>
      </div>
    </div>
  );
}
