import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { MatchItemCard } from "../../matches/components/MatchItemCard";
import { matchingError } from "../../matches/api/matches";
import type { AppealStatus } from "../../matches/api/appeals";
import {
  ADMIN_APPEAL_PAGE_SIZE,
  getAdminAppeals,
  getUserContact,
  openAdminAppeal,
  rejectAdminAppeal,
  verifyAdminAppeal,
  type AdminAppeal,
  type AdminAppealDetail,
  type ScoreBreakdown,
  type UserContact,
} from "../api/matchAppeals";

const STATUS_TABS: { value: AppealStatus; label: string }[] = [
  { value: "PENDING", label: "Pending" },
  { value: "VERIFIED", label: "Verified" },
  { value: "REJECTED", label: "Rejected" },
];

const STATUS_STYLES: Record<AppealStatus, string> = {
  PENDING: "bg-amber-100 text-amber-800",
  VERIFIED: "bg-emerald-100 text-emerald-800",
  REJECTED: "bg-rose-100 text-rose-800",
};

type ContactState = UserContact | "deleted" | "loading" | "error";

const REJECT_REASON_MAX = 300;

function formatDate(value: string) {
  return new Date(value).toLocaleString();
}

function Breakdown({ breakdown }: { breakdown: ScoreBreakdown }) {
  const rows: [string, number][] = [
    ["Title", breakdown.title],
    ["Category", breakdown.category],
    ["Description", breakdown.description],
    ["Image description", breakdown.imageDescription],
    ["Image attributes", breakdown.imageAttributes],
  ];

  return (
    <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-gray-600">
      {rows.map(([label, value]) => (
        <div key={label} className="flex justify-between gap-2">
          <dt>{label}</dt>
          <dd className="font-medium text-gray-900">{value.toFixed(2)}%</dd>
        </div>
      ))}
    </dl>
  );
}

function Contact({ label, contact }: { label: string; contact: ContactState | undefined }) {
  let body: ReactNode;

  if (contact === undefined || contact === "loading") {
    body = <span className="text-gray-400">Loading…</span>;
  } else if (contact === "deleted") {
    body = <span className="font-semibold text-rose-700">Deleted user</span>;
  } else if (contact === "error") {
    body = <span className="text-gray-500">Details unavailable</span>;
  } else {
    body = (
      <>
        <span className="font-semibold text-gray-900">{contact.name}</span>
        <span>{contact.email}</span>
        <span>{contact.phoneNo}</span>
      </>
    );
  }

  return (
    <div className="flex flex-col text-sm text-gray-600">
      <span className="text-xs font-medium uppercase text-gray-400">{label}</span>
      {body}
    </div>
  );
}

