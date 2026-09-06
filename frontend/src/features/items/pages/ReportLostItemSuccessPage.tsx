import { useEffect } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { ShieldCheck, ArrowLeft, Lock } from "lucide-react";
import { AppHeader } from "../layout/AppHeader";
import { SuccessCheckIllustration } from "../components/Illustrations";
import { LOST_ITEM_CATEGORIES } from "../schemas/reportLostItemSchema";
import type { LostItemResponse } from "../api/reportLostItem";

function formatDisplay(value: string): string {
  const d = new Date(value.length <= 10 ? `${value}T00:00:00` : value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
}

export function ReportLostItemSuccessPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const report = (location.state as { report?: LostItemResponse } | null)?.report;

  useEffect(() => {
    if (!report) {
      navigate("/report-lost-item", { replace: true });
    }
  }, [report, navigate]);

  if (!report) return null;

  const categoryLabel = LOST_ITEM_CATEGORIES.find((c) => c.value === report.category)?.label ?? report.category;

  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />
      <main className="mx-auto flex max-w-2xl flex-col items-center px-6 py-14 text-center">
        <SuccessCheckIllustration />

        <h1 className="mt-4 text-3xl font-extrabold tracking-tight text-gray-900">
          Lost Item Reported Successfully
        </h1>
        <p className="mt-3 text-[15px] leading-relaxed text-gray-500">
          Your report has been submitted and is now active.
          <br />
          We'll keep your report visible while our matching system looks for potential matches.
        </p>

        <div className="mt-8 w-full rounded-2xl border border-indigo-100 bg-gradient-to-b from-indigo-50/60 to-white p-6 text-left shadow-sm">
          <div className="mb-4 flex items-center justify-between">
            <h2 className="text-lg font-bold text-gray-900">Report Summary</h2>
            <span className="flex items-center gap-1.5 rounded-full bg-indigo-600 px-3 py-1 text-xs font-bold text-white">
              <span className="h-1.5 w-1.5 rounded-full bg-white" />
              {report.status}
            </span>
          </div>

          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
            <div>
              <p className="text-xs font-medium uppercase tracking-wide text-gray-400">Item</p>
              <p className="mt-0.5 font-semibold text-gray-900">{report.title}</p>
            </div>
            <div>
              <p className="text-xs font-medium uppercase tracking-wide text-gray-400">Category</p>
              <p className="mt-0.5 font-semibold text-gray-900">{categoryLabel}</p>
            </div>
            <div>
              <p className="text-xs font-medium uppercase tracking-wide text-gray-400">Date Lost</p>
              <p className="mt-0.5 font-semibold text-gray-900">{formatDisplay(report.dateLost)}</p>
            </div>
            <div>
              <p className="text-xs font-medium uppercase tracking-wide text-gray-400">Last Known Location</p>
              <p className="mt-0.5 font-semibold text-gray-900">{report.lastKnownLocation}</p>
            </div>
          </div>

          <hr className="my-4 border-gray-100" />

          <div className="flex items-center justify-between">
            <div>
              <p className="text-xs font-medium uppercase tracking-wide text-gray-400">
                Private verification detail
              </p>
              <p className="mt-0.5 flex items-center gap-1.5 text-sm text-gray-700">
                <Lock className="h-3.5 w-3.5" />
                Hidden
              </p>
            </div>
            {report.photoUrls.length > 0 && (
              <div className="flex -space-x-2">
                {report.photoUrls.slice(0, 3).map((url, i) => (
                  <img
                    key={i}
                    src={url}
                    alt=""
                    className="h-12 w-12 rounded-lg border-2 border-white object-cover shadow-sm"
                  />
                ))}
              </div>
            )}
          </div>
        </div>

        <div className="mt-8 w-full text-left">
          <h3 className="mb-4 text-sm font-bold text-gray-900">What's next?</h3>
          <div className="flex items-center justify-between text-sm text-gray-500">
            <span className="flex items-center gap-1.5 text-gray-800">
              <span className="flex h-4 w-4 items-center justify-center rounded-full bg-indigo-600 text-[10px] text-white">
                ✓
              </span>
              Your report has been created
            </span>
            <span className="mx-2 h-px flex-1 bg-gray-200" />
            <span className="flex items-center gap-1.5 text-gray-800">
              <span className="flex h-4 w-4 items-center justify-center rounded-full bg-indigo-600 text-[10px] text-white">
                ✓
              </span>
              Your item is now active
            </span>
            <span className="mx-2 h-px flex-1 bg-gray-200" />
            <span className="flex items-center gap-1.5">
              <span className="h-4 w-4 rounded-full border-2 border-gray-300" />
              We'll notify you about potential matches
            </span>
          </div>
        </div>

        <div className="mt-8 flex w-full flex-col gap-3 sm:flex-row">
          <button
            type="button"
            onClick={() => navigate("/my-reports")}
            className="flex-1 rounded-xl bg-indigo-600 px-5 py-3 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700"
          >
            View My Reports
          </button>
          <button
            type="button"
            onClick={() => navigate("/report-lost-item")}
            className="flex-1 rounded-xl border border-gray-200 bg-white px-5 py-3 text-sm font-semibold text-gray-700 shadow-sm transition-colors hover:bg-gray-50"
          >
            Report Another Item
          </button>
        </div>

        <button
          type="button"
          onClick={() => navigate("/")}
          className="mt-4 flex items-center gap-1.5 text-sm font-medium text-indigo-600 hover:text-indigo-500"
        >
          <ArrowLeft className="h-4 w-4" />
          Back to Dashboard
        </button>

        <p className="mt-6 flex items-center gap-1.5 text-xs text-gray-400">
          <ShieldCheck className="h-3.5 w-3.5" />
          Your report has been securely saved.
        </p>
      </main>
    </div>
  );
}
