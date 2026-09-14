import { apiGet } from "../../../lib/apiClient";

export type ItemType = "LOST" | "FOUND";

export type ItemSummary = {
  id: string;
  itemType: ItemType;
  title: string;
  category: string;
  description: string;
  date: string;       // dateLost or dateFound, yyyy-MM-dd
  location: string;    // lastKnownLocation or locationFound
  status: string;
  photoUrl: string | null;
  createdAt: string;
};

export type PagedResult<T> = {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
};

export type BrowseItemsParams = {
  q?: string;
  category?: string;
  type?: ItemType;
  dateFrom?: string; // yyyy-MM-dd
  dateTo?: string;   // yyyy-MM-dd
  page?: number;
  pageSize?: number;
};

export const browseItems = (
  params: BrowseItemsParams,
  signal?: AbortSignal,
): Promise<PagedResult<ItemSummary>> => {
  const qs = new URLSearchParams();
  if (params.q) qs.set("q", params.q);
  if (params.category) qs.set("category", params.category);
  if (params.type) qs.set("type", params.type);
  if (params.dateFrom) qs.set("dateFrom", params.dateFrom);
  if (params.dateTo) qs.set("dateTo", params.dateTo);
  qs.set("page", String(params.page ?? 1));
  qs.set("pageSize", String(params.pageSize ?? 12));

  return apiGet<PagedResult<ItemSummary>>("items", `/api/items?${qs.toString()}`, signal);
};

export type ItemPhoto = { id: string; url: string };

// Shape returned by GET /api/items/{id} — the public item-details endpoint.
// Never includes hiddenInformation or userId; the backend excludes both.
export type ItemDetail = {
  id: string;
  type: ItemType;
  title: string;
  description: string;
  category: string;
  location: string;   // lastKnownLocation or locationFound
  date: string;        // dateLost or dateFound, yyyy-MM-dd
  status: string;
  photoUrls: string[];
  photo: ItemPhoto | null;
  createdAt: string;
};

export const getItemDetails = (id: string, signal?: AbortSignal): Promise<ItemDetail> =>
  apiGet<ItemDetail>("items", `/api/items/${id}`, signal);