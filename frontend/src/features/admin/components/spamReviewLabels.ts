import type { SpamRecordStatus } from "../api/spamRecords";

export const STATUS_LABELS: Record<SpamRecordStatus, { label: string; style: string }> = {
  NEEDS_REVIEW: { label: "Needs review", style: "bg-amber-100 text-amber-800" },
  UNDER_REVIEW: { label: "Under review", style: "bg-sky-100 text-sky-800" },
  PENDING_SOLVE: { label: "Pending solve", style: "bg-violet-100 text-violet-800" },
  SOLVED: { label: "Solved", style: "bg-emerald-100 text-emerald-800" },
  DISMISSED: { label: "Dismissed", style: "bg-gray-200 text-gray-700" },
};
