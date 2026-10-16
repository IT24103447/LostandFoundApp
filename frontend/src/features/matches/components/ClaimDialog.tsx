import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  getClaimCandidates,
  matchingError,
  previewClaim,
  submitClaim,
  type ClaimItem,
  type ClaimPreview,
  type PairRequest,
  type ReportType,
  type SavedMatch,
} from "../api/matches";
import {
  APPEAL_NOTE_MAX,
  getPairAppealStatus,
  sendAppeal,
} from "../api/appeals";
import { MatchItemCard } from "./MatchItemCard";

const APPEAL_UNAVAILABLE_HINT =
  "This pair has already been submitted for appeal by you or the other user.";

type Props = {
  targetId: string;
  targetType: ReportType;
  onClose: () => void;
};

export function ClaimDialog({
  targetId,
  targetType,
  onClose,
}: Props) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const requestRef =
    useRef<AbortController | null>(null);
  const submittingRef = useRef(false);

  const [candidates, setCandidates] =
    useState<ClaimItem[]>([]);

  const [loading, setLoading] = useState(true);
  const [previewLoading, setPreviewLoading] =
    useState(false);
  const [submitting, setSubmitting] =
    useState(false);

  const [preview, setPreview] =
    useState<ClaimPreview | null>(null);

  const [pair, setPair] =
    useState<PairRequest | null>(null);

  const [saved, setSaved] =
    useState<SavedMatch | null>(null);

  const [error, setError] = useState("");

  const [appealed, setAppealed] = useState(false);
  const [appealOpen, setAppealOpen] = useState(false);
  const [appealNote, setAppealNote] = useState("");
  const [appealSent, setAppealSent] = useState(false);

  const ownType: ReportType =
    targetType === "FOUND"
      ? "LOST"
      : "FOUND";

  const reportLabel =
    ownType === "LOST"
      ? "lost"
      : "found";

  useEffect(() => {
    const dialog = dialogRef.current;

    if (dialog && !dialog.open) {
      dialog.showModal();
    }

    return () => {
      requestRef.current?.abort();
      dialog?.close();
    };
  }, []);

  useEffect(() => {
    const controller = new AbortController();

    setLoading(true);
    setError("");

    getClaimCandidates(ownType, controller.signal)
      .then((items) => {
        if (!controller.signal.aborted) {
          setCandidates(items);
        }
      })
      .catch((reason) => {
        if (!controller.signal.aborted) {
          setError(matchingError(reason));
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false);
        }
      });

    return () => controller.abort();
  }, [ownType]);

  const close = () => {
    if (submittingRef.current) {
      return;
    }

    requestRef.current?.abort();
    onClose();
  };

  const selectReport = async (
    item: ClaimItem,
  ) => {
    requestRef.current?.abort();

    const controller = new AbortController();
    requestRef.current = controller;

    const selectedPair: PairRequest =
      targetType === "FOUND"
        ? {
            lostItemId: item.id,
            foundItemId: targetId,
          }
        : {
            lostItemId: targetId,
            foundItemId: item.id,
          };

    setError("");
    setPreview(null);
    setPair(selectedPair);
    resetAppeal();
    setPreviewLoading(true);

    try {
      const result = await previewClaim(
        selectedPair,
        controller.signal,
      );

      if (!result.canClaim) {
        await refreshAppealStatus(
          selectedPair,
          controller.signal,
        );
      }

      if (!controller.signal.aborted) {
        setPreview(result);
      }
    } catch (reason) {
      if (!controller.signal.aborted) {
        setError(matchingError(reason));
      }
    } finally {
      if (!controller.signal.aborted) {
        setPreviewLoading(false);
      }
    }
  };

  function resetAppeal() {
    setAppealed(false);
    setAppealOpen(false);
    setAppealNote("");
  }

  async function refreshAppealStatus(
    selectedPair: PairRequest,
    signal?: AbortSignal,
  ) {
    try {
      const status = await getPairAppealStatus(
        selectedPair,
        signal,
      );

      if (!signal?.aborted) {
        setAppealed(status.appealed);
      }
    } catch {
      // The status only greys out the Appeal button; the server still refuses a second appeal.
    }
  }

  const confirmAppeal = async () => {
    if (
      !pair ||
      !preview ||
      preview.canClaim ||
      submittingRef.current
    ) {
      return;
    }

    submittingRef.current = true;
    setSubmitting(true);
    setError("");

    try {
      await sendAppeal(
        pair,
        preview.previewVersion,
        appealNote,
      );

      setAppealSent(true);
    } catch (reason) {
      setError(matchingError(reason));
      setAppealOpen(false);
      await refreshAppealStatus(pair);
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  };

  const confirmClaim = async () => {
    if (
      !pair ||
      !preview?.canClaim ||
      submittingRef.current
    ) {
      return;
    }

    submittingRef.current = true;
    setSubmitting(true);
    setError("");

    try {
      const result = await submitClaim(
        pair,
        preview.previewVersion,
      );

      setSaved(result);
    } catch (reason) {
      setError(matchingError(reason));
      setPreview(null);
      setPair(null);
    } finally {
      submittingRef.current = false;
      setSubmitting(false);
    }
  };

  return (
    <dialog
      ref={dialogRef}
      aria-labelledby="claim-dialog-title"
      className="m-auto max-h-[90vh] w-[min(95vw,760px)] overflow-y-auto rounded-2xl p-0 shadow-xl backdrop:bg-black/40"
      onCancel={(event) => {
        event.preventDefault();
        close();
      }}
      onClick={(event) => {
        if (event.target === event.currentTarget) {
          close();
        }
      }}
    >
      <div className="p-6">
        <div className="mb-5 flex items-center justify-between gap-4">
          <h2
            id="claim-dialog-title"
            className="text-xl font-bold"
          >
            {saved
              ? "Claim submitted"
              : appealSent
                ? "Appeal sent"
                : "Preview your match"}
          </h2>

          <button
            type="button"
            onClick={close}
            disabled={submitting}
            aria-label="Close claim popup"
            className="rounded-lg px-3 py-2 text-gray-600 hover:bg-gray-100 disabled:opacity-50"
          >
            Close
          </button>
        </div>

        {error && (
          <p
            role="alert"
            className="mb-4 rounded-lg bg-red-50 p-3 text-sm text-red-700"
          >
            {error}
          </p>
        )}

        {saved ? (
          <div className="space-y-4">
            <p>
              Your claim was saved with a similarity
              score of{" "}
              <strong>
                {saved.score.toFixed(2)}%
              </strong>.
            </p>

            <p className="text-sm text-gray-600">
              It is now visible to both parties under
              Matched Items and is waiting for the other
              reporter’s decision.
            </p>

            <Link
              to="/matched-items"
              onClick={onClose}
              className="inline-block rounded-xl bg-indigo-600 px-5 py-3 font-semibold text-white"
            >
              View Matched Items
            </Link>
          </div>
        ) : appealSent ? (
          <div className="space-y-4">
            <p>Appeal sent. An admin will review it.</p>

            <p className="text-sm text-gray-600">
              You can follow its status under My appeals
              on the Matched Items page.
            </p>

            <Link
              to="/matched-items?tab=appeals"
              onClick={onClose}
              className="inline-block rounded-xl bg-indigo-600 px-5 py-3 font-semibold text-white"
            >
              View My appeals
            </Link>
          </div>
        ) : preview ? (
          <div className="space-y-5">
            <div className="grid gap-4 sm:grid-cols-2">
              <MatchItemCard item={preview.lost} />
              <MatchItemCard item={preview.found} />
            </div>

            <div className="rounded-xl bg-indigo-50 p-4 text-center">
              <p className="text-sm text-gray-600">
                Similarity score
              </p>

              <p className="text-3xl font-bold text-indigo-700">
                {preview.score.toFixed(2)}%
              </p>

              <p className="mt-2 text-sm text-gray-600">
                A score of {preview.threshold}% or above
                is required. Similarity does not prove
                ownership.
              </p>
            </div>

            {!preview.canClaim && (
              <p className="text-sm text-red-700">
                These reports do not meet the similarity
                threshold. Select another report.
              </p>
            )}

            {appealOpen ? (
              <div className="space-y-3 rounded-xl border border-amber-200 bg-amber-50 p-4">
                <p className="font-semibold text-amber-900">
                  Send this pair to an admin for review?
                </p>

                <p className="text-sm text-amber-900">
                  Do not edit your report while this appeal is
                  being reviewed or waiting for the other user.
                  Editing it may cancel the match permanently.
                </p>

                <label
                  htmlFor="appeal-note"
                  className="block text-sm font-medium text-gray-700"
                >
                  Note to the admin (optional)
                </label>

                <textarea
                  id="appeal-note"
                  value={appealNote}
                  maxLength={APPEAL_NOTE_MAX}
                  rows={3}
                  onChange={(event) =>
                    setAppealNote(event.target.value)
                  }
                  placeholder="Explain why this pair is a genuine match"
                  className="w-full rounded-lg border border-gray-300 bg-white p-3 text-sm"
                />

                <p className="text-right text-xs text-gray-500">
                  {appealNote.length}/{APPEAL_NOTE_MAX}
                </p>

                <div className="flex flex-wrap justify-end gap-3">
                  <button
                    type="button"
                    disabled={submitting}
                    onClick={() => setAppealOpen(false)}
                    className="rounded-xl border bg-white px-4 py-3 disabled:opacity-50"
                  >
                    Go back
                  </button>

                  <button
                    type="button"
                    disabled={submitting}
                    onClick={confirmAppeal}
                    className="rounded-xl bg-amber-600 px-5 py-3 font-semibold text-white disabled:opacity-50"
                  >
                    {submitting
                      ? "Sending…"
                      : "Send appeal"}
                  </button>
                </div>
              </div>
            ) : (
            <div className="flex flex-wrap justify-end gap-3">
              <button
                type="button"
                disabled={submitting}
                onClick={() => {
                  setPreview(null);
                  setPair(null);
                  setError("");
                  resetAppeal();
                }}
                className="rounded-xl border px-4 py-3 disabled:opacity-50"
              >
                Choose another report
              </button>

              <button
                type="button"
                disabled={submitting}
                onClick={close}
                className="rounded-xl border px-4 py-3 disabled:opacity-50"
              >
                Cancel
              </button>

              {appealed ? (
                <span
                  title={APPEAL_UNAVAILABLE_HINT}
                  className="inline-block cursor-not-allowed"
                >
                  <button
                    type="button"
                    disabled
                    className="pointer-events-none rounded-xl border border-amber-600 px-4 py-3 font-semibold text-amber-700 opacity-40"
                  >
                    Appeal unavailable
                  </button>
                </span>
              ) : (
                <button
                  type="button"
                  disabled={
                    preview.canClaim || submitting
                  }
                  onClick={() => setAppealOpen(true)}
                  className="rounded-xl border border-amber-600 px-4 py-3 font-semibold text-amber-700 disabled:cursor-not-allowed disabled:opacity-40"
                >
                  Appeal
                </button>
              )}

              <button
                type="button"
                disabled={
                  !preview.canClaim || submitting
                }
                onClick={confirmClaim}
                className="rounded-xl bg-indigo-600 px-5 py-3 font-semibold text-white disabled:cursor-not-allowed disabled:opacity-40"
              >
                {submitting
                  ? "Submitting…"
                  : "Confirm and claim"}
              </button>
            </div>
            )}
          </div>
        ) : (
          <div className="space-y-4">
            <p className="text-gray-600">
              Select the active {reportLabel} report
              you want to claim with.
            </p>

            {loading ? (
              <p role="status">
                Loading your reports…
              </p>
            ) : candidates.length === 0 ? (
              <div className="rounded-xl bg-gray-50 p-4">
                <p>
                  You don’t have any active {reportLabel}{" "}
                  item reports.
                </p>

                <Link
                  to={
                    ownType === "LOST"
                      ? "/report-lost-item"
                      : "/report-found-item"
                  }
                  onClick={onClose}
                  className="mt-3 inline-block font-semibold text-indigo-600"
                >
                  Create a {reportLabel} report
                </Link>
              </div>
            ) : (
              <div className="space-y-2">
                {candidates.map((candidate) => (
                  <button
                    key={candidate.id}
                    type="button"
                    disabled={previewLoading}
                    onClick={() =>
                      selectReport(candidate)
                    }
                    className="block w-full rounded-xl border border-gray-200 p-4 text-left hover:border-indigo-500 hover:bg-indigo-50 disabled:opacity-50"
                  >
                    <span className="block font-semibold">
                      {candidate.title}
                    </span>

                    <span className="mt-1 block text-sm text-gray-600">
                      {candidate.category} ·{" "}
                      {candidate.date} ·{" "}
                      {candidate.location}
                    </span>
                  </button>
                ))}
              </div>
            )}

            {previewLoading && (
              <p
                role="status"
                className="text-sm text-indigo-600"
              >
                Comparing the reports…
              </p>
            )}

            <button
              type="button"
              onClick={close}
              className="rounded-xl border px-4 py-2"
            >
              Cancel
            </button>
          </div>
        )}
      </div>
    </dialog>
  );
}