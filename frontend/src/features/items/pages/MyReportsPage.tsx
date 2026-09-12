import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  MapPin,
  Calendar,
  CheckCircle2,
  Package,
  Plus,
  ArrowRight,
  Search,
  PackagePlus,
  ClipboardCheck,
  Link2,
  Handshake,
  TrendingUp,
  AlertTriangle,
} from "lucide-react";
import { AppHeader } from "../layout/AppHeader";
import { LOST_ITEM_CATEGORIES } from "../schemas/reportLostItemSchema";
import { FOUND_ITEM_CATEGORIES } from "../schemas/reportFoundItemSchema";
import { getMyLostItems, getMyFoundItems } from "../api/myReports";
import { resolveLostItem, type LostItemResponse } from "../api/reportLostItem";
import { resolveFoundItem, type FoundItemResponse } from "../api/reportFoundItem";
import { resolvePhotoUrl } from "../../../config/env";
import type { ApiError } from "../../../lib/apiClient";
import { ResolveConfirmModal } from "../components/ResolveConfirmModal";

type ReportRow = {
  id: string;
  kind: "lost" | "found";
  title: string;
  category: string;
  location: string;
  date: string;
  status: string;
  photoUrl?: string;
  // No API today returns a match count for an item (that lives in the
  // Matching Service, not built yet). Left optional and unset on purpose —
  // once that data exists, the "N Match" badge below will render itself.
  matchCount?: number;
};

type FilterKey = "all" | "lost" | "found" | "active" | "resolved";
type SortOrder = "newest" | "oldest";

const PAGE_SIZE = 6;

const FILTERS: { key: FilterKey; label: string }[] = [
  { key: "all", label: "All Reports" },
  { key: "lost", label: "Lost" },
  { key: "found", label: "Found" },
  { key: "active", label: "Active" },
  { key: "resolved", label: "Resolved" },
];

