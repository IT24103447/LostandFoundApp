import { apiGet, apiPut, apiPutForm, apiPostForm, apiDelete } from "../../../lib/apiClient";
import type { ItemPhoto } from "./reportLostItem";

export type UpdateFoundItemPayload = {
  title: string;
  category: string;
  description: string;
  dateFound: string;
  locationFound: string;
  hiddenInformation: string;
};

export const getFoundItem = (id: string, signal?: AbortSignal): Promise<FoundItemResponse> =>
  apiGet<FoundItemResponse>("items", `/api/items/found/${id}`, signal);

export const updateFoundItem = (
  id: string,
  payload: UpdateFoundItemPayload,
  signal?: AbortSignal,
): Promise<FoundItemResponse> =>
  apiPut<UpdateFoundItemPayload, FoundItemResponse>("items", `/api/items/found/${id}`, payload, signal);

export const replaceFoundItemPhoto = (
  id: string,
  file: File,
  signal?: AbortSignal,
): Promise<FoundItemResponse> => {
  const form = new FormData();
  form.append("photo", file);
  return apiPutForm<FoundItemResponse>("items", `/api/items/found/${id}/photo`, form, signal);
};

export const deleteFoundItemPhoto = (
  id: string,
  signal?: AbortSignal,
): Promise<FoundItemResponse> =>
  apiDelete<undefined, FoundItemResponse>("items", `/api/items/found/${id}/photo`, undefined, signal);

export type ReportFoundItemPayload = {
  title: string;
  category: string;
  description: string;
  dateFound: string;
  locationFound: string;
  hiddenInformation: string;
  photos?: File[];
};

// IMPORTANT: there is deliberately no `hiddenInformation` field on this type.
// The backend's FoundItemResponseDto never returns it (see
// FoundItemsController.ToDto on the backend), so it must never be modelled
// here either — that's what keeps it out of every frontend-facing response.
export type FoundItemResponse = {
  id: string;
  userId: string;
  title: string;
  category: string;
  description: string;
  dateFound: string;
  locationFound: string;
  status: string;
  photoUrls: string[];
  photo: ItemPhoto | null;
  createdAt: string;
};

export const reportFoundItem = (
  payload: ReportFoundItemPayload,
  signal?: AbortSignal,
): Promise<FoundItemResponse> => {
  const form = new FormData();
  form.append("Title", payload.title);
  form.append("Category", payload.category);
  form.append("Description", payload.description);
  form.append("DateFound", payload.dateFound);
  form.append("LocationFound", payload.locationFound);
  form.append("HiddenInformation", payload.hiddenInformation);
  if (payload.photos) {
    payload.photos.forEach((file) => form.append("Photos", file));
  }

  return apiPostForm<FoundItemResponse>("items", "/api/items/found", form, signal);
};