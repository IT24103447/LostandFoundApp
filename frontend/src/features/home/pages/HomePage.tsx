import { useEffect, useState } from "react";
import { Search, ChevronLeft, ChevronRight, SearchX } from "lucide-react";
import { AppHeader } from "../../items/layout/AppHeader"; // ⚠️ verify this path in your repo
import { LOST_ITEM_CATEGORIES } from "../../items/schemas/reportLostItemSchema";
import { browseItems, type ItemSummary, type ItemType } from "../../items/api/browseItems";
import type { ApiError } from "../../../lib/apiClient";
import { ItemCard } from "../components/ItemCard";

const GENERIC_LOAD_ERROR = "We couldn't load items right now. Please try again.";

// Mirrors the shape ASP.NET Core's ValidationProblem() sends back:
// { errors: { "DateRange": ["DateFrom must not be after DateTo."], ... } }
function isValidationErrorBody(body: unknown): body is { errors: Record<string, string[]> } {
  return !!body && typeof body === "object" && "errors" in (body as Record<string, unknown>);
}

// Turns a failed browseItems() call into a message the user can act on.
// Falls back to a generic message for anything that isn't a 400 with field errors.
function describeLoadError(err: unknown): string {
  const apiErr = err as Partial<ApiError>;
  if (apiErr?.status === 400 && isValidationErrorBody(apiErr.body)) {
    const messages = Object.values(apiErr.body.errors).flat();
    if (messages.length > 0) return messages.join(" ");
  }
  return GENERIC_LOAD_ERROR;
}

type AppliedFilters = {
  q: string;
  category: string; // "" = all
  type: ItemType | "ALL";
  dateFrom: string; // "" = unset
  dateTo: string;
};

const EMPTY_FILTERS: AppliedFilters = { q: "", category: "", type: "ALL", dateFrom: "", dateTo: "" };
const PAGE_SIZE = 12;

function formatChipDate(from: string, to: string): string | null {
  if (!from && !to) return null;
  const fmt = (v: string) => {
    const d = new Date(`${v}T00:00:00`);
    return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
  };
  if (from && to) return `${fmt(from)} – ${fmt(to)}`;
  if (from) return `From ${fmt(from)}`;
  return `Until ${fmt(to)}`;
}

