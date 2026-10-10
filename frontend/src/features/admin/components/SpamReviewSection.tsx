import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { getUsers } from "../api/admin";
import { getUserContact, type UserContact } from "../api/matchAppeals";
import {
  MAX_FILTER_USERS,
  getSpamRecords,
  type SpamRecord,
  type SpamRecordStatus,
  type SpamReviewSort,
  type SpamReviewTab,
} from "../api/spamRecords";

const TABS: { value: SpamReviewTab; label: string }[] = [
  { value: "active", label: "Active" },
  { value: "dismissed", label: "Dismissed" },
  { value: "solved", label: "Solved" },
];

const STATUS_LABELS: Record<SpamRecordStatus, { label: string; style: string }> = {
  NEEDS_REVIEW: { label: "Needs review", style: "bg-amber-100 text-amber-800" },
  UNDER_REVIEW: { label: "Under review", style: "bg-sky-100 text-sky-800" },
  PENDING_SOLVE: { label: "Pending solve", style: "bg-violet-100 text-violet-800" },
  SOLVED: { label: "Solved", style: "bg-emerald-100 text-emerald-800" },
  DISMISSED: { label: "Dismissed", style: "bg-gray-200 text-gray-700" },
};

const INPUT_CLASS =
  "rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none";

type ContactState = UserContact | "deleted" | "error";

type Filters = {
  sort: SpamReviewSort;
  search: string;
  from: string;
  to: string;
};

const EMPTY_FILTERS: Filters = { sort: "score", search: "", from: "", to: "" };

function errorMessage(reason: unknown): string {
  const response = reason as { status?: number; body?: { error?: string } };

  if (response?.status === 401) return "Please sign in again.";
  if (response?.status === 403) return "You need an admin account to view Spam records.";
  return response?.body?.error ?? "Couldn't load Spam records. Please try again.";
}

