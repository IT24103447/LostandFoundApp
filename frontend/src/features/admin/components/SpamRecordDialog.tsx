import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { resolvePhotoUrl } from "../../../config/env";
import {
  ACTIVE_STATUSES,
  getAdminItem,
  getAdminItemPhotos,
  dismissSpamRecord,
  getSpamRecord,
  openSpamRecord,
  type AdminItem,
  type SolveResult,
  type SpamRecord,
  type SpamRecordDetail,
} from "../api/spamRecords";
import { useSolveRunner, type KickRunState, type ListingRunState } from "../solve/solveRunnerContext";
import { SolveConfirm } from "./SolveConfirm";
import { ScoreBox, StatusBadge, UserCell, type ContactState } from "./spamReviewShared";

const REFRESH_MS = 3000;

const SOLVE_RESULT_LABELS: Record<SolveResult, { label: string; style: string }> = {
  DELETED: { label: "Deleted by Solve", style: "bg-emerald-100 text-emerald-800" },
  SKIPPED: { label: "Skipped", style: "bg-gray-200 text-gray-700" },
  FAILED: { label: "Delete failed", style: "bg-rose-100 text-rose-800" },
};

const RUN_LABELS: Record<ListingRunState, { label: string; style: string }> = {
  pending: { label: "Waiting", style: "bg-gray-100 text-gray-600" },
  deleting: { label: "Deleting…", style: "bg-amber-100 text-amber-800" },
  deleted: SOLVE_RESULT_LABELS.DELETED,
  skipped: SOLVE_RESULT_LABELS.SKIPPED,
  failed: SOLVE_RESULT_LABELS.FAILED,
};

const KICK_LABELS: Record<KickRunState, string> = {
  "not-requested": "",
  pending: "Kick: waiting for the listings",
  kicking: "Kick: kicking…",
  kicked: "Kick: user kicked",
  failed: "Kick: failed. Kick the user from User Management.",
};

const SAVED_KICK: Record<SpamRecordDetail["kickStatus"], KickRunState> = {
  NOT_REQUESTED: "not-requested",
  PENDING: "pending",
  KICKED: "kicked",
  FAILED: "failed",
};

const SOLVE_BLOCKED = "Can't check the listings right now. Try again later.";

export type ItemState =
  | { kind: "loading" }
  | { kind: "loaded"; item: AdminItem }
  | { kind: "missing" }
  | { kind: "unavailable" };

type Phase = "warning" | "loading" | "open" | "error";

type PhotoState = string[] | "unavailable";

function PhotoStrip({
  listingId,
  photos,
  onOpen,
}: {
  listingId: string;
  photos: PhotoState | undefined;
  onOpen: (url: string) => void;
}) {
  const [broken, setBroken] = useState<Set<string>>(new Set());

  if (photos === undefined || (Array.isArray(photos) && photos.length === 0)) return null;

  if (photos === "unavailable") {
    return (
      <p id={`spam-listing-photo-unavailable-${listingId}`} className="mt-2 text-xs text-gray-500">
        Photo unavailable
      </p>
    );
  }

  return (
    <div id={`spam-listing-photos-${listingId}`} className="mt-2 flex flex-wrap gap-2">
      {photos.map((url) =>
        broken.has(url) ? (
          <span
            key={url}
            className="flex h-20 w-20 items-center justify-center rounded-lg bg-gray-100 p-1 text-center text-[11px] text-gray-500"
          >
            Photo unavailable
          </span>
        ) : (
          <button key={url} type="button" onClick={() => onOpen(url)} className="rounded-lg focus:outline-none focus:ring-2 focus:ring-indigo-500">
            <img
              src={resolvePhotoUrl(url)}
              alt="Listing photo"
              className="h-20 w-20 rounded-lg object-cover"
              onError={() => setBroken((current) => new Set(current).add(url))}
            />
          </button>
        ),
      )}
    </div>
  );
}

