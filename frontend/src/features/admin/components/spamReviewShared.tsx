import type { ReactNode } from "react";
import type { UserContact } from "../api/matchAppeals";
import type { SpamRecordStatus } from "../api/spamRecords";
import { STATUS_LABELS } from "./spamReviewLabels";

export type ContactState = UserContact | "deleted" | "error";

export function StatusBadge({ status, id }: { status: SpamRecordStatus; id?: string }) {
  return (
    <span id={id} className={`inline-block rounded-2xl px-3 py-1 text-xs font-bold ${STATUS_LABELS[status].style}`}>
      {STATUS_LABELS[status].label}
    </span>
  );
}

export function ScoreBox({ score, id }: { score: number; id?: string }) {
  return (
    <span
      id={id}
      className="inline-block min-w-10 rounded-lg bg-purple-50 px-3 py-1 text-center text-lg font-bold text-purple-800 ring-1 ring-purple-200"
    >
      {score}
    </span>
  );
}

export function UserCell({ contact }: { contact: ContactState | undefined }) {
  let body: ReactNode;

  if (contact === undefined) {
    body = <span className="text-gray-400">Loading…</span>;
  } else if (contact === "deleted") {
    body = <span className="font-semibold text-rose-700">Deleted user</span>;
  } else if (contact === "error") {
    body = <span className="text-gray-500">Details unavailable</span>;
  } else {
    body = (
      <>
        <span className="font-semibold text-gray-900">{contact.name}</span>
        <span className="break-all">{contact.email}</span>
        <span>{contact.phoneNo}</span>
      </>
    );
  }

  return <div className="flex flex-col text-sm text-gray-600">{body}</div>;
}
