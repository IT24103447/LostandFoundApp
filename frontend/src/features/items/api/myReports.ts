import { apiGet } from "../../../lib/apiClient";
import type { LostItemResponse } from "./reportLostItem";
import type { FoundItemResponse } from "./reportFoundItem";

/**
 * PLACEHOLDER ENDPOINTS.
 * The backend does not yet expose a "list my items" route — only
 * GET /api/items/lost/{id} and GET /api/items/found/{id} exist today.
 * Once you add e.g. GET /api/items/lost/mine and GET /api/items/found/mine
 * on LostItemsController / FoundItemsController, these two calls are the
 * only thing you need to update — nothing in MyReportsPage.tsx has to change.
 */
export const getMyLostItems = (signal?: AbortSignal): Promise<LostItemResponse[]> =>
  apiGet<LostItemResponse[]>("items", "/api/items/lost/mine", signal);

export const getMyFoundItems = (signal?: AbortSignal): Promise<FoundItemResponse[]> =>
  apiGet<FoundItemResponse[]>("items", "/api/items/found/mine", signal);