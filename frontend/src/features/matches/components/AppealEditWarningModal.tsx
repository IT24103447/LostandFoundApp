import { AlertTriangle } from "lucide-react";

export type AppealEditWarningStage = "open" | "save";

const COPY: Record<
  AppealEditWarningStage,
  { title: string; body: string; cancel: string; confirm: string }
> = {
  open: {
    title: "Part of a match appeal",
    body: "This report is part of a match appeal. Saving changes may cancel the match permanently. Continue?",
    cancel: "Cancel",
    confirm: "Continue",
  },
  save: {
    title: "Save these changes?",
    body: "As mentioned, this report is part of a match appeal, so saving may cancel the match permanently.",
    cancel: "Keep editing",
    confirm: "Save changes",
  },
};

type Props = {
  stage: AppealEditWarningStage;
  onConfirm: () => void;
  onCancel: () => void;
};

export function AppealEditWarningModal({ stage, onConfirm, onCancel }: Props) {
  const copy = COPY[stage];

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-gray-900/40 px-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="appeal-edit-warning-title"
    >
      <div className="w-full max-w-md rounded-3xl bg-white p-8 shadow-xl">
        <div className="flex flex-col items-center text-center">
          <span className="mb-5 flex h-16 w-16 items-center justify-center rounded-full bg-amber-50">
            <AlertTriangle className="h-7 w-7 text-amber-600" strokeWidth={2.5} />
          </span>

          <h2 id="appeal-edit-warning-title" className="text-xl font-bold text-gray-900">
            {copy.title}
          </h2>
          <p className="mt-3 text-[15px] text-gray-500">{copy.body}</p>
        </div>

        <div className="mt-6 flex gap-3">
          <button
            type="button"
            onClick={onCancel}
            className="flex-1 rounded-xl border border-gray-300 bg-white py-3 text-sm font-semibold text-gray-700 transition-colors hover:bg-gray-50"
          >
            {copy.cancel}
          </button>
          <button
            type="button"
            onClick={onConfirm}
            className="flex-1 rounded-xl bg-amber-600 py-3 text-sm font-semibold text-white transition-opacity hover:opacity-95"
          >
            {copy.confirm}
          </button>
        </div>
      </div>
    </div>
  );
}