function isActive(status: string) {
  return ACTIVE_STATUSES.includes(status as SpamRecordDetail["status"]);
}

function listingState(item: AdminItem) {
  if (item.deletedAt) return { label: "Deleted", style: "bg-rose-100 text-rose-800" };
  if (item.status === "RESOLVED") return { label: "Resolved", style: "bg-sky-100 text-sky-800" };
  return { label: "Active", style: "bg-emerald-100 text-emerald-800" };
}

function formatDate(value: string) {
  return new Date(value).toLocaleString();
}

function errorText(reason: unknown) {
  const response = reason as { status?: number; body?: { error?: string } };
  if (response?.status === 403) return "You need an admin account to open Spam records.";
  return response?.body?.error ?? "Couldn't open this Spam record. Please try again.";
}

type Props = {
  record: SpamRecord;
  contact: ContactState | undefined;
  onClose: () => void;
};

export function SpamRecordDialog({ record, contact, onClose }: Props) {
  const [phase, setPhase] = useState<Phase>(record.status === "NEEDS_REVIEW" ? "warning" : "loading");
  const [detail, setDetail] = useState<SpamRecordDetail | null>(null);
  const [items, setItems] = useState<Record<string, ItemState>>({});
  const [error, setError] = useState<string | null>(null);
  const [changedNote, setChangedNote] = useState<string | null>(null);
  const [photos, setPhotos] = useState<Record<string, PhotoState>>({});
  const [viewing, setViewing] = useState<string | null>(null);
  const [confirmingDismiss, setConfirmingDismiss] = useState(false);
  const [acting, setActing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [confirmingSolve, setConfirmingSolve] = useState(false);
  const { runs, startSolve } = useSolveRunner();
  const run = runs[record.id];
  const runPhase = run?.phase;
  const hasLocalRun = run !== undefined;

  const labelOf = (listingId: string, listingType: string) => {
    const state = items[listingId];
    if (state?.kind === "loaded") return state.item.title;
    return `${listingType === "lost" ? "Lost" : "Found"} listing`;
  };

  const labelsOf = (listings: SpamRecordDetail["listings"]) =>
    Object.fromEntries(listings.map((listing) => [listing.listingId, labelOf(listing.listingId, listing.listingType)]));

  const confirmSolve = (kick: boolean) => {
    if (!detail) return;
    setConfirmingSolve(false);
    startSolve({
      recordId: record.id,
      userId: detail.userId,
      userName: contact && typeof contact === "object" ? contact.name : "this user",
      kick,
      resume: detail.status === "PENDING_SOLVE",
      labels: labelsOf(detail.listings),
    });
  };

  const dismiss = async () => {
    setActing(true);
    setActionError(null);

    try {
      await dismissSpamRecord(record.id);
      onClose();
    } catch (reason) {
      setConfirmingDismiss(false);
      setActionError(errorText(reason));
      getSpamRecord(record.id).then(setDetail).catch(() => undefined);
    } finally {
      setActing(false);
    }
  };
  const itemsRequested = useRef(false);

  const loadItems = useCallback((loaded: SpamRecordDetail) => {
    if (itemsRequested.current) return;
    itemsRequested.current = true;

    setItems(Object.fromEntries(loaded.listings.map((listing) => [listing.listingId, { kind: "loading" }])));

    loaded.listings.forEach((listing) => {
      getAdminItem(listing.listingType, listing.listingId)
        .then((item) => setItems((current) => ({ ...current, [listing.listingId]: { kind: "loaded", item } })))
        .catch((reason: { status?: number }) =>
          setItems((current) => ({
            ...current,
            [listing.listingId]: { kind: reason?.status === 404 ? "missing" : "unavailable" },
          })),
        );

      getAdminItemPhotos(listing.listingType, listing.listingId)
        .then((result) => setPhotos((current) => ({ ...current, [listing.listingId]: result.photoUrls })))
        .catch(() => setPhotos((current) => ({ ...current, [listing.listingId]: "unavailable" })));
    });
  }, []);

  const openRecord = useCallback(async () => {
    setPhase("loading");
    setError(null);

    try {
      const loaded = isActive(record.status) ? await openSpamRecord(record.id) : await getSpamRecord(record.id);
      setDetail(loaded);
      setPhase("open");
      loadItems(loaded);
    } catch (reason) {
      setError(errorText(reason));
      setPhase("error");
    }
  }, [record.id, record.status, loadItems]);

  const openRequested = useRef(false);

  useEffect(() => {
    if (record.status === "NEEDS_REVIEW" || openRequested.current) return;
    openRequested.current = true;
    void openRecord();
  }, [record.status, openRecord]);

  const liveStatus = detail?.status;

  useEffect(() => {
    if (phase !== "open" || !liveStatus || !isActive(liveStatus)) return;

    const timer = window.setInterval(() => {
      getSpamRecord(record.id)
        .then((latest) => {
          setDetail(latest);
          if (!isActive(latest.status) && !hasLocalRun) {
            setChangedNote(
              `This record was ${latest.status === "DISMISSED" ? "dismissed" : "solved"} by another admin. It is now closed.`,
            );
          }
        })
        .catch(() => undefined);
    }, REFRESH_MS);

    return () => window.clearInterval(timer);
  }, [phase, liveStatus, record.id, hasLocalRun]);

  useEffect(() => {
    if (runPhase === "solved" || runPhase === "stopped") {
      getSpamRecord(record.id).then(setDetail).catch(() => undefined);
    }
  }, [runPhase, record.id]);

  if (phase === "warning") {
    return createPortal(
      <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
        <div id="spam-open-warning" className="mx-4 w-full max-w-sm rounded-lg bg-white p-6 shadow-xl">
          <h3 className="mb-2 text-lg font-semibold text-gray-900">Open Spam record</h3>
          <p className="mb-6 text-sm text-gray-600">
            Opening this record stops it collecting new listings and moves it to Under review. This can't be undone.
          </p>
          <div className="flex justify-end gap-3">
            <button
              id="spam-open-cancel"
              type="button"
              onClick={onClose}
              className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
            >
              Cancel
            </button>
            <button
              id="spam-open-confirm"
              type="button"
              onClick={() => void openRecord()}
              className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
            >
              Open record
            </button>
          </div>
        </div>
      </div>,
      document.body,
    );
  }

  const readOnly = detail !== null && !isActive(detail.status);
  const kickState = run?.kick ?? (detail ? SAVED_KICK[detail.kickStatus] : "not-requested");
  const kickLabel = KICK_LABELS[kickState];
  const canSolve =
    (detail?.status === "UNDER_REVIEW" || detail?.status === "PENDING_SOLVE") && runPhase !== "running";
  const solveBlocked =
    detail !== null &&
    detail.listings.some((listing) => {
      const kind = items[listing.listingId]?.kind ?? "loading";
      return kind === "loading" || kind === "unavailable";
    });

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div
        id="spam-record-dialog"
        className="flex max-h-[90vh] w-full max-w-3xl flex-col overflow-hidden rounded-xl bg-white shadow-xl"
      >
        <div className="flex items-start justify-between gap-4 border-b px-6 py-4">
          <div>
            <h3 className="text-lg font-semibold text-gray-900">Spam record</h3>
            {detail && (
              <p className="text-xs text-gray-500">Flagged {formatDate(detail.flaggedAt)}</p>
            )}
          </div>
          <button
            id="spam-record-close"
            type="button"
            onClick={onClose}
            className="rounded-lg border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
          >
            Close
          </button>
        </div>

        <div className="flex-1 space-y-4 overflow-y-auto px-6 py-4">
          {phase === "loading" && <p className="text-sm text-gray-500">Opening record…</p>}

          {phase === "error" && (
            <p id="spam-record-error" className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-700">
              {error}
            </p>
          )}

          {detail && (
            <>
              {changedNote && (
                <p id="spam-record-changed" className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
                  {changedNote}
                </p>
              )}
              {readOnly && !changedNote && (
                <p id="spam-record-readonly" className="rounded-lg bg-gray-50 px-4 py-3 text-sm text-gray-600">
                  This record is closed.
                </p>
              )}

              <div className="grid gap-4 rounded-lg bg-gray-50 p-4 sm:grid-cols-3">
                <div>
                  <p className="mb-1 text-xs font-medium uppercase text-gray-400">Flagged user</p>
                  <UserCell contact={contact} />
                </div>
                <div>
                  <p className="mb-1 text-xs font-medium uppercase text-gray-400">Score / No. of listings</p>
                  <ScoreBox score={detail.scoreA} id="spam-record-score" />
                </div>
                <div>
                  <p className="mb-1 text-xs font-medium uppercase text-gray-400">Status</p>
                  <StatusBadge status={detail.status} id="spam-record-status" />
                </div>
              </div>

              {runPhase === "running" && (
                <p id="spam-solve-running" className="rounded-lg bg-amber-50 px-4 py-3 text-sm text-amber-800">
                  <span className="font-semibold">Solving…</span> You can close this popup or switch sections and it keeps
                  running. If you sign out, or close or refresh this tab, it stops. The record stays Pending solve so it can be
                  resumed.
                </p>
              )}
              {runPhase === "solved" && (
                <p id="spam-solve-done" className="rounded-lg bg-emerald-50 px-4 py-3 text-sm text-emerald-800">
                  Solve finished. This record is now Solved.
                </p>
              )}
              {run && run.errors.length > 0 && (
                <div id="spam-solve-errors" className="rounded-lg bg-red-50 px-4 py-3 text-sm text-red-700">
                  <p className="font-medium">Solve stopped with errors:</p>
                  <ul className="ml-5 list-disc">
                    {run.errors.map((message) => (
                      <li key={message}>{message}</li>
                    ))}
                  </ul>
                </div>
              )}
              {kickLabel && (
                <p id="spam-record-kick" className="text-sm text-gray-700">
                  {kickLabel}
                </p>
              )}

              <ul className="space-y-3">
                {detail.listings.map((listing) => {
                  const state = items[listing.listingId] ?? { kind: "loading" };
                  const runState = run?.listings[listing.listingId];
                  let result = listing.solveResult ? SOLVE_RESULT_LABELS[listing.solveResult] : null;
                  if (runState) result = RUN_LABELS[runState];

                  return (
                    <li
                      key={listing.listingId}
                      id={`spam-listing-${listing.listingId}`}
                      className="rounded-lg border p-4"
                    >
                      <div className="flex flex-wrap items-start justify-between gap-2">
                        <p className="font-semibold text-gray-900">
                          {state.kind === "loaded" ? state.item.title : `${listing.listingType === "lost" ? "Lost" : "Found"} listing`}
                        </p>
                        <div className="flex flex-wrap gap-2">
                          {state.kind === "loaded" && (
                            <span
                              id={`spam-listing-state-${listing.listingId}`}
                              className={`rounded-2xl px-3 py-1 text-xs font-bold ${listingState(state.item).style}`}
                            >
                              {listingState(state.item).label}
                            </span>
                          )}
                          {(state.kind === "missing" || state.kind === "unavailable") && (
                            <span
                              id={`spam-listing-state-${listing.listingId}`}
                              className="rounded-2xl bg-gray-200 px-3 py-1 text-xs font-bold text-gray-700"
                            >
                              State unavailable
                            </span>
                          )}
                          {result && (
                            <span
                              id={`spam-listing-result-${listing.listingId}`}
                              className={`rounded-2xl px-3 py-1 text-xs font-bold ${result.style}`}
                            >
                              {result.label}
                            </span>
                          )}
                        </div>
                      </div>

                      {state.kind === "loading" && <p className="mt-1 text-sm text-gray-400">Loading listing…</p>}

                      {state.kind === "loaded" && (
                        <>
                          <p className="mt-1 whitespace-pre-wrap text-sm text-gray-700">{state.item.description}</p>
                          <PhotoStrip listingId={listing.listingId} photos={photos[listing.listingId]} onOpen={setViewing} />
                          <p className="mt-2 text-xs text-gray-500">
                            Created {formatDate(state.item.createdAt)}
                            {state.item.deletedAt && ` · Deleted ${formatDate(state.item.deletedAt)}`}
                          </p>
                        </>
                      )}
                    </li>
                  );
                })}
              </ul>
            </>
          )}
        </div>

        {detail && canSolve && (
          <div className="border-t px-6 py-4">
            {actionError && (
              <p id="spam-record-action-error" className="mb-3 rounded-lg bg-red-50 px-4 py-3 text-sm text-red-700">
                {actionError}
              </p>
            )}

            {confirmingSolve && (
              <SolveConfirm
                detail={detail}
                items={items}
                labels={labelsOf(detail.listings)}
                resume={detail.status === "PENDING_SOLVE"}
                userDeleted={contact === "deleted"}
                onConfirm={confirmSolve}
                onCancel={() => setConfirmingSolve(false)}
              />
            )}

            {!confirmingSolve && confirmingDismiss && (
              <div id="spam-dismiss-confirm-dialog" className="flex flex-wrap items-center justify-end gap-3 rounded-lg border border-gray-200 bg-gray-50 p-4">
                <span className="text-sm text-gray-700">
                  Dismiss this record as a false positive? Its listings won't be changed.
                </span>
                <button
                  id="spam-dismiss-cancel"
                  type="button"
                  disabled={acting}
                  onClick={() => setConfirmingDismiss(false)}
                  className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                >
                  Cancel
                </button>
                <button
                  id="spam-dismiss-confirm"
                  type="button"
                  disabled={acting}
                  onClick={() => void dismiss()}
                  className="rounded-lg bg-gray-700 px-4 py-2 text-sm font-medium text-white hover:bg-gray-800 disabled:opacity-50"
                >
                  {acting ? "Dismissing…" : "Yes, dismiss"}
                </button>
              </div>
            )}

            {!confirmingSolve && !confirmingDismiss && (
              <div className="flex flex-wrap items-center justify-end gap-3">
                {solveBlocked && (
                  <span id="spam-solve-blocked" className="text-sm text-gray-500">
                    {SOLVE_BLOCKED}
                  </span>
                )}
                {detail.status === "UNDER_REVIEW" && (
                  <button
                    id="spam-record-dismiss"
                    type="button"
                    onClick={() => {
                      setActionError(null);
                      setConfirmingDismiss(true);
                    }}
                    className="rounded-lg border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
                  >
                    Dismiss
                  </button>
                )}
                <button
                  id="spam-record-solve"
                  type="button"
                  disabled={solveBlocked}
                  onClick={() => {
                    setActionError(null);
                    setConfirmingSolve(true);
                  }}
                  className="rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50"
                >
                  {detail.status === "PENDING_SOLVE" ? "Resume Solve" : "Solve"}
                </button>
              </div>
            )}
          </div>
        )}
      </div>

      {viewing && (
        <div
          id="spam-photo-viewer"
          className="fixed inset-0 z-[60] flex items-center justify-center bg-black/80 p-4"
          onClick={() => setViewing(null)}
        >
          <img src={resolvePhotoUrl(viewing)} alt="Listing photo, full size" className="max-h-[85vh] max-w-full rounded-lg" />
          <button
            id="spam-photo-viewer-close"
            type="button"
            onClick={() => setViewing(null)}
            className="absolute right-4 top-4 rounded-lg bg-white px-3 py-1.5 text-sm font-medium text-gray-800"
          >
            Close
          </button>
        </div>
      )}
    </div>,
    document.body,
  );
}
