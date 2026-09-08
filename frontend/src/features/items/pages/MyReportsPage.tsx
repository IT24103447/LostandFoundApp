import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ClipboardCheck,
  Link2,
  Handshake,
  TrendingUp,
  AlertTriangle,
  CheckCircle2,
  MoreHorizontal,
  Package,
  PackagePlus,
  Search,
  Plus,
  ArrowRight,
} from "lucide-react";
import { AppHeader } from "../layout/AppHeader";
import { LOST_ITEM_CATEGORIES } from "../schemas/reportLostItemSchema";
import { FOUND_ITEM_CATEGORIES } from "../schemas/reportFoundItemSchema";
import { getMyLostItems, getMyFoundItems } from "../api/myReports";
import type { LostItemResponse } from "../api/reportLostItem";
import type { FoundItemResponse } from "../api/reportFoundItem";
import { resolvePhotoUrl } from "../../../config/env";

type ReportRow = {
  id: string;
  kind: "lost" | "found";
  title: string;
  category: string;
  location: string;
  date: string;
  status: string;
  photoUrl?: string;
};

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

export function MyReportsPage() {
  const navigate = useNavigate();
  const [rows, setRows] = useState<ReportRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);

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

        {/* Report list */}
        <h2 className="mb-4 text-lg font-bold text-gray-900">Your Reports</h2>

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

        {rows !== null && !error && rows.length === 0 && (
          <div className="flex flex-col items-center justify-center rounded-2xl border border-gray-100 bg-white p-14 text-center shadow-sm">
            <span className="mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-indigo-50">
              <Package className="h-6 w-6 text-indigo-600" />
            </span>
            <p className="font-semibold text-gray-900">No reports yet</p>
            <p className="mt-1 text-sm text-gray-500">
              When you report a lost or found item, it will show up here.
            </p>
          </div>
        )}

        {rows !== null && !error && rows.length > 0 && (
          <div className="flex flex-col gap-3">
            {rows.map((row) => (
              <div
                key={`${row.kind}-${row.id}`}
                className="flex flex-col gap-3 rounded-2xl border border-gray-100 bg-white p-4 shadow-sm sm:flex-row sm:items-center sm:gap-6"
              >
                <div className="flex items-center gap-4 sm:flex-1">
                  {row.photoUrl ? (
                    <img
                      src={resolvePhotoUrl(row.photoUrl)}
                      alt=""
                      className="h-14 w-14 flex-shrink-0 rounded-xl object-cover"
                    />
                  ) : (
                    <span className="flex h-14 w-14 flex-shrink-0 items-center justify-center rounded-xl bg-gray-100">
                      <Package className="h-6 w-6 text-gray-400" />
                    </span>
                  )}
                  <div>
                    <p className="font-semibold text-gray-900">{row.title}</p>
                    <span className="text-xs font-medium uppercase tracking-wide text-gray-400">
                      {row.kind === "lost" ? "Lost Item" : "Found Item"}
                    </span>
                  </div>
                </div>

                <div className="grid flex-1 grid-cols-3 gap-4 text-sm sm:max-w-md">
                  <div>
                    <p className="text-xs text-gray-400">Category</p>
                    <p className="font-medium text-gray-800">{row.category}</p>
                  </div>
                  <div>
                    <p className="text-xs text-gray-400">Location</p>
                    <p className="font-medium text-gray-800">{row.location}</p>
                  </div>
                  <div>
                    <p className="text-xs text-gray-400">Date</p>
                    <p className="font-medium text-gray-800">{formatDisplay(row.date)}</p>
                  </div>
                </div>

                <div className="flex items-center gap-3">
                  <span
                    className={`flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-bold ${statusPillClasses(row.status)}`}
                  >
                    <span className="h-1.5 w-1.5 rounded-full bg-current" />
                    {row.status}
                  </span>
                  <button
                    type="button"
                    aria-label="More options"
                    className="flex h-8 w-8 items-center justify-center rounded-full text-gray-400 transition-colors hover:bg-gray-100 hover:text-gray-600"
                  >
                    <MoreHorizontal className="h-4 w-4" />
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </main>
    </div>
  );
}