function UserCell({ contact }: { contact: ContactState | undefined }) {
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

export function SpamReviewSection() {
  const [tab, setTab] = useState<SpamReviewTab>("active");
  const [filters, setFilters] = useState<Filters>(EMPTY_FILTERS);
  const [draft, setDraft] = useState<Filters>(EMPTY_FILTERS);
  const [userIds, setUserIds] = useState<string[] | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [records, setRecords] = useState<SpamRecord[]>([]);
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [contacts, setContacts] = useState<Record<string, ContactState>>({});

  const requestId = useRef(0);
  const requestedContacts = useRef(new Set<string>());
  const sentinel = useRef<HTMLDivElement | null>(null);

  const loadContacts = useCallback((items: SpamRecord[]) => {
    const ids = [...new Set(items.map((record) => record.userId))].filter(
      (id) => !requestedContacts.current.has(id),
    );

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
    (pageToLoad: number) => {
      const current = ++requestId.current;

      if (userIds?.length === 0) {
        setRecords([]);
        setHasMore(false);
        setLoading(false);
        return;
      }

      setLoading(true);
      setError(null);

      getSpamRecords({
        tab,
        sort: filters.sort,
        from: filters.from || undefined,
        to: filters.to || undefined,
        userIds: userIds ?? undefined,
        page: pageToLoad,
      })
        .then((result) => {
          if (current !== requestId.current) return;
          setRecords((existing) => (pageToLoad === 1 ? result.items : [...existing, ...result.items]));
          setHasMore(result.hasMore);
          setPage(pageToLoad);
          loadContacts(result.items);
        })
        .catch((reason) => {
          if (current === requestId.current) setError(errorMessage(reason));
        })
        .finally(() => {
          if (current === requestId.current) setLoading(false);
        });
    },
    [tab, filters, userIds, loadContacts],
  );

  useEffect(() => {
    setRecords([]);
    load(1);
  }, [load]);

  useEffect(() => {
    const target = sentinel.current;
    if (!target || !hasMore || loading) return;

    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) load(page + 1);
    });

    observer.observe(target);
    return () => observer.disconnect();
  }, [hasMore, loading, page, load]);

  const applyFilters = async (event: FormEvent) => {
    event.preventDefault();

    if (draft.from && draft.to && draft.from > draft.to) {
      setNotice("The from date must not be after the to date.");
      return;
    }

    setNotice(null);
    const search = draft.search.trim();

    if (!search) {
      setUserIds(null);
      setFilters({ ...draft, search: "" });
      return;
    }

    try {
      const result = await getUsers({ search, pageSize: MAX_FILTER_USERS });

      if (result.total > MAX_FILTER_USERS) {
        setNotice(`More than ${MAX_FILTER_USERS} users match "${search}". Showing records for the first ${MAX_FILTER_USERS}. Try a more specific name or email.`);
      }

      setUserIds(result.users.map((user) => user.id));
      setFilters({ ...draft, search });
    } catch (reason) {
      setNotice(errorMessage(reason));
    }
  };

  const clearFilters = () => {
    setNotice(null);
    setDraft(EMPTY_FILTERS);
    setUserIds(null);
    setFilters(EMPTY_FILTERS);
  };

  const changeSort = (sort: SpamReviewSort) => {
    setDraft((current) => ({ ...current, sort }));
    setFilters((current) => ({ ...current, sort }));
  };

  const filtered = filters.search !== "" || filters.from !== "" || filters.to !== "";
  const tabLabel = TABS.find((item) => item.value === tab)?.label.toLowerCase();

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold text-gray-900">Spam Review</h1>

      <div role="tablist" className="flex gap-2 border-b border-gray-200">
        {TABS.map((item) => (
          <button
            key={item.value}
            type="button"
            id={`spam-review-tab-${item.value}`}
            aria-selected={tab === item.value}
            onClick={() => setTab(item.value)}
            className={`-mb-px border-b-2 px-4 py-2 text-sm font-semibold ${
              tab === item.value
                ? "border-indigo-600 text-indigo-700"
                : "border-transparent text-gray-500 hover:text-gray-800"
            }`}
          >
            {item.label}
          </button>
        ))}
      </div>

      <form id="spam-review-filters" onSubmit={applyFilters} className="flex flex-wrap items-end gap-3 rounded-xl border bg-white p-4 shadow-sm">
        <label className="flex flex-col gap-1 text-xs font-medium text-gray-500">
          Sort by
          <select
            id="spam-review-sort"
            value={draft.sort}
            onChange={(event) => changeSort(event.target.value as SpamReviewSort)}
            className={INPUT_CLASS}
          >
            <option value="score">Score (highest first)</option>
            <option value="date">Date flagged (newest first)</option>
          </select>
        </label>
        <label className="flex min-w-56 flex-1 flex-col gap-1 text-xs font-medium text-gray-500">
          Flagged user (name or email)
          <input
            id="spam-review-search"
            value={draft.search}
            onChange={(event) => setDraft((current) => ({ ...current, search: event.target.value }))}
            placeholder="Search by name or email"
            className={INPUT_CLASS}
          />
        </label>
        <label className="flex flex-col gap-1 text-xs font-medium text-gray-500">
          Flagged from
          <input
            type="date"
            id="spam-review-from"
            onChange={(event) => setDraft((current) => ({ ...current, from: event.target.value }))}
            className={INPUT_CLASS}
          />
        </label>
        <label className="flex flex-col gap-1 text-xs font-medium text-gray-500">
          Flagged to
          <input
            type="date"
            id="spam-review-to"
            onChange={(event) => setDraft((current) => ({ ...current, to: event.target.value }))}
            className={INPUT_CLASS}
          />
        </label>
        <button
          id="spam-review-apply"
          className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white hover:bg-indigo-700"
        >
          Apply
        </button>
        <button id="spam-review-clear" type="button" onClick={clearFilters} className="rounded-lg border px-4 py-2 text-sm">
          Clear
        </button>
      </form>

      {notice && (
        <div id="spam-review-notice" className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          {notice}
        </div>
      )}

      {error && (
        <div id="spam-review-error" className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      {loading && records.length === 0 ? (
        <p id="spam-review-loading" className="text-sm text-gray-500">Loading Spam records…</p>
      ) : records.length === 0 && !error ? (
        <p id="spam-review-empty" className="rounded-lg border bg-white p-6 text-sm text-gray-500">
          {filtered ? "No Spam records match these filters." : `No ${tabLabel} Spam records.`}
        </p>
      ) : (
        <div className="overflow-x-auto rounded-xl border bg-white shadow-sm">
          <table id="spam-review-table" className="w-full table-fixed divide-y divide-gray-200 text-left text-sm">
            <thead className="bg-gray-50 text-xs font-medium uppercase text-gray-500">
              <tr>
                <th className="w-1/5 px-4 py-3">Flagged user</th>
                <th className="w-1/5 px-4 py-3 text-center">Score / No. of listings</th>
                <th className="w-1/5 px-4 py-3 text-center">Status</th>
                <th className="w-1/5 px-4 py-3">Date flagged</th>
                <th className="w-1/5 px-4 py-3 text-center">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {records.map((record) => (
                <tr key={record.id} id={`spam-record-${record.id}`} data-status={record.status}>
                  <td className="px-4 py-3">
                    <UserCell contact={contacts[record.userId]} />
                  </td>
                  <td className="px-4 py-3 text-center">
                    <span className="inline-block min-w-10 rounded-lg bg-purple-50 px-3 py-1 text-center text-lg font-bold text-purple-800 ring-1 ring-purple-200">
                      {record.scoreA}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-center">
                    <span className={`inline-block rounded-2xl px-3 py-1 text-xs font-bold ${STATUS_LABELS[record.status].style}`}>
                      {STATUS_LABELS[record.status].label}
                    </span>
                  </td>
                  <td className="px-4 py-3 text-gray-700">{new Date(record.flaggedAt).toLocaleString()}</td>
                  <td className="px-4 py-3 text-center">
                    <button
                      id={`spam-record-view-${record.id}`}
                      type="button"
                      disabled
                      className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white"
                    >
                      View record
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div ref={sentinel} id="spam-review-more" className="px-4 py-3 text-center text-sm text-gray-500">
            {loading && records.length > 0 ? "Loading more…" : null}
          </div>
        </div>
      )}
    </div>
  );
}
