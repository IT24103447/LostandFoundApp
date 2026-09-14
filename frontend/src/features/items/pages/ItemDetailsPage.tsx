import { useEffect, useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import {
  MapPin,
  Calendar,
  ShieldCheck,
  Smartphone,
  ShoppingBag,
  Shirt,
  Watch,
  FileText,
  KeyRound,
  Package,
  type LucideIcon,
} from "lucide-react";
import { AppHeader } from "../layout/AppHeader";
import { resolvePhotoUrl } from "../../../config/env";
import { LOST_ITEM_CATEGORIES } from "../schemas/reportLostItemSchema";
import { getItemDetails, type ItemDetail } from "../api/browseItems";

const CATEGORY_ICONS: Record<string, LucideIcon> = {
  Electronics: Smartphone,
  Bags: ShoppingBag,
  Clothing: Shirt,
  Accessories: Watch,
  Documents: FileText,
  Keys: KeyRound,
  Other: Package,
};

function categoryLabel(value: string): string {
  return LOST_ITEM_CATEGORIES.find((c) => c.value === value)?.label ?? value;
}

function categoryIcon(value: string): LucideIcon {
  return CATEGORY_ICONS[value] ?? Package;
}

function formatDisplay(value: string): string {
  const d = new Date(`${value}T00:00:00`);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
}

function statusBadgeClass(status: string): string {
  switch (status) {
    case "ACTIVE":
      return "bg-emerald-50 text-emerald-700";
    case "MATCHED":
      return "bg-blue-50 text-blue-700";
    case "RESOLVED":
    case "CLOSED":
      return "bg-gray-100 text-gray-600";
    default:
      return "bg-gray-100 text-gray-600";
  }
}

export function ItemDetailsPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const [item, setItem] = useState<ItemDetail | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [activePhotoIndex, setActivePhotoIndex] = useState(0);

  useEffect(() => {
    if (!id) return;
    const controller = new AbortController();
    setLoading(true);
    setLoadError(null);

    getItemDetails(id, controller.signal)
      .then((data) => {
        setItem(data);
        setActivePhotoIndex(0);
      })
      .catch((err) => {
        if (err instanceof DOMException && err.name === "AbortError") return;
        const status = (err as { status?: number }).status;
        setLoadError(
          status === 404
            ? "This item couldn't be found. It may have been removed or resolved."
            : "Something went wrong loading this item. Please try again.",
        );
      })
      .finally(() => setLoading(false));

    return () => controller.abort();
  }, [id]);

  if (loading) {
    return (
      <div className="min-h-screen bg-[#FAFAFC]">
        <AppHeader />
        <div className="mx-auto max-w-3xl px-6 py-16 text-center text-gray-500">Loading…</div>
      </div>
    );
  }

  if (loadError || !item) {
    return (
      <div className="min-h-screen bg-[#FAFAFC]">
        <AppHeader />
        <div className="mx-auto max-w-3xl px-6 py-16 text-center">
          <p className="text-gray-600">{loadError ?? "This item couldn't be found."}</p>
          <button
            type="button"
            onClick={() => navigate(-1)}
            className="mt-4 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-indigo-700"
          >
            Back to Results
          </button>
        </div>
      </div>
    );
  }

  const isLost = item.type === "LOST";
  const PlaceholderIcon = categoryIcon(item.category);
  const photos = item.photoUrls;
  const activePhoto = photos[activePhotoIndex] ?? photos[0] ?? null;

  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />

      <div className="mx-auto max-w-6xl px-6 py-10">
        <nav className="mb-6 flex items-center gap-2 text-sm">
          {/* PLACEHOLDER: points home until the Browse Items page story lands,
              same convention as the "Browse Now" link in MyReportsPage.tsx. */}
          <Link to="/" className="text-gray-500 hover:text-gray-700">
            Browse Items
          </Link>
          <span className="text-gray-300">/</span>
          <span className="font-medium text-gray-900">{item.title}</span>
        </nav>

        <div className="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_420px]">
          {/* Photo panel */}
          <div className="rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
            <div className="flex aspect-[4/3] w-full items-center justify-center overflow-hidden rounded-xl bg-gray-50">
              {activePhoto ? (
                <img
                  src={resolvePhotoUrl(activePhoto)}
                  alt={item.title}
                  className="h-full w-full object-contain"
                />
              ) : (
                <div className="flex flex-col items-center gap-3 text-gray-400">
                  <div className="flex h-16 w-16 items-center justify-center rounded-full border-2 border-dashed border-indigo-200 bg-white/70">
                    <PlaceholderIcon className="h-7 w-7 text-indigo-300" strokeWidth={1.5} />
                  </div>
                  <span className="text-xs font-medium">No photo added</span>
                </div>
              )}
            </div>

            {photos.length > 1 && (
              <div className="mt-4 grid grid-cols-4 gap-3 sm:grid-cols-6">
                {photos.map((url, idx) => (
                  <button
                    key={url}
                    type="button"
                    onClick={() => setActivePhotoIndex(idx)}
                    className={`aspect-square overflow-hidden rounded-lg border-2 bg-gray-50 transition-colors ${
                      idx === activePhotoIndex ? "border-indigo-600" : "border-transparent hover:border-gray-300"
                    }`}
                  >
                    <img
                      src={resolvePhotoUrl(url)}
                      alt={`${item.title} photo ${idx + 1}`}
                      className="h-full w-full object-cover"
                    />
                  </button>
                ))}
              </div>
            )}
          </div>

          {/* Details panel */}
          <div className="relative rounded-2xl border border-gray-200 bg-white p-6 shadow-sm">
            <span
              className={`absolute right-6 top-6 rounded-full px-3 py-1 text-xs font-bold ${
                isLost ? "bg-indigo-100 text-indigo-700" : "bg-orange-100 text-orange-700"
              }`}
            >
              {item.type}
            </span>

            <h1 className="pr-20 text-2xl font-extrabold leading-tight text-gray-900">{item.title}</h1>

            <div className="mt-2 flex items-center gap-3">
              <span className="text-sm text-gray-500">{categoryLabel(item.category)}</span>
              <span className={`rounded-full px-2.5 py-1 text-xs font-bold ${statusBadgeClass(item.status)}`}>
                {item.status}
              </span>
            </div>

            <p className="mt-5 text-[15px] leading-relaxed text-gray-700">{item.description}</p>

            <div className="mt-6 border-t border-gray-100 pt-5">
              <h2 className="text-xs font-semibold uppercase tracking-wide text-gray-400">When &amp; Where</h2>

              <div className="mt-3 flex items-start gap-2.5 text-[15px] text-gray-700">
                <Calendar className="mt-0.5 h-4 w-4 shrink-0 text-gray-400" />
                <span>
                  <span className="font-semibold text-gray-900">{isLost ? "Lost:" : "Found:"}</span>{" "}
                  {formatDisplay(item.date)}
                </span>
              </div>

              <div className="mt-2 flex items-start gap-2.5 text-[15px] text-gray-700">
                <MapPin className="mt-0.5 h-4 w-4 shrink-0 text-gray-400" />
                <span>
                  <span className="font-semibold text-gray-900">Location:</span> {item.location}
                </span>
              </div>
            </div>

            <div className="mt-6 space-y-3 border-t border-gray-100 pt-5">
              {/* TODO: wire to the ownership-verification / matching flow once that story exists */}
              <button
                type="button"
                className="w-full rounded-xl bg-indigo-600 px-5 py-3 text-[15px] font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700"
              >
                {isLost ? "I Found This Item" : "This Is My Item"}
              </button>

              <button
                type="button"
                onClick={() => navigate(-1)}
                className="w-full rounded-xl bg-indigo-50 px-5 py-3 text-[15px] font-semibold text-indigo-700 transition-colors hover:bg-indigo-100"
              >
                Back to Results
              </button>
            </div>

            <div className="mt-5 flex items-start gap-3 rounded-xl bg-gray-50 p-4">
              <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-indigo-100">
                <ShieldCheck className="h-4 w-4 text-indigo-600" />
              </span>
              <p className="text-sm text-gray-600">
                <span className="font-semibold text-gray-900">Think this is your item?</span> Use the private
                verification process to confirm ownership.
              </p>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}