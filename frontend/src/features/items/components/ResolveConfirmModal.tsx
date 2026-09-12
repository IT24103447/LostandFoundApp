import { Check, X, MapPin } from "lucide-react";
import { resolvePhotoUrl } from "../../../config/env";

type ResolveConfirmModalProps = {
  item: {
    title: string;
    kind: "lost" | "found";
    location: string;
    status: string;
    photoUrl?: string;
  };
  isSubmitting: boolean;
  onConfirm: () => void;
  onCancel: () => void;
};

export function ResolveConfirmModal({ item, isSubmitting, onConfirm, onCancel }: ResolveConfirmModalProps) {
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-gray-900/40 px-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby="resolve-modal-title"
    >
      <div className="relative w-full max-w-md rounded-3xl bg-white p-8 shadow-xl">
        <button
          type="button"
          onClick={onCancel}
          aria-label="Close"
          className="absolute right-5 top-5 flex h-8 w-8 items-center justify-center rounded-full text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600"
        >
          <X className="h-5 w-5" />
        </button>

        <div className="flex flex-col items-center text-center">
          <span className="mb-5 flex h-16 w-16 items-center justify-center rounded-full bg-indigo-50">
            <Check className="h-7 w-7 text-indigo-700" strokeWidth={2.5} />
          </span>

          <h2 id="resolve-modal-title" className="text-xl font-bold text-gray-900">
            Mark this report as resolved?
          </h2>
          <p className="mt-3 text-[15px] text-gray-500">
            Are you sure you no longer need this report to remain active?
          </p>
          <p className="mt-2 text-[15px] text-gray-500">
            Once resolved, this report will no longer appear as an active item in browse and search results.
          </p>
        </div>

        <div className="mt-6 flex items-center gap-4 rounded-2xl border border-gray-100 bg-gray-50 p-4">
          {item.photoUrl ? (
            <img
              src={resolvePhotoUrl(item.photoUrl)}
              alt=""
              className="h-16 w-16 flex-shrink-0 rounded-xl object-cover"
            />
          ) : (
            <div className="h-16 w-16 flex-shrink-0 rounded-xl bg-gradient-to-br from-indigo-200 to-indigo-100" />
          )}
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <p className="truncate font-bold text-gray-900">{item.title}</p>
              <span
                className={`flex-shrink-0 rounded-full px-2.5 py-0.5 text-[11px] font-bold text-white ${
                  item.kind === "lost" ? "bg-indigo-900" : "bg-teal-600"
                }`}
              >
                {item.kind === "lost" ? "LOST" : "FOUND"}
              </span>
            </div>
            <p className="mt-1 flex items-center gap-1.5 text-sm text-gray-500">
              <MapPin className="h-3.5 w-3.5 flex-shrink-0" />
              <span className="truncate">{item.location}</span>
            </p>
            <p className="mt-1.5 text-sm text-gray-500">
              Status:{" "}
              <span className="rounded-full bg-indigo-50 px-2 py-0.5 text-xs font-bold text-indigo-700">
                {item.status}
              </span>
            </p>
          </div>
        </div>

        <div className="mt-6 flex gap-3">
          <button
            type="button"
            onClick={onCancel}
            disabled={isSubmitting}
            className="flex-1 rounded-xl border border-gray-300 bg-white py-3 text-sm font-semibold text-gray-700 transition-colors hover:bg-gray-50 disabled:opacity-60"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={isSubmitting}
            className="flex-1 rounded-xl bg-indigo-700 py-3 text-sm font-semibold text-white transition-opacity hover:opacity-95 disabled:opacity-60"
          >
            {isSubmitting ? "Resolving…" : "Yes, Resolve"}
          </button>
        </div>
      </div>
    </div>
  );
}