export function HomePage() {
  // Draft state bound to the form controls.
  const [draftQ, setDraftQ] = useState("");
  const [draftCategory, setDraftCategory] = useState("");
  const [draftDateFrom, setDraftDateFrom] = useState("");
  const [draftDateTo, setDraftDateTo] = useState("");

  // Applied state: what's actually been searched for.
  const [applied, setApplied] = useState<AppliedFilters>(EMPTY_FILTERS);
  const [page, setPage] = useState(1);

  const [result, setResult] = useState<{ items: ItemSummary[]; totalCount: number; totalPages: number } | null>(
    null,
  );
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    browseItems(
      {
        q: applied.q || undefined,
        category: applied.category || undefined,
        type: applied.type === "ALL" ? undefined : applied.type,
        dateFrom: applied.dateFrom || undefined,
        dateTo: applied.dateTo || undefined,
        page,
        pageSize: PAGE_SIZE,
      },
      controller.signal,
    )
      .then((res) => {
        setResult({ items: res.items, totalCount: res.totalCount, totalPages: res.totalPages });
      })
      .catch((err) => {
        if (controller.signal.aborted) return;
        setError(describeLoadError(err));
        setResult({ items: [], totalCount: 0, totalPages: 0 });
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [applied, page]);

  function runSearch() {
    setApplied((prev) => ({ ...prev, q: draftQ.trim() }));
    setPage(1);
  }

  function applyFilters() {
    setApplied((prev) => ({
      ...prev,
      category: draftCategory,
      dateFrom: draftDateFrom,
      dateTo: draftDateTo,
    }));
    setPage(1);
  }

  function setType(type: ItemType | "ALL") {
    setApplied((prev) => ({ ...prev, type }));
    setPage(1);
  }

  function clearAll() {
    setDraftQ("");
    setDraftCategory("");
    setDraftDateFrom("");
    setDraftDateTo("");
    setApplied(EMPTY_FILTERS);
    setPage(1);
  }

  function removeChip(key: "type" | "category" | "date") {
    if (key === "type") setApplied((prev) => ({ ...prev, type: "ALL" }));
    if (key === "category") {
      setDraftCategory("");
      setApplied((prev) => ({ ...prev, category: "" }));
    }
    if (key === "date") {
      setDraftDateFrom("");
      setDraftDateTo("");
      setApplied((prev) => ({ ...prev, dateFrom: "", dateTo: "" }));
    }
    setPage(1);
  }

  const chips: { key: "type" | "category" | "date"; label: string }[] = [];
  if (applied.type !== "ALL") chips.push({ key: "type", label: applied.type === "LOST" ? "Lost" : "Found" });
  if (applied.category) {
    const label = LOST_ITEM_CATEGORIES.find((c) => c.value === applied.category)?.label ?? applied.category;
    chips.push({ key: "category", label });
  }
  const dateChip = formatChipDate(applied.dateFrom, applied.dateTo);
  if (dateChip) chips.push({ key: "date", label: dateChip });

  const hasAnyFilter =
    !!applied.q || applied.type !== "ALL" || !!applied.category || !!applied.dateFrom || !!applied.dateTo;

  const totalCount = result?.totalCount ?? 0;
  const totalPages = result?.totalPages ?? 0;
  const rangeStart = totalCount === 0 ? 0 : (page - 1) * PAGE_SIZE + 1;
  const rangeEnd = Math.min(page * PAGE_SIZE, totalCount);

  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />

      <main className="mx-auto max-w-[1600px] px-6 py-10 lg:px-10">
        <h1 className="text-4xl font-extrabold tracking-tight text-gray-900">Find an Item</h1>
        <p className="mt-2 text-gray-500">Search lost and found reports and use filters to narrow down the results.</p>

        {/* Search bar */}
        <div className="mt-6 flex items-center gap-3 rounded-2xl border border-gray-200 bg-white p-2 pl-4 shadow-sm">
          <Search className="h-5 w-5 shrink-0 text-gray-400" />
          <input
            type="text"
            value={draftQ}
            onChange={(e) => setDraftQ(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") runSearch();
            }}
            placeholder="Search by item name, description or location..."
            className="min-w-0 flex-1 border-0 bg-transparent text-[15px] text-gray-900 placeholder:text-gray-400 focus:outline-none focus:ring-0"
          />
          <button
            type="button"
            onClick={runSearch}
            className="shrink-0 rounded-xl bg-indigo-600 px-6 py-3 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700"
          >
            Search
          </button>
        </div>

        <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-[280px_1fr]">
          {/* Filters sidebar */}
          <aside className="h-fit rounded-2xl border border-gray-200 bg-white p-5 shadow-sm">
            <div className="rounded-xl bg-indigo-600 px-4 py-2.5 text-center text-sm font-bold text-white">
              Filter Items
            </div>

            <div className="mt-5">
              <label className="text-sm font-semibold text-gray-800">Item Type</label>
              <div className="mt-2 grid grid-cols-3 gap-1.5 rounded-xl bg-gray-100 p-1">
                {(["ALL", "LOST", "FOUND"] as const).map((t) => (
                  <button
                    key={t}
                    type="button"
                    onClick={() => setType(t)}
                    className={`rounded-lg py-1.5 text-sm font-medium transition-colors ${
                      applied.type === t ? "bg-indigo-600 text-white shadow-sm" : "text-gray-600 hover:text-gray-900"
                    }`}
                  >
                    {t === "ALL" ? "All" : t === "LOST" ? "Lost" : "Found"}
                  </button>
                ))}
              </div>
            </div>

            <div className="mt-5">
              <label htmlFor="category-filter" className="text-sm font-semibold text-gray-800">
                Category
              </label>
              <select
                id="category-filter"
                value={draftCategory}
                onChange={(e) => setDraftCategory(e.target.value)}
                className="mt-2 w-full appearance-none rounded-xl border border-gray-300 bg-white px-3.5 py-2.5 text-[15px] text-gray-900 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
              >
                <option value="">All Categories</option>
                {LOST_ITEM_CATEGORIES.map((c) => (
                  <option key={c.value} value={c.value}>
                    {c.label}
                  </option>
                ))}
              </select>
            </div>

            <div className="mt-5">
              <label className="text-sm font-semibold text-gray-800">Date Range</label>
              <div className="mt-2 space-y-2">
                <input
                  type="date"
                  value={draftDateFrom}
                  onChange={(e) => setDraftDateFrom(e.target.value)}
                  className="w-full rounded-xl border border-gray-300 px-3.5 py-2.5 text-sm text-gray-900 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                />
                <input
                  type="date"
                  value={draftDateTo}
                  onChange={(e) => setDraftDateTo(e.target.value)}
                  min={draftDateFrom || undefined}
                  className="w-full rounded-xl border border-gray-300 px-3.5 py-2.5 text-sm text-gray-900 focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                />
              </div>
            </div>

            <button
              type="button"
              onClick={applyFilters}
              className="mt-5 w-full rounded-xl bg-indigo-600 py-2.5 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700"
            >
              Apply Filters
            </button>

            <button
              type="button"
              onClick={clearAll}
              className="mt-3 w-full text-center text-sm font-medium text-indigo-600 hover:text-indigo-700"
            >
              Clear all
            </button>
          </aside>

          {/* Results */}
          <section>
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="text-lg font-bold text-gray-900">
                {loading ? "Loading…" : `${totalCount} Active Item${totalCount === 1 ? "" : "s"}`}
              </h2>
              <span className="rounded-lg border border-gray-200 bg-white px-3 py-2 text-sm text-gray-500">
                Newest first
              </span>
            </div>

            {chips.length > 0 && (
              <div className="mt-4 flex flex-wrap gap-2">
                {chips.map((chip) => (
                  <span
                    key={chip.key}
                    className="flex items-center gap-1.5 rounded-full bg-indigo-100 px-3 py-1.5 text-sm font-medium text-indigo-700"
                  >
                    {chip.label}
                    <button
                      type="button"
                      onClick={() => removeChip(chip.key)}
                      aria-label={`Remove ${chip.label} filter`}
                      className="text-indigo-500 hover:text-indigo-800"
                    >
                      ×
                    </button>
                  </span>
                ))}
              </div>
            )}

            {error && (
              <div className="mt-6 rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
                {error}
              </div>
            )}

            {loading ? (
              <div className="mt-6 grid grid-cols-1 gap-5 sm:grid-cols-2 xl:grid-cols-3">
                {Array.from({ length: 6 }).map((_, i) => (
                  <div key={i} className="animate-pulse rounded-2xl border border-gray-200 bg-white">
                    <div className="aspect-[4/3] w-full rounded-t-2xl bg-gray-200" />
                    <div className="space-y-2 p-4">
                      <div className="h-4 w-2/3 rounded bg-gray-200" />
                      <div className="h-3 w-1/3 rounded bg-gray-200" />
                      <div className="h-3 w-full rounded bg-gray-200" />
                    </div>
                  </div>
                ))}
              </div>
            ) : result && result.items.length === 0 ? (
              <div className="mt-6 flex flex-col items-center rounded-2xl border border-gray-200 bg-white px-6 py-16 text-center shadow-sm">
                <SearchX className="h-16 w-16 text-indigo-200" strokeWidth={1.5} />
                <h3 className="mt-5 text-2xl font-extrabold text-gray-900">No matching items</h3>
                <p className="mt-2 max-w-sm text-gray-500">
                  We couldn't find any active items matching your search and filters.
                </p>
                {hasAnyFilter && <p className="mt-1 text-sm text-gray-400">Try a different keyword or remove one of your filters.</p>}
                <button
                  type="button"
                  onClick={clearAll}
                  className="mt-6 rounded-xl bg-indigo-600 px-6 py-2.5 text-sm font-semibold text-white shadow-sm hover:bg-indigo-700"
                >
                  Clear Filters
                </button>
              </div>
            ) : (
              <>
                <div className="mt-6 grid grid-cols-1 gap-5 sm:grid-cols-2 xl:grid-cols-3">
                  {result?.items.map((item) => (
                    <ItemCard key={item.id} item={item} />
                  ))}
                </div>

                <div className="mt-8 flex flex-wrap items-center justify-between gap-3">
                  <p className="text-sm text-gray-500">
                    Showing {rangeStart}–{rangeEnd} of {totalCount} items
                  </p>
                  {totalPages > 1 && (
                    <div className="flex items-center gap-1.5">
                      <button
                        type="button"
                        disabled={page <= 1}
                        onClick={() => setPage((p) => Math.max(1, p - 1))}
                        className="flex h-9 w-9 items-center justify-center rounded-lg border border-gray-200 text-gray-500 disabled:opacity-40 hover:bg-gray-50"
                      >
                        <ChevronLeft className="h-4 w-4" />
                      </button>
                      {Array.from({ length: totalPages }, (_, i) => i + 1)
                        .filter((p) => p === 1 || p === totalPages || Math.abs(p - page) <= 1)
                        .map((p, idx, arr) => (
                          <div key={p} className="flex items-center gap-1.5">
                            {idx > 0 && arr[idx - 1] !== p - 1 && <span className="px-1 text-gray-400">…</span>}
                            <button
                              type="button"
                              onClick={() => setPage(p)}
                              className={`flex h-9 w-9 items-center justify-center rounded-lg text-sm font-medium ${
                                p === page ? "bg-indigo-600 text-white" : "border border-gray-200 text-gray-600 hover:bg-gray-50"
                              }`}
                            >
                              {p}
                            </button>
                          </div>
                        ))}
                      <button
                        type="button"
                        disabled={page >= totalPages}
                        onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                        className="flex h-9 w-9 items-center justify-center rounded-lg border border-gray-200 text-gray-500 disabled:opacity-40 hover:bg-gray-50"
                      >
                        <ChevronRight className="h-4 w-4" />
                      </button>
                    </div>
                  )}
                </div>
              </>
            )}

            <p className="mt-8 text-center text-xs text-gray-400">Only active lost and found reports are shown.</p>
          </section>
        </div>
      </main>
    </div>
  );
}