export function MatchAppealsSection() {
  const [status, setStatus] = useState<AppealStatus>("PENDING");
  const [appeals, setAppeals] = useState<AdminAppeal[]>([]);
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [contacts, setContacts] = useState<Record<string, ContactState>>({});
  const [details, setDetails] = useState<Record<string, AdminAppealDetail | "loading">>({});
  const [confirming, setConfirming] = useState<{ id: string; action: "verify" | "reject" } | null>(null);
  const [acting, setActing] = useState(false);
  const [actionError, setActionError] = useState<Record<string, string>>({});
  const [rejectReason, setRejectReason] = useState("");

  const startConfirm = (id: string, action: "verify" | "reject") => {
    setRejectReason("");
    setConfirming({ id, action });
  };

  const requestedContacts = useRef(new Set<string>());

  const loadContacts = useCallback((items: AdminAppeal[]) => {
    const ids = [
      ...new Set(items.flatMap((appeal) => [appeal.lostReporterId, appeal.finderId])),
    ].filter((id) => !requestedContacts.current.has(id));

    ids.forEach((id) => {
      requestedContacts.current.add(id);
      getUserContact(id)
        .then((contact) => setContacts((current) => ({ ...current, [id]: contact })))
        .catch((reason: { status?: number }) =>
          setContacts((current) => ({
            ...current,
            [id]: reason?.status === 404 ? "deleted" : "error",
          })),
        );
    });
  }, []);

  const load = useCallback(
    (selected: AppealStatus, pageToLoad: number) => {
      setLoading(true);
      setError(null);

      getAdminAppeals(selected, pageToLoad)
        .then((items) => {
          setAppeals((current) => (pageToLoad === 1 ? items : [...current, ...items]));
          setHasMore(items.length === ADMIN_APPEAL_PAGE_SIZE);
          setPage(pageToLoad);
          loadContacts(items);
        })
        .catch((reason) => setError(matchingError(reason)))
        .finally(() => setLoading(false));
    },
    [loadContacts],
  );

  useEffect(() => {
    setDetails({});
    setConfirming(null);
    load(status, 1);
  }, [status, load]);

  const openAppeal = async (id: string) => {
    setDetails((current) => ({ ...current, [id]: "loading" }));
    try {
      const detail = await openAdminAppeal(id);
      setDetails((current) => ({ ...current, [id]: detail }));
    } catch (reason) {
      setDetails((current) => {
        const next = { ...current };
        delete next[id];
        return next;
      });
      setActionError((current) => ({ ...current, [id]: matchingError(reason) }));
    }
  };

  const decide = async () => {
    if (!confirming) return;

    const { id, action } = confirming;
    setActing(true);
    setActionError((current) => ({ ...current, [id]: "" }));

    try {
      if (action === "verify") {
        await verifyAdminAppeal(id);
      } else {
        await rejectAdminAppeal(id, rejectReason);
      }
      setConfirming(null);
      setAppeals((current) => current.filter((appeal) => appeal.id !== id));
    } catch (reason) {
      setConfirming(null);
      setActionError((current) => ({ ...current, [id]: matchingError(reason) }));
    } finally {
      setActing(false);
    }
  };

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold text-gray-900">Match Appeals</h1>

      <div role="tablist" className="flex gap-2 border-b border-gray-200">
        {STATUS_TABS.map((tab) => (
          <button
            key={tab.value}
            type="button"
            role="tab"
            aria-selected={status === tab.value}
            onClick={() => setStatus(tab.value)}
            className={`-mb-px border-b-2 px-4 py-2 text-sm font-semibold ${
              status === tab.value
                ? "border-indigo-600 text-indigo-700"
                : "border-transparent text-gray-500 hover:text-gray-800"
            }`}
          >
            {tab.label}
          </button>
        ))}
      </div>

      {error && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      {loading && appeals.length === 0 ? (
        <p className="text-sm text-gray-500">Loading appeals…</p>
      ) : appeals.length === 0 && !error ? (
        <p className="rounded-lg border bg-white p-6 text-sm text-gray-500">
          No {status.toLowerCase()} appeals.
        </p>
      ) : (
        <div className="space-y-5">
          {appeals.map((appeal) => {
            const lostContact = contacts[appeal.lostReporterId];
            const foundContact = contacts[appeal.finderId];
            const userDeleted = lostContact === "deleted" || foundContact === "deleted";
            const detail = details[appeal.id];
            const isConfirming = confirming?.id === appeal.id;

            return (
              <article key={appeal.id} className="rounded-xl border bg-white p-5 shadow-sm">
                <div className="mb-4 flex flex-wrap items-start justify-between gap-3">
                  <div className="text-sm text-gray-600">
                    <p>
                      Sent {formatDate(appeal.createdAt)} by the{" "}
                      <strong>{appeal.appellantRole === "LOST" ? "lost reporter" : "finder"}</strong>
                    </p>
                    {appeal.decidedAt && <p>Decided {formatDate(appeal.decidedAt)}</p>}
                  </div>
                  <span className={`rounded-full px-3 py-1 text-xs font-bold ${STATUS_STYLES[appeal.status]}`}>
                    {STATUS_TABS.find((tab) => tab.value === appeal.status)?.label}
                  </span>
                </div>

                <div className="grid gap-4 sm:grid-cols-2">
                  <MatchItemCard item={appeal.lost} showDescription />
                  <MatchItemCard item={appeal.found} showDescription />
                </div>

                <div className="mt-4 grid gap-4 rounded-lg bg-gray-50 p-4 sm:grid-cols-2">
                  <Contact label="Lost reporter" contact={lostContact} />
                  <Contact label="Finder" contact={foundContact} />
                </div>

                <div className="mt-4 rounded-lg border border-gray-100 p-4">
                  <p className="mb-2 text-sm font-semibold text-gray-900">
                    Score when sent: {appeal.score.toFixed(2)}%
                  </p>
                  <Breakdown breakdown={appeal.breakdown} />
                </div>

                {detail === "loading" && (
                  <p className="mt-4 text-sm text-gray-500">Checking the current score…</p>
                )}

                {detail && detail !== "loading" && (
                  <div className="mt-4 rounded-lg border border-indigo-100 bg-indigo-50 p-4">
                    {detail.current ? (
                      <>
                        <p className="mb-2 text-sm font-semibold text-indigo-900">
                          Current score: {detail.current.score.toFixed(2)}%
                        </p>
                        <Breakdown breakdown={detail.current.breakdown} />
                      </>
                    ) : detail.currentUnavailableReason === "REPORT_INACTIVE" ? (
                      <p className="text-sm font-semibold text-rose-700">Report no longer active</p>
                    ) : detail.currentUnavailableReason === "SCORE_UNAVAILABLE" ? (
                      <p className="text-sm text-gray-700">
                        Current score unavailable, try again shortly
                      </p>
                    ) : (
                      <p className="text-sm text-gray-700">This appeal has already been decided.</p>
                    )}
                  </div>
                )}

                {appeal.note && (
                  <div className="mt-4 rounded-lg bg-gray-50 p-4">
                    <p className="text-xs font-medium uppercase text-gray-400">Note from the user</p>
                    <p className="mt-1 whitespace-pre-wrap text-sm text-gray-700">{appeal.note}</p>
                  </div>
                )}

                {appeal.status === "REJECTED" && appeal.rejectionReason && (
                  <div className="mt-4 rounded-lg bg-rose-50 p-4">
                    <p className="text-xs font-medium uppercase text-rose-500">Reason given to the user</p>
                    <p className="mt-1 whitespace-pre-wrap text-sm text-rose-900">{appeal.rejectionReason}</p>
                  </div>
                )}

                {actionError[appeal.id] && (
                  <p className="mt-4 rounded-lg bg-red-50 px-4 py-3 text-sm text-red-700">
                    {actionError[appeal.id]}
                  </p>
                )}

                {isConfirming && confirming.action === "reject" && (
                  <div className="mt-4">
                    <label
                      htmlFor={`reject-reason-${appeal.id}`}
                      className="block text-sm font-medium text-gray-700"
                    >
                      Reason for the user (optional)
                    </label>
                    <textarea
                      id={`reject-reason-${appeal.id}`}
                      value={rejectReason}
                      maxLength={REJECT_REASON_MAX}
                      rows={3}
                      onChange={(event) => setRejectReason(event.target.value)}
                      placeholder="Only the user who sent the appeal will see this"
                      className="mt-1 w-full rounded-lg border border-gray-300 p-3 text-sm focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none"
                    />
                    <p className="text-right text-xs text-gray-500">
                      {rejectReason.length}/{REJECT_REASON_MAX}
                    </p>
                  </div>
                )}

                {appeal.status === "PENDING" && (
                  <div className="mt-4 flex flex-wrap justify-end gap-3">
                    {isConfirming ? (
                      <>
                        <span className="self-center text-sm text-gray-700">
                          {confirming.action === "verify"
                            ? "Create a match for this pair?"
                            : "Reject this appeal?"}
                        </span>
                        <button
                          type="button"
                          disabled={acting}
                          onClick={() => setConfirming(null)}
                          className="rounded-lg border px-4 py-2 text-sm disabled:opacity-50"
                        >
                          Go back
                        </button>
                        <button
                          type="button"
                          disabled={acting}
                          onClick={decide}
                          className={`rounded-lg px-4 py-2 text-sm font-semibold text-white disabled:opacity-50 ${
                            confirming.action === "verify" ? "bg-emerald-600" : "bg-rose-600"
                          }`}
                        >
                          {acting
                            ? "Saving…"
                            : confirming.action === "verify"
                              ? "Yes, verify"
                              : "Yes, reject"}
                        </button>
                      </>
                    ) : (
                      <>
                        <button
                          type="button"
                          disabled={detail === "loading"}
                          onClick={() => openAppeal(appeal.id)}
                          className="rounded-lg border px-4 py-2 text-sm disabled:opacity-50"
                        >
                          {detail ? "Recheck score" : "Open"}
                        </button>
                        <button
                          type="button"
                          onClick={() => startConfirm(appeal.id, "reject")}
                          className="rounded-lg border border-rose-300 px-4 py-2 text-sm font-semibold text-rose-700"
                        >
                          Reject
                        </button>
                        <span
                          title={userDeleted ? "A user in this pair has been deleted, so the appeal can only be rejected." : undefined}
                          className={userDeleted ? "inline-block cursor-not-allowed" : "inline-block"}
                        >
                          <button
                            type="button"
                            disabled={userDeleted}
                            onClick={() => startConfirm(appeal.id, "verify")}
                            className={`rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white ${
                              userDeleted ? "pointer-events-none opacity-40" : ""
                            }`}
                          >
                            Verify
                          </button>
                        </span>
                      </>
                    )}
                  </div>
                )}
              </article>
            );
          })}

          {hasMore && (
            <button
              type="button"
              disabled={loading}
              onClick={() => load(status, page + 1)}
              className="rounded-lg border bg-white px-4 py-2 text-sm disabled:opacity-40"
            >
              {loading ? "Loading…" : "Load more"}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