function formatDisplay(value: string): string {
  const d = new Date(value.length <= 10 ? `${value}T00:00:00` : value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

function toRow(item: LostItemResponse | FoundItemResponse, kind: "lost" | "found"): ReportRow {
  const categories = kind === "lost" ? LOST_ITEM_CATEGORIES : FOUND_ITEM_CATEGORIES;
  const categoryLabel = categories.find((c) => c.value === item.category)?.label ?? item.category;
  const isLost = kind === "lost";
  return {
    id: item.id,
    kind,
    title: item.title,
    category: categoryLabel,
    location: isLost ? (item as LostItemResponse).lastKnownLocation : (item as FoundItemResponse).locationFound,
    date: isLost ? (item as LostItemResponse).dateLost : (item as FoundItemResponse).dateFound,
    status: item.status,
    photoUrl: item.photoUrls[0],
  };
}

function statusPillClasses(status: string): string {
  const s = status.toLowerCase();
  if (s === "resolved") return "bg-emerald-50 text-emerald-700";
  if (s === "matched" || s === "possible_match") return "bg-amber-50 text-amber-700";
  return "bg-indigo-50 text-indigo-700";
}

// Turns a failed resolve call into a message the user can act on.
function describeResolveError(err: unknown): string {
  const apiErr = err as Partial<ApiError>;
  if (apiErr?.status === 403) return "You're not allowed to resolve this report.";
  if (apiErr?.status === 409) return "This item was already resolved.";
  if (apiErr?.status === 404) return "This report couldn't be found. It may have been removed.";
  return "We couldn't resolve this report right now. Please try again.";
}

export function MyReportsPage() {
  const navigate = useNavigate();
  const [rows, setRows] = useState<ReportRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [filter, setFilter] = useState<FilterKey>("all");
  const [sortOrder, setSortOrder] = useState<SortOrder>("newest");
  const [page, setPage] = useState(1);

  const [resolveTarget, setResolveTarget] = useState<ReportRow | null>(null);
  const [isResolving, setIsResolving] = useState(false);
  const [resolveError, setResolveError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    async function load() {
      try {
        const [lost, found] = await Promise.all([
          getMyLostItems(controller.signal),
          getMyFoundItems(controller.signal),
        ]);
        const combined = [
          ...lost.map((i) => toRow(i, "lost")),
          ...found.map((i) => toRow(i, "found")),
        ].sort((a, b) => b.date.localeCompare(a.date));
        setRows(combined);
      } catch (err) {
        if (controller.signal.aborted) return;
        setError("We couldn't load your reports. Please try again.");
        setRows([]);
      }
    }

    load();
    return () => controller.abort();
  }, []);

  const activeCount = rows?.filter((r) => r.status.toLowerCase() === "active").length ?? 0;
  const possibleMatchCount = rows?.filter((r) => r.status.toLowerCase().includes("match")).length ?? 0;
  const resolvedCount = rows?.filter((r) => r.status.toLowerCase() === "resolved").length ?? 0;

  const filteredSortedRows = useMemo(() => {
    if (!rows) return [];
    const filtered = rows.filter((r) => {
      if (filter === "all") return true;
      if (filter === "lost") return r.kind === "lost";
      if (filter === "found") return r.kind === "found";
      if (filter === "active") return r.status.toLowerCase() === "active";
      return r.status.toLowerCase() === "resolved";
    });
    const sorted = [...filtered].sort((a, b) =>
      sortOrder === "newest" ? b.date.localeCompare(a.date) : a.date.localeCompare(b.date),
    );
    return sorted;
  }, [rows, filter, sortOrder]);

  const totalPages = Math.max(1, Math.ceil(filteredSortedRows.length / PAGE_SIZE));
  const clampedPage = Math.min(page, totalPages);
  const pageStart = (clampedPage - 1) * PAGE_SIZE;
  const visibleRows = filteredSortedRows.slice(pageStart, pageStart + PAGE_SIZE);

  function changeFilter(key: FilterKey) {
    setFilter(key);
    setPage(1);
  }

  function changeSortOrder(order: SortOrder) {
    setSortOrder(order);
    setPage(1);
  }

  async function confirmResolve() {
    if (!resolveTarget) return;
    setIsResolving(true);
    setResolveError(null);
    try {
      const updated =
        resolveTarget.kind === "lost"
          ? await resolveLostItem(resolveTarget.id)
          : await resolveFoundItem(resolveTarget.id);
      setRows((prev) =>
        prev
          ? prev.map((r) =>
              r.id === resolveTarget.id && r.kind === resolveTarget.kind ? { ...r, status: updated.status } : r,
            )
          : prev,
      );
      setResolveTarget(null);
    } catch (err) {
      setResolveError(describeResolveError(err));
    } finally {
      setIsResolving(false);
    }
  }

  function closeResolveModal() {
    if (isResolving) return;
    setResolveTarget(null);
    setResolveError(null);
  }

  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />

      <main className="mx-auto max-w-[1600px] px-6 py-10 lg:px-10">
        <div className="mb-8 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h1 className="text-3xl font-extrabold tracking-tight text-gray-900">My Reports</h1>
            <p className="mt-1 text-[15px] text-gray-500">
              Track the status of your lost and found item reports.
            </p>
          </div>
          <button
            type="button"
            onClick={() => navigate("/report-lost-item")}
            className="flex items-center justify-center gap-2 rounded-xl bg-gradient-to-r from-indigo-700 to-indigo-900 px-5 py-3 text-sm font-semibold text-white shadow-sm transition-opacity hover:opacity-95"
          >
            <Plus className="h-4 w-4" />
            Report Lost Item
          </button>
        </div>

        {/* Quick action cards */}
        <div className="mb-8 grid grid-cols-1 gap-4 sm:grid-cols-2">
          <button
            type="button"
            onClick={() => navigate("/report-lost-item")}
            className="flex items-start gap-4 rounded-2xl border-2 border-gray-300 bg-white p-6 text-left shadow-sm transition-colors hover:border-indigo-600 hover:shadow-md"
          >
            <span className="flex h-12 w-12 flex-shrink-0 items-center justify-center rounded-full bg-indigo-50">
              <Search className="h-5 w-5 text-indigo-600" />
            </span>
            <div>
              <p className="text-lg font-bold text-gray-900">Report Lost Item</p>
              <p className="mt-1 text-sm text-gray-500">Tell us what you lost and where you last saw it.</p>
              <span className="mt-3 flex items-center gap-1.5 text-sm font-semibold text-indigo-600">
                Create Report
                <ArrowRight className="h-4 w-4" />
              </span>
            </div>
          </button>

          <button
            type="button"
            onClick={() => navigate("/report-found-item")}
            className="flex items-start gap-4 rounded-2xl border-2 border-gray-300 bg-white p-6 text-left shadow-sm transition-colors hover:border-indigo-600 hover:shadow-md"
          >
            <span className="flex h-12 w-12 flex-shrink-0 items-center justify-center rounded-full bg-indigo-50">
              <PackagePlus className="h-5 w-5 text-indigo-600" />
            </span>
            <div>
              <p className="text-lg font-bold text-gray-900">Report Found Item</p>
              <p className="mt-1 text-sm text-gray-500">Let others know you found something and where.</p>
              <span className="mt-3 flex items-center gap-1.5 text-sm font-semibold text-indigo-600">
                Create Report
                <ArrowRight className="h-4 w-4" />
              </span>
            </div>
          </button>
        </div>

        {/* Stat cards */}
        <div className="mb-8 grid grid-cols-1 gap-4 sm:grid-cols-3">
          <div className="flex items-center justify-between rounded-2xl border border-gray-100 bg-white p-5 shadow-sm">
            <div className="flex items-center gap-4">
              <span className="flex h-11 w-11 items-center justify-center rounded-full bg-gray-100">
                <ClipboardCheck className="h-5 w-5 text-gray-500" />
              </span>
              <div>
                <p className="text-sm text-gray-500">Active Reports</p>
                <p className="text-2xl font-extrabold text-gray-900">{activeCount}</p>
              </div>
            </div>
            <TrendingUp className="h-5 w-5 text-emerald-500" />
          </div>

          <div className="flex items-center justify-between rounded-2xl border border-gray-100 bg-white p-5 shadow-sm">
            <div className="flex items-center gap-4">
              <span className="flex h-11 w-11 items-center justify-center rounded-full bg-gray-100">
                <Link2 className="h-5 w-5 text-gray-500" />
              </span>
              <div>
                <p className="text-sm text-gray-500">Possible Matches</p>
                <p className="text-2xl font-extrabold text-gray-900">{possibleMatchCount}</p>
              </div>
            </div>
            <AlertTriangle className="h-5 w-5 text-amber-500" />
          </div>

          <div className="flex items-center justify-between rounded-2xl border border-gray-100 bg-white p-5 shadow-sm">
            <div className="flex items-center gap-4">
              <span className="flex h-11 w-11 items-center justify-center rounded-full bg-gray-100">
                <Handshake className="h-5 w-5 text-gray-500" />
              </span>
              <div>
                <p className="text-sm text-gray-500">Resolved</p>
                <p className="text-2xl font-extrabold text-gray-900">{resolvedCount}</p>
              </div>
            </div>
            <CheckCircle2 className="h-5 w-5 text-emerald-500" />
          </div>
        </div>

        {/* Filter tabs + sort */}
        <div className="mb-6 flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="inline-flex flex-wrap items-center gap-1 rounded-full border border-gray-200 bg-white p-1">
            {FILTERS.map((f) => (
              <button
                key={f.key}
                type="button"
                onClick={() => changeFilter(f.key)}
                className={`rounded-full px-4 py-2 text-sm font-medium transition-colors ${
                  filter === f.key ? "bg-indigo-700 text-white" : "text-gray-500 hover:text-gray-700"
                }`}
              >
                [ {f.label} ]
              </button>
            ))}
          </div>

          <div className="relative inline-block">
            <select
              value={sortOrder}
              onChange={(e) => changeSortOrder(e.target.value as SortOrder)}
              className="appearance-none rounded-lg border border-gray-200 bg-white py-2 pl-4 pr-9 text-sm font-medium text-gray-700 focus:outline-none focus:ring-1 focus:ring-indigo-500"
            >
              <option value="newest">Newest first</option>
              <option value="oldest">Oldest first</option>
            </select>
            <ChevronDown className="pointer-events-none absolute right-3 top-1/2 h-4 w-4 -translate-y-1/2 text-gray-400" />
          </div>
        </div>

        {/* Report grid */}
        {rows === null && (
          <div className="rounded-2xl border border-gray-100 bg-white p-10 text-center text-sm text-gray-400 shadow-sm">
            Loading your reports…
          </div>
        )}

        {rows !== null && error && (
          <div className="rounded-2xl border border-red-100 bg-red-50 p-6 text-center text-sm text-red-600">
            {error}
          </div>
        )}

        {rows !== null && !error && filteredSortedRows.length === 0 && (
          <div className="flex flex-col items-center justify-center rounded-2xl border border-gray-100 bg-white p-14 text-center shadow-sm">
            <span className="mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-indigo-50">
              <Package className="h-6 w-6 text-indigo-600" />
            </span>
            <p className="font-semibold text-gray-900">No reports found</p>
            <p className="mt-1 text-sm text-gray-500">
              {rows.length === 0
                ? "When you report a lost or found item, it will show up here."
                : "Try a different filter to see more of your reports."}
            </p>
          </div>
        )}

        {rows !== null && !error && filteredSortedRows.length > 0 && (
          <>
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2 lg:grid-cols-3">
              {visibleRows.map((row) => {
                const isResolved = row.status.toLowerCase() === "resolved";
                return (
                  <div
                    key={`${row.kind}-${row.id}`}
                    className="flex flex-col rounded-2xl border border-gray-100 bg-white p-4 shadow-sm"
                  >
                    <div className="flex gap-4">
                      {row.photoUrl ? (
                        <img
                          src={resolvePhotoUrl(row.photoUrl)}
                          alt=""
                          className="h-28 w-28 flex-shrink-0 rounded-2xl object-cover"
                        />
                      ) : (
                        <div className="flex h-28 w-28 flex-shrink-0 items-center justify-center rounded-2xl bg-gradient-to-br from-indigo-200 to-indigo-100">
                          <Package className="h-8 w-8 text-indigo-400" />
                        </div>
                      )}

                      <div className="min-w-0 flex-1">
                        <span
                          className={`inline-block rounded-full px-3 py-1 text-xs font-bold text-white ${
                            row.kind === "lost" ? "bg-indigo-900" : "bg-teal-600"
                          }`}
                        >
                          {row.kind === "lost" ? "LOST" : "FOUND"}
                        </span>
                        <p className="mt-2 truncate text-lg font-bold text-gray-900">{row.title}</p>
                        <p className="text-sm text-gray-500">{row.category}</p>
                        <p className="mt-1.5 flex items-center gap-1.5 text-sm text-gray-500">
                          <MapPin className="h-3.5 w-3.5 flex-shrink-0" />
                          <span className="truncate">{row.location}</span>
                        </p>
                        <p className="mt-1 flex items-center gap-1.5 text-sm text-gray-500">
                          <Calendar className="h-3.5 w-3.5 flex-shrink-0" />
                          {formatDisplay(row.date)}
                        </p>
                      </div>
                    </div>

                    <div className="mt-3 flex flex-wrap items-center gap-2">
                      <span
                        className={`flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-bold ${statusPillClasses(row.status)}`}
                      >
                        {row.status}
                      </span>
                      {typeof row.matchCount === "number" && row.matchCount > 0 && (
                        <span className="rounded-full bg-amber-400 px-3 py-1 text-xs font-bold text-white">
                          {row.matchCount} Match{row.matchCount > 1 ? "es" : ""}
                        </span>
                      )}
                    </div>

                    {isResolved ? (
                      <div className="mt-4 flex items-center justify-center gap-2 border-t border-gray-100 pt-4 text-sm font-semibold text-emerald-600">
                        <CheckCircle2 className="h-4 w-4" />
                        Resolved
                      </div>
                    ) : (
                      <button
                        type="button"
                        onClick={() => setResolveTarget(row)}
                        className="mt-4 w-full rounded-xl border-2 border-indigo-700 py-2.5 text-sm font-semibold text-indigo-700 transition-colors hover:bg-indigo-50"
                      >
                        Mark as Resolved
                      </button>
                    )}
                  </div>
                );
              })}
            </div>

            <div className="mt-8 flex flex-col items-center justify-between gap-4 sm:flex-row">
              <p className="text-sm text-gray-500">
                Showing {pageStart + 1}–{Math.min(pageStart + PAGE_SIZE, filteredSortedRows.length)} of{" "}
                {filteredSortedRows.length} reports
              </p>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.max(1, p - 1))}
                  disabled={clampedPage === 1}
                  aria-label="Previous page"
                  className="flex h-9 w-9 items-center justify-center rounded-full text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600 disabled:opacity-40 disabled:hover:bg-transparent"
                >
                  <ChevronLeft className="h-4 w-4" />
                </button>
                {Array.from({ length: totalPages }, (_, i) => i + 1).map((n) => (
                  <button
                    key={n}
                    type="button"
                    onClick={() => setPage(n)}
                    className={`flex h-9 w-9 items-center justify-center rounded-full text-sm font-semibold transition-colors ${
                      n === clampedPage ? "bg-indigo-100 text-indigo-700" : "text-gray-500 hover:bg-gray-100"
                    }`}
                  >
                    {n}
                  </button>
                ))}
                <button
                  type="button"
                  onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                  disabled={clampedPage === totalPages}
                  aria-label="Next page"
                  className="flex h-9 w-9 items-center justify-center rounded-full text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600 disabled:opacity-40 disabled:hover:bg-transparent"
                >
                  <ChevronRight className="h-4 w-4" />
                </button>
              </div>
            </div>
          </>
        )}
      </main>

      {resolveTarget && (
        <>
          <ResolveConfirmModal
            item={resolveTarget}
            isSubmitting={isResolving}
            onConfirm={confirmResolve}
            onCancel={closeResolveModal}
          />
          {resolveError && (
            <div
              className="fixed inset-x-0 bottom-6 z-[60] mx-auto w-fit rounded-xl bg-red-600 px-5 py-3 text-sm font-medium text-white shadow-lg"
              role="alert"
            >
              {resolveError}
            </div>
          )}
        </>
      )}
    </div>
  );
}