import { MapPin, Smartphone, ShoppingBag, Shirt, Watch, FileText, KeyRound, Package, type LucideIcon } from "lucide-react";
import { resolvePhotoUrl } from "../../../config/env";
import type { ItemSummary } from "../../items/api/browseItems";
import { LOST_ITEM_CATEGORIES } from "../../items/schemas/reportLostItemSchema";

function formatDisplay(value: string): string {
  const d = new Date(`${value}T00:00:00`);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

function categoryLabel(value: string): string {
  return LOST_ITEM_CATEGORIES.find((c) => c.value === value)?.label ?? value;
}

const CATEGORY_ICONS: Record<string, LucideIcon> = {
  Electronics: Smartphone,
  Bags: ShoppingBag,
  Clothing: Shirt,
  Accessories: Watch,
  Documents: FileText,
  Keys: KeyRound,
  Other: Package,
};

function categoryIcon(value: string): LucideIcon {
  return CATEGORY_ICONS[value] ?? Package;
}

export function ItemCard({ item }: { item: ItemSummary }) {
  const isLost = item.itemType === "LOST";
  const PlaceholderIcon = categoryIcon(item.category);

  return (
    <div className="overflow-hidden rounded-2xl border border-gray-200 bg-white shadow-sm transition-shadow hover:shadow-md">
      <div className="relative aspect-[4/3] w-full bg-gray-900">
        {item.photoUrl ? (
          <img
            src={resolvePhotoUrl(item.photoUrl)}
            alt={item.title}
            className="h-full w-full object-cover"
          />
        ) : (
          <div className="flex h-full w-full flex-col items-center justify-center gap-3 bg-gradient-to-br from-indigo-50 to-gray-100">
            <div className="flex h-16 w-16 items-center justify-center rounded-full border-2 border-dashed border-indigo-200 bg-white/70">
              <PlaceholderIcon className="h-7 w-7 text-indigo-300" strokeWidth={1.5} />
            </div>
            <span className="text-xs font-medium text-gray-400">No photo added</span>
          </div>
        )}
        <span
          className={`absolute right-3 top-3 rounded-full px-3 py-1 text-xs font-bold text-white shadow ${
            isLost ? "bg-indigo-600" : "bg-orange-500"
          }`}
        >
          {isLost ? "LOST" : "FOUND"}
        </span>
      </div>

      <div className="p-4">
        <h3 className="truncate text-[15px] font-bold text-gray-900">{item.title}</h3>
        <p className="mt-0.5 text-sm text-gray-500">{categoryLabel(item.category)}</p>
        <p className="mt-2 line-clamp-2 text-sm text-gray-600">{item.description}</p>

        <div className="mt-3 flex items-center justify-between text-sm">
          <span className="flex min-w-0 items-center gap-1.5 text-gray-500">
            <MapPin className="h-4 w-4 shrink-0" />
            <span className="truncate">{item.location}</span>
          </span>
          <span className="shrink-0 rounded-full bg-indigo-50 px-2.5 py-1 text-xs font-semibold text-indigo-700">
            {item.status}
          </span>
        </div>

        <div className="mt-3 flex items-center justify-between border-t border-gray-100 pt-3 text-sm">
          <span className="flex items-center gap-1.5 text-gray-500">
            <CalendarIcon />
            {formatDisplay(item.date)}
          </span>
          {/* TODO: wire to an item-detail page once that story exists */}
          <span className="font-medium text-indigo-600">View Details →</span>
        </div>
      </div>
    </div>
  );
}

function CalendarIcon() {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      className="h-4 w-4"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <rect x="3" y="4" width="18" height="18" rx="2" />
      <line x1="16" y1="2" x2="16" y2="6" />
      <line x1="8" y1="2" x2="8" y2="6" />
      <line x1="3" y1="10" x2="21" y2="10" />
    </svg>
  